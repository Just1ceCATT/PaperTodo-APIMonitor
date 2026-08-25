using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// MiniMax 胶囊：纯圆环进度指示 + 文本。不含内圆点 / 呼吸动画。
///
/// 设计原则：与 BalanceDotCapsuleView 完全独立 —— 不继承、不共享指示器字段、
/// 不共享动画常量。改动 MiniMax 圆环不会牵连 DeepSeek 胶囊，反之亦然。
///
/// 性能契约:
/// - Pen / StreamGeometry 由 BalanceProgressRing 内部缓存并 Freeze。
/// - 所有 Brush 由 ToBrush 返回冻结实例，跨线程共享。
/// </summary>
internal sealed class BalanceRingCapsuleView : Grid
{
    private readonly TextBlock _label;
    private readonly BalanceProgressRing _ring;
    // 圆环列可见性缓存:防止外部反复调用 SetRingVisible 时重写 Column 宽度。
    private bool _ringColumnVisible = true;

    // FontFamily 缓存：theme.FontFamily 是字符串，按字符串相等判断避免重复构造。
    // 避免每次 ApplyTheme 都触发 WPF 字体回退链解析（首次解析可达 100ms 级）。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    public BalanceRingCapsuleView(PaperCapsuleViewContext context)
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 5 列布局：[6 pad][18 ring][5 gap][* text][4 right pad]
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

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left
        };
        Grid.SetColumn(_label, 3);
        Children.Add(_label);

        // Overlay 层：跨 5 列覆盖在 ring/label 之上，hook 触发时显示。
        BuildOverlayLayer();

        ApplyTheme(context.Theme);
    }

    // ----- Overlay 渲染（hook 触发时临时覆盖胶囊） -----
    private readonly Grid _overlayLayer = new();
    private readonly Border _overlaySpinnerBadge = new();
    private readonly TextBlock _overlaySpinnerText = new();
    private readonly RotateTransform _overlaySpinnerRotate = new();
    private readonly DoubleAnimation _overlaySpinnerAnim = new();
    // spinner badge 蓝色背景:冻结后跨帧复用,渲染期不再做线程检查;
    // 提取为静态字段,允许多实例共用同一 frozen 实例。
    private static readonly SolidColorBrush SpinnerBadgeBrush = CreateFrozenSpinnerBrush();
    private static SolidColorBrush CreateFrozenSpinnerBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        brush.Freeze();
        return brush;
    }
    private DispatcherTimer? _overlayCountdown;
    // Color overlay 缓存原始值,倒计时恢复时用
    private string? _storedLabelText;
    private string? _storedRingColorHex;
    private double _storedRingArc;
    // Update 节流字段:仅当颜色字符串/弧值真正变化时才覆盖/重绘
    private string? _lastRingColorHex;
    private double _lastAppliedArc = -1.0;
    // 复用 DispatcherTimer 实例:fade-in / countdown timer 不再每次 new,
    // Tick handler 用实例方法代替 lambda,避免高频 hook 事件下的 GC 分配。
    private readonly DispatcherTimer _fadeInTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string? _pendingFadeInText;

    private void BuildOverlayLayer()
    {
        _overlayLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _overlayLayer.VerticalAlignment = VerticalAlignment.Stretch;
        _overlayLayer.IsHitTestVisible = false;
        _overlayLayer.Background = Brushes.Transparent;
        _overlayLayer.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(_overlayLayer, 5);
        Children.Add(_overlayLayer);

        _overlaySpinnerRotate.Angle = 0;
        _overlaySpinnerAnim.From = 0;
        _overlaySpinnerAnim.To = 360;
        _overlaySpinnerAnim.Duration = TimeSpan.FromSeconds(1.5);
        _overlaySpinnerAnim.RepeatBehavior = RepeatBehavior.Forever;
        // 线性匀速:原 SineEase EaseInOut + Forever 在每圈两端速度归零,看起来每 1.5s 停顿一帧;
        // spinner 的视觉惯例是匀速旋转,这里移除缓动。
        _overlaySpinnerAnim.EasingFunction = null;
        _overlaySpinnerBadge.Width = 14;
        _overlaySpinnerBadge.Height = 14;
        _overlaySpinnerBadge.CornerRadius = new CornerRadius(2);
        _overlaySpinnerBadge.Background = SpinnerBadgeBrush;
        _overlaySpinnerBadge.HorizontalAlignment = HorizontalAlignment.Left;
        _overlaySpinnerBadge.VerticalAlignment = VerticalAlignment.Center;
        _overlaySpinnerBadge.RenderTransform = _overlaySpinnerRotate;
        _overlaySpinnerBadge.RenderTransformOrigin = new Point(0.5, 0.5);
        _overlaySpinnerBadge.Child = BuildHourglassGlyph();
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;

        _overlaySpinnerText.Margin = new Thickness(20, 0, 0, 0);
        _overlaySpinnerText.VerticalAlignment = VerticalAlignment.Center;
        _overlaySpinnerText.FontSize = 12.0;
        _overlaySpinnerText.FontWeight = FontWeights.Medium;
        _overlaySpinnerText.Foreground = ToBrush("#2196F3", "#707070");
        _overlaySpinnerText.Visibility = Visibility.Collapsed;

        var spinnerRow = new Grid();
        spinnerRow.Children.Add(_overlaySpinnerBadge);
        spinnerRow.Children.Add(_overlaySpinnerText);
        _overlayLayer.Children.Add(spinnerRow);
    }

    /// <summary>蓝色沙漏图形：上三角 + 下三角。</summary>
    private static FrameworkElement BuildHourglassGlyph()
    {
        var canvas = new Canvas { Width = 14, Height = 14, Background = Brushes.Transparent };
        var top = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2,1 L 12,1 L 7,7 Z"),
            Fill = Brushes.White
        };
        var bottom = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 7,7 L 12,13 L 2,13 Z"),
            Fill = Brushes.White
        };
        canvas.Children.Add(top);
        canvas.Children.Add(bottom);
        return canvas;
    }

    /// <summary>
    /// 设置 hook overlay：用 WPF 原生控件实现 PNG 描述的效果,不是直接显示 PNG。
    /// - Color overlay (Stop/Permission/Failure):改 _label.Text + _ring 颜色,带倒计时自动恢复
    /// - Spinner overlay (PreTool/PostTool):蓝色沙漏 + 固定文本,持续到下次 Update()
    /// </summary>
    public void SetHookOverlay(HookOverlayKind kind, int durationSeconds, string pluginDir)
    {
        // 先清掉旧倒计时与动画
        StopOverlayCountdown();
        _fadeInTimer.Stop();
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;
        _overlaySpinnerText.Visibility = Visibility.Collapsed;

        // 恢复上一次 color overlay 的暂存值(防止叠加)
        RestoreColorOverlayIfAny();

        if (kind == HookOverlayKind.None)
        {
            _overlayLayer.Visibility = Visibility.Collapsed;
            return;
        }

        // 颜色方案:参考 HTML 示范用 #68E534(鲜艳绿)。Permission/Failure 仍按现有色系保留风险辨识度。
        if (kind == HookOverlayKind.StopImage)
        {
            ShowColorOverlay("任务完成", "#68E534", durationSeconds);
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
        PaperTodo.Plugin.ApiBalanceMonitor.Services.HookTrace.Write(
            $"SetHookOverlay kind={kind} dur={durationSeconds}s label='{_label.Text}'");
    }

    /// <summary>
    /// 临时修改 _label.Text + _ring 颜色,显示 hook 状态。
    /// 倒计时后由 RestoreColorOverlayIfAny 恢复。
    ///
    /// 动画时序参考"圆环 ease-in-out 闭合 → 对勾 ease-out 描边 → 文本 ease-in-out 淡入"的 CSS 成功动画节奏:
    ///   - 圆环闭合 fill: 400ms (BeginCheckmarkAnimation 内置,ease-in-out)
    ///   - 对勾描边 stroke: BeginTime = 350ms, Duration = 350ms (ease-out)
    ///   - 文本淡入 fade-in: BeginTime = 500ms, Duration = 350ms (ease-in-out)
    ///   - 总时长 ~850ms
    /// </summary>
    private void ShowColorOverlay(string text, string ringColorHex, int durationSeconds)
    {
        _storedLabelText = _label.Text;
        _storedRingColorHex = _ring.ForegroundBrush is SolidColorBrush sb ? sb.Color.ToString() : null;
        _storedRingArc = _ring.Value;
        // 字符淡出淡入:先把 _label 隐藏,然后延迟显示文本并触发 fade-in。
        _label.Text = "";
        _label.Opacity = 0;
        // 设置圆环颜色 + 重置对勾状态;BeginCheckmarkAnimation 内部从当前 Value 插值到 1.0(ease-in-out),
        // 圆环会"生长"到满,不再像之前那样先 Value=1.0 后跳过 fill 动画。
        _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
        _ring.ResetCheckmark();
        _label.InvalidateMeasure();
        // 触发动画:圆环闭合 + 短暂停留 + 对勾描边
        _ring.BeginCheckmarkAnimation();
        // 延迟显示新文本(让圆环闭合动画先跑起来),500ms 后显示文字并触发 fade-in。
        // 复用 _fadeInTimer 实例(handler 用实例方法,避免每次 new lambda 闭包)。
        _fadeInTimer.Stop();
        _fadeInTimer.Tick -= OnFadeInTick;
        _fadeInTimer.Tick += OnFadeInTick;
        _pendingFadeInText = text;
        _fadeInTimer.Interval = TimeSpan.FromMilliseconds(500);
        _fadeInTimer.Start();
        // 倒计时恢复余额快照:复用 _overlayCountdown,停掉旧 tick handler 后再加新 handler。
        _overlayCountdown ??= new DispatcherTimer();
        _overlayCountdown.Stop();
        _overlayCountdown.Tick -= OnCountdownTick;
        _overlayCountdown.Tick += OnCountdownTick;
        _overlayCountdown.Interval = TimeSpan.FromSeconds(durationSeconds);
        _overlayCountdown.Start();
        PaperTodo.Plugin.ApiBalanceMonitor.Services.HookTrace.Write(
            $"ShowColorOverlay AFTER text='{text}' ring={ringColorHex}");
    }

    /// <summary>恢复暂存的 _label.Text / _ring 颜色 / arc;若未暂存则不动。</summary>
    private void RestoreColorOverlayIfAny()
    {
        if (_storedLabelText != null)
        {
            _label.Text = _storedLabelText;
            _storedLabelText = null;
        }
        if (_storedRingColorHex != null)
        {
            _ring.ForegroundBrush = ToBrush(_storedRingColorHex, "#9E9E9E");
            _ring.Value = _storedRingArc;
            _storedRingColorHex = null;
        }
        // 重置对勾动画:隐藏 + 停止正在跑的 StrokeDashOffset 动画
        _ring.ResetCheckmark();
    }

    private void ShowSpinnerOverlay(string text)
    {
        _overlaySpinnerText.Text = text;
        _overlaySpinnerBadge.Visibility = Visibility.Visible;
        _overlaySpinnerText.Visibility = Visibility.Visible;
        _overlayLayer.Visibility = Visibility.Visible;
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _overlaySpinnerAnim);
        // Spinner 类型无倒计时:持续到下次 Update() 由外部 HideHookOverlay 清掉
    }

    /// <summary>外部在 Update() 时清掉 spinner overlay(spinner 是"持续型")。</summary>
    public void HideHookOverlay()
    {
        StopOverlayCountdown();
        _fadeInTimer.Stop();
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;
        _overlaySpinnerText.Visibility = Visibility.Collapsed;
        RestoreColorOverlayIfAny();
        _overlayLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// MiniMax 胶囊刷新：文本 + 圆环弧值 / 颜色（圆环颜色由风险档位 / 剩余百分比决定）。
    /// text 真正变化时触发 350ms 淡入（避免每次 polling tick 都闪）；
    /// 圆环值/颜色未变时不再 InvalidateVisual（避免空挥）。
    /// </summary>
    public void Update(string text, string ringColorHex, double ringArc)
    {
        var textChanged = _label.Text != text;
        _label.Text = text;
        var clampedArc = Math.Clamp(ringArc, 0, 1);
        _ring.Value = clampedArc;
        // 颜色变化才覆盖前景 brush:同一 string 多次 ToBrush 颜色相同但实例不同，
        // ToBrush 已 Freeze，引用变化时仍触发圆环重渲染，这里仅当真正改变时覆盖。
        if (!string.Equals(_lastRingColorHex, ringColorHex, StringComparison.OrdinalIgnoreCase))
        {
            _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
            _lastRingColorHex = ringColorHex;
        }
        // ring 值变更超过 1‰ 时才显式 InvalidateVisual；否则由缓存机制避免重复渲染。
        if (Math.Abs(clampedArc - _lastAppliedArc) > 0.001)
        {
            _ring.InvalidateVisual();
            _lastAppliedArc = clampedArc;
        }
        // 余额快照推送时清掉 spinner overlay（spinner 持续到下一次 Update）;
        // PNG overlay 已经有自己的倒计时,自然结束。
        if (_overlayLayer.Visibility == Visibility.Visible &&
            (_overlaySpinnerBadge.Visibility == Visibility.Visible ||
             _overlaySpinnerText.Visibility == Visibility.Visible))
        {
            HideHookOverlay();
        }
        if (textChanged)
        {
            BeginLabelFadeIn();
        }
    }

    /// <summary>
    /// 文字淡入:Opacity 0 → 1, 350ms CubicEase EaseOut。
    /// 只动 _label.Opacity，不影响呼吸动效与 disableRing 的列宽。
    /// 用于常规 Update 的 textChanged 路径（轻量、快速反馈）。
    /// </summary>
    private void BeginLabelFadeIn()
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

    /// <summary>
    /// 文字淡入(慢速):Opacity 0 → 1, 350ms CubicEase EaseInOut。
    /// 用于 hook overlay 路径,匹配 HTML CSS "Payment Success" ease-in-out 节奏,感官更顺滑。
    /// </summary>
    private void BeginLabelFadeInSlow()
    {
        _label.Opacity = 0;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        _label.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>_fadeInTimer Tick 实例方法:替换原 lambda,避免每次 ShowColorOverlay 都分配闭包。</summary>
    private void OnFadeInTick(object? sender, EventArgs e)
    {
        _fadeInTimer.Stop();
        if (_pendingFadeInText != null)
        {
            _label.Text = _pendingFadeInText;
            _pendingFadeInText = null;
        }
        // 慢速淡入:ease-in-out,350ms,匹配 HTML CSS "Payment Success" 标题 0.6s ease-in-out 的节奏。
        BeginLabelFadeInSlow();
    }

    /// <summary>_overlayCountdown Tick 实例方法:替换原 lambda。</summary>
    private void OnCountdownTick(object? sender, EventArgs e)
    {
        StopOverlayCountdown();
        RestoreColorOverlayIfAny();
    }

    /// <summary>统一的倒计时停止动作:Stop + 解除 handler 订阅,保留实例供下次复用。</summary>
    private void StopOverlayCountdown()
    {
        if (_overlayCountdown == null) return;
        _overlayCountdown.Stop();
        _overlayCountdown.Tick -= OnCountdownTick;
    }

    /// <summary>
    /// 切换圆环可见性（设置 disableRing 时调用）。关闭后圆环列折叠为 0 宽,
    /// 文字列左移填满;切回时恢复原宽度,胶囊宽度不会抖动（Columns 已声明）。
    /// </summary>
    public void SetRingVisible(bool visible)
    {
        _ringColumnVisible = visible;
        // Column 1 是圆环列（[6 pad][18 ring][5 gap][* text][4 right pad]）。
        // 关闭时宽度归 0,gap 列也归 0,文字列自动扩到剩余空间。
        ColumnDefinitions[1].Width = visible
            ? new GridLength(18)
            : new GridLength(0);
        ColumnDefinitions[2].Width = visible
            ? new GridLength(5)
            : new GridLength(0);
        _ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // 修复 D：列宽变了，文字可显示宽度变了，强制重测避免一帧截断。
        _label.InvalidateMeasure();
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
        _label.Foreground = ToBrush(theme.WeakTextColor, "#707070");

        var accent = ToBrush(theme.AccentColor, "#B07A31");
        var track = new SolidColorBrush(
            Color.FromArgb(38, accent.Color.R, accent.Color.G, accent.Color.B));
        track.Freeze();
        _ring.TrackBrush = track;
        _ring.InvalidateVisual();
    }

    private static SolidColorBrush ToBrush(string value, string fallback)
    {
        SolidColorBrush brush;
        try
        {
            brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value)!);
        }
        catch
        {
            try
            {
                brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(fallback)!);
            }
            catch
            {
                brush = new SolidColorBrush(Colors.Gray);
            }
        }
        brush.Freeze();
        return brush;
    }
}