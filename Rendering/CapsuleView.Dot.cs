using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Payload;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// DeepSeek / OpenCode 胶囊：外圆环 + 内圆点（"◉" 形式）+ 文本。
///
/// 视觉规则：
/// - 圆点默认按风险档位（绿/黄/橙/红）静态显示。
/// - 高峰期（UTC+8 9-12 / 14-18）圆点强制变橙色并呼吸（Opacity 1.0 → 0.35，1.6s 周期）。
/// - 非高峰期圆点不呼吸（Opacity 固定 1.0）。
///
/// 设计原则：与 BalanceRingCapsuleView 完全独立 —— 不继承、不共享指示器字段、
/// 不共享动画常量。圆环由本类自己实例化持有，呼吸动画对象也独立维护，
/// 改动本胶囊的圆点 / 呼吸规则不会牵连 MiniMax 胶囊，反之亦然。
///
/// 性能契约：
/// - 呼吸动画只动 Ellipse.Opacity，不解冻 Ellipse.Fill（Brush 仍冻结）。
/// - ToBrush 返回冻结 Brush，跨线程共享。
/// </summary>
internal sealed class BalanceDotCapsuleView : Grid
{
    private readonly TextBlock _label;
    private readonly BalanceProgressRing _ring;
    // 内圆点：6×6 DIP，居中叠在圆环上。
    private readonly Ellipse _dot;

    // 高峰期橙色常量：#FF9800 与 MiniMax 5h 进度条同源色。
    private const string PeakDotColorHex = "#FF9800";

    // 圆环 + 圆点列可见性缓存：防止外部反复调用 SetRingVisible 时重写 Column 宽度。
    private bool _ringColumnVisible = true;

    // 高峰期圆点呼吸动效开关：默认 true；disableDotBreath=true 时改为静态显示,
    // 即使 isPeakHour=true 也不再触发 Opacity 动画。
    private bool _dotBreathEnabled = true;

    // 呼吸动画只读一份，所有 BalanceDotCapsuleView 实例共享（Freeze 后线程安全）。
    private static readonly DoubleAnimation DotBreathAnimation = CreateBreathAnimation();

    // FontFamily 缓存：theme.FontFamily 是字符串，按字符串相等判断避免重复构造。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    public BalanceDotCapsuleView(PaperCapsuleViewContext context)
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 5 列布局：[6 pad][18 ring+dot][5 gap][* text][4 right pad]
        // 圆环与圆点共用第 1 列（18 DIP 宽），居中叠加。
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

        _ring = new BalanceProgressRing
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(_ring, 1);
        Children.Add(_ring);

