using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Payload;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;

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
    // Permission overlay 黄色问号覆盖层：叠在内圆点上,默认隐藏。
    // Permission 触发时显示并隐藏 _dot,RestoreColorOverlayIfAny 反向恢复。
    private readonly TextBlock _questionGlyph;
    // HookGlyphKind enum 统一在 Services/HookOverlayPlan.cs 定义。Dot 视图用它判断是否
    // 切换 _questionGlyph 显示(PermissionImage → Question,其它隐藏)。

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

        // Permission 黄色问号覆盖层：放在 Column 1 居中位置,与 _dot 重叠。
        // 字号 14 比 _dot 直径(6)大,视觉替换圆点(隐藏 _dot)。Panel.ZIndex=1 叠在圆点上方。
        // 默认隐藏,由 ShowColorOverlay(glyph=Question) 触发显示。
        _questionGlyph = new TextBlock
        {
            Text = "?",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = PermissionGlyphBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Focusable = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_questionGlyph, 1);
        Panel.SetZIndex(_questionGlyph, 1);
        Children.Add(_questionGlyph);

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
    // Permission 黄色(#FFC107 Material Amber 500):与 Ring 视图 PermissionGlyphBrush 同色,
    // 保证两个胶囊在等待用户回应时视觉一致。各文件独立声明是因为 Ring / Dot 类不继承。
    private static readonly SolidColorBrush PermissionGlyphBrush = CreateFrozenPermissionBrush();
    private static SolidColorBrush CreateFrozenPermissionBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        brush.Freeze();
        return brush;
    }
    private DispatcherTimer? _overlayCountdown;
    // Color / Spinner overlay 共用暂存字段:倒计时恢复或 HideHookOverlay 时用
    private string? _storedLabelText;
    private string? _storedDotFillHex;
    // 内圆点 Visibility 暂存:Permission overlay 把 _dot 隐藏换成 _questionGlyph,
    // RestoreColorOverlayIfAny 据此恢复 _dot 显示状态(Visible / Hidden)。
    private Visibility? _storedDotVisibility;

    /// <summary>
    /// 数据驱动接口:接收 <see cref="HookOverlayPlan"/> 渲染 overlay。plan == null 等价于 ClearOverlay。
    /// 由 HookOverlayController 派发。
    /// </summary>
    public void PushOverlay(HookOverlayPlan? plan)
    {
        // 与 Ring 同步:完整清理在飞的 fade / countdown,避免 spinner 与 color overlay 动画并存。
        // WPF DispatcherTimer.Stop() 不取消已 Post 入队的 Tick,必须显式退订 handler。
        _overlayCountdown?.Stop();
        _overlayCountdown?.Tick -= OnCountdownTick;
        _overlayCountdown = null;
        // 打断 _label.OpacityProperty 的 fade 动画,避免 spinner text 被残留的透明度拖到看不见。
        _label.BeginAnimation(UIElement.OpacityProperty, null);
        _label.Opacity = 1;
        RestoreColorOverlayIfAny();

        if (plan == null || plan.Kind == HookOverlayKind.None)
        {
            return;
        }

        if (plan.Kind is HookOverlayKind.StopImage
            or HookOverlayKind.PermissionImage
            or HookOverlayKind.FailureImage)
        {
            // Color overlay:Color 走 Dot 视图的固定色系(注:Dot 不画对勾/问号细节,glyph 字段此处不消费)。
            var dotColor = plan.Kind switch
            {
                HookOverlayKind.StopImage => "#4CAF50",
                HookOverlayKind.PermissionImage => "#FFC107",
                HookOverlayKind.FailureImage => "#F44336",
                _ => plan.RingColorHex ?? "#9E9E9E"
            };
            ShowColorOverlay(plan.Text, dotColor, plan.DurationSeconds ?? 3, HookGlyphKind.None);
        }
        else
        {
            // spinner 路径:文本已由 Controller 派生(已含 tool-aware + 兜底)。
            ShowSpinnerOverlay(plan.Text);
        }
    }

    /// <summary>
    /// Color overlay（PNG 描述的固定状态）：改 _label.Text + _dot.Fill。
    /// 倒计时后由 RestoreColorOverlayIfAny 恢复。
    ///
    /// glyph 决定是否叠加问号 glyph:Question 时同时隐藏 _dot、显示 _questionGlyph,
    /// 视觉上用问号替代内圆点;Check 时只改 _dot.Fill(无 glyph 改动)。
    /// </summary>
    private void ShowColorOverlay(string text, string dotColorHex, int durationSeconds, HookGlyphKind glyph)
    {
        _storedLabelText = _label.Text;
        _storedDotFillHex = _dot.Fill is SolidColorBrush sb ? sb.Color.ToString() : null;
        // 暂存 _dot.Visibility,RestoreColorOverlayIfAny 据此恢复显示/隐藏。
        _storedDotVisibility = _dot.Visibility;
        _label.Text = text;
        _dot.Fill = Format.ToFrozenBrush(dotColorHex, "#9E9E9E");
        // glyph 直接用入参判断,不再保存到字段(Controller 是单一真相源)。
        if (glyph == HookGlyphKind.Question)
        {
            // 用问号 glyph 替换内圆点:字号 14 vs _dot 直径 6,视觉占主导。
            _dot.Visibility = Visibility.Collapsed;
            _questionGlyph.Visibility = Visibility.Visible;
        }
        _overlayCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        _overlayCountdown.Tick += OnCountdownTick;
        _overlayCountdown.Start();
    }

    /// <summary>_overlayCountdown Tick 实例方法:替换原 lambda,方便 SetHookOverlay 入口显式退订。
    /// 退订是必需的——DispatcherTimer.Stop() 不取消已 Post 入队的 Tick handler,
    /// 否则残留 handler 会把刚切换的 spinner 状态又恢复成 Color overlay。</summary>
    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _overlayCountdown?.Stop();
        _overlayCountdown = null;
        RestoreColorOverlayIfAny();
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
        // 恢复内圆点 + 隐藏问号 glyph(若上次是 Permission 触发)
        if (_questionGlyph.Visibility == Visibility.Visible)
        {
            _questionGlyph.Visibility = Visibility.Collapsed;
        }
        if (_storedDotVisibility.HasValue)
        {
            _dot.Visibility = _storedDotVisibility.Value;
            _storedDotVisibility = null;
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
    public void ClearOverlay()
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