        _dot = new Ellipse
        {
            // 6×6 DIP：缩到圆环直径（18）的 1/3，让位给外环轮廓，避免视觉上压过 track。
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Visible
        };
        Grid.SetColumn(_dot, 1);
        Children.Add(_dot);

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left
        };
        Grid.SetColumn(_label, 3);
        Children.Add(_label);

        // spinner overlay 已改为"替换模式"(改 _label.Text + _dot.Fill),
        // 不再需要 _overlayLayer 覆盖层与 BuildOverlayLayer。

        ApplyTheme(context.Theme);
    }

    // ----- Overlay 渲染（hook 触发时临时覆盖胶囊） -----
    // spinner 蓝(#2196F3):冻结后跨帧复用,渲染期不再做线程检查;
    // 提取为静态字段,允许多实例共用同一 frozen 实例。
    private static readonly SolidColorBrush SpinnerBadgeBrush = CreateFrozenSpinnerBrush();
    private static SolidColorBrush CreateFrozenSpinnerBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        brush.Freeze();
        return brush;
    }
    private DispatcherTimer? _overlayCountdown;
    // Color / Spinner overlay 共用暂存字段:倒计时恢复或 HideHookOverlay 时用
    private string? _storedLabelText;
    private string? _storedDotFillHex;

    /// <summary>
    /// 设置 hook overlay：用 WPF 原生 ring+text 实现 PNG 描述的效果。
    /// - Color overlay (Stop/Permission/Failure):改 _label.Text + _dot.Fill,带倒计时恢复
    /// - Spinner overlay (PreTool/PostTool):改 _label.Text + _dot.Fill 为蓝色,持续到下次 Update
    /// </summary>
    public void SetHookOverlay(HookOverlayKind kind, int durationSeconds, string pluginDir)
    {
        _overlayCountdown?.Stop();
        _overlayCountdown = null;
        RestoreColorOverlayIfAny();

        if (kind == HookOverlayKind.None)
        {
            return;
        }

        if (kind == HookOverlayKind.StopImage)
        {
            ShowColorOverlay("任务完成", "#4CAF50", durationSeconds);
        }
        else if (kind == HookOverlayKind.PermissionImage)
        {
            ShowColorOverlay("等待回复", "#FF9800", durationSeconds);
        }
        else if (kind == HookOverlayKind.FailureImage)
        {
            ShowColorOverlay("执行异常", "#F44336", durationSeconds);
        }
        else if (kind == HookOverlayKind.PreToolSpinner)
        {
            ShowSpinnerOverlay("准备调用工具");
        }
        else if (kind == HookOverlayKind.PostToolSpinner)
        {
            ShowSpinnerOverlay("文件编辑完成");
        }
    }

    /// <summary>
    /// Color overlay（PNG 描述的固定状态）：改 _label.Text + _dot.Fill。
    /// 倒计时后由 RestoreColorOverlayIfAny 恢复。
    /// </summary>
    private void ShowColorOverlay(string text, string dotColorHex, int durationSeconds)
    {
        _storedLabelText = _label.Text;
        _storedDotFillHex = _dot.Fill is SolidColorBrush sb ? sb.Color.ToString() : null;
        _label.Text = text;
        _dot.Fill = Format.ToFrozenBrush(dotColorHex, "#9E9E9E");
        _overlayCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        _overlayCountdown.Tick += (_, _) =>
        {
            _overlayCountdown?.Stop();
            _overlayCountdown = null;
            RestoreColorOverlayIfAny();
        };
        _overlayCountdown.Start();
    }

    /// <summary>恢复暂存的 _label.Text / _dot.Fill；未暂存则不动。</summary>
    private void RestoreColorOverlayIfAny()
    {
        if (_storedLabelText != null)
        {
            _label.Text = _storedLabelText;
            _storedLabelText = null;
        }
        if (_storedDotFillHex != null)
        {
            _dot.Fill = Format.ToFrozenBrush(_storedDotFillHex, "#9E9E9E");
            _storedDotFillHex = null;
        }
    }

    private void ShowSpinnerOverlay(string text)
    {
        // Spinner overlay（替换模式）：与 ShowColorOverlay 共用 RestoreColorOverlayIfAny 路径。
        // - 暂存 _label.Text / _dot 当前填充色
        // - 把 _label.Text 替换为 spinner 文本,_dot.Fill 替换为 spinner 蓝(#2196F3)
        // - 圆环保持 Brushes.Transparent(Dot 视图圆环始终为轮廓底色,不显示 spinner 颜色弧)
        // 不创建覆盖层、不旋转沙漏、不带倒计时。
        _storedLabelText = _label.Text;
        _storedDotFillHex = _dot.Fill is SolidColorBrush sb ? sb.Color.ToString() : null;

        _label.Text = text;
        _dot.Fill = SpinnerBadgeBrush;
        // 圆环保持 Brushes.Transparent(与 Update 路径一致),不动 _ring.ForegroundBrush / _ring.Value。
    }

    /// <summary>外部在 Update() 时清掉 spinner overlay(spinner 是"持续型")。</summary>
    public void HideHookOverlay()
    {
        _overlayCountdown?.Stop();
        _overlayCountdown = null;
        // spinner 期间改动的 _label.Text / _dot.Fill 由 RestoreColorOverlayIfAny 恢复。
        RestoreColorOverlayIfAny();
    }

    private static DoubleAnimation CreateBreathAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = new Duration(TimeSpan.FromSeconds(0.8)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Freeze();
        return anim;
    }

    /// <summary>
    /// 刷新胶囊文本 / 圆环 / 圆点。
    /// - ringColorHex / ringArc：本胶囊不消费颜色（外环保持 track 底色，仅作轮廓），
    ///                      但参数仍保留以保持调用方签名统一。
    /// - dotColorHex：圆点默认颜色（按风险档位：绿/黄/橙/红）。
    /// - isPeakHour=true：圆点变橙色并呼吸；false：按 dotColorHex 静态显示。
    /// </summary>
    public void Update(
        string text,
        string ringColorHex,
        double ringArc,
        string dotColorHex,
        bool isPeakHour)
    {
        var textChanged = _label.Text != text;
        _label.Text = text;

        // 圆环：仅作轮廓底色，不显示风险颜色弧。ForegroundBrush 用 Brushes.Transparent，
        // 让 OnRender 只画 track（TrackBrush 由 ApplyTheme 注入），弧段自然透明。
        // Arc 值仍可保留（避免 0/1 抖动），但视觉上不出现。
        _ring.Value = Math.Clamp(ringArc, 0, 1);
        _ring.ForegroundBrush = Brushes.Transparent;
        _ring.InvalidateVisual();

        // 圆点：高峰期强制橙色 + 呼吸；其他时段按 dotColorHex 静态。
        // 呼吸条件 = isPeakHour && _ringColumnVisible && _dotBreathEnabled。
        // 关闭圆环时整个圆点列不可见，禁止触发动画（避免不可见时还在跑 Opacity 动画）；
        // 关闭呼吸动效（disableDotBreath）时即使在高峰期也保持静态。
        var dotColor = isPeakHour ? PeakDotColorHex : dotColorHex;
        _dot.Fill = Format.ToFrozenBrush(dotColor, "#9E9E9E");
        if (isPeakHour && _ringColumnVisible && _dotBreathEnabled)
        {
            _dot.BeginAnimation(UIElement.OpacityProperty, DotBreathAnimation);
        }
        else
        {
            // 清掉动画引用，Opacity 回到默认 1.0（静态）。
            _dot.BeginAnimation(UIElement.OpacityProperty, null);
            _dot.Opacity = 1.0;
        }

        // 文字真正变化时触发淡入；polling tick 不变 text 则不闪。
        // spinner overlay 改用替换模式后不再有 _overlayLayer / _overlaySpinner* 状态可检测,
        // 清理由 BalanceSession.UpdateSnapshot spinner 分支显式触发(Ring + Dot 双视图)。
        if (textChanged)
        {
            _label.Opacity = 0;
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _label.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    /// <summary>
    /// 主题切换：文本字体 / 字号 / 颜色，圆环底色（近似 Theme.Tint(38)）。
    /// 字号取 12 × FontScale，与宿主默认 CapsuleTextSize=Medium + AppTypography.Scale 一致。
    /// </summary>
    public void ApplyTheme(PaperBodyTheme theme)
    {
        var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
        if (_cachedFontFamilySource != theme.FontFamily || _cachedFontFamily == null)
        {
            _cachedFontFamily = new FontFamily(theme.FontFamily);
            _cachedFontFamilySource = theme.FontFamily;
        }
        _label.FontFamily = _cachedFontFamily;
        _label.FontSize = 12.0 * scale;
        _label.FontWeight = FontWeights.Normal;
        _label.Foreground = Format.ToFrozenBrush(theme.WeakTextColor, "#707070");

        // 外圆环底色：浅白色（alpha ≈ 70/255 ≈ 27%），弱视觉锚点。深色主题下清晰可见，
        // 浅色主题下与背景融为一体只剩微弱轮廓，符合"外环不需要颜色、仅作衬托"的设计意图。
        var track = new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xFF, 0xFF));
        track.Freeze();
        _ring.TrackBrush = track;
        _ring.InvalidateVisual();
    }

    /// <summary>
    /// 切换圆环 + 圆点可见性（设置 disableRing 时调用）。
    /// 关闭时圆环列 + gap 列折叠为 0 宽，圆点列也归 0；切回时恢复原宽度。
    /// 关闭状态下即使 isPeakHour=true 也不再触发呼吸动画（避免残留闪烁）。
    /// </summary>
    public void SetRingVisible(bool visible)
    {
        _ringColumnVisible = visible;
        // 5 列布局与 Ring 胶囊共用列宽规则：[6 pad][18 ring+dot][5 gap][* text][4 right pad]
        ColumnDefinitions[1].Width = visible
            ? new GridLength(18)
            : new GridLength(0);
        ColumnDefinitions[2].Width = visible
            ? new GridLength(5)
            : new GridLength(0);
        _ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _dot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            // 关闭后清掉任何残留的呼吸动画,避免不可见时还在跑 Opacity 动画。
            _dot.BeginAnimation(UIElement.OpacityProperty, null);
            _dot.Opacity = 1.0;
        }
        // 修复 D：列宽变了，文字可显示宽度变了，强制重测避免一帧截断。
        _label.InvalidateMeasure();
    }

    /// <summary>
    /// 切换圆点呼吸动效（设置 disableDotBreath 时调用）。
    /// 关闭后圆点在高峰期不再触发 Opacity 动画，保持静态显示；
    /// 切回开启后下次 Update 在 isPeakHour=true 时会重新触发动画。
    /// </summary>
    public void SetDotBreathEnabled(bool enabled)
    {
        _dotBreathEnabled = enabled;
        if (!enabled)
        {
            // 立即清掉可能正在跑的呼吸动画,Opacity 回归 1.0 静态显示。
            _dot.BeginAnimation(UIElement.OpacityProperty, null);
            _dot.Opacity = 1.0;
        }
    }
}