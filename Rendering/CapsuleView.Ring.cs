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
    // 当前 hook 事件对应的圆环颜色,在 fade-out → fade-in 串联过程中跨 tick 持有,
    // 由 SetHookOverlay 写入,OnFadeOutCompleteTick 读出用于 ForegroundBrush。
    private string? _lastHookColorHex;

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
        // 清掉可能正在跑的 ring Opacity 动画,但不显式设回 1——让 RestoreColorOverlayIfAny 接管状态重置
        // (若该次 Restore 把 ring 重新置 1,后续 ShowColorOverlay 会从 1 重新 fade-out;若没 stored state 需要恢复,
        // 则原状保留)。这样避免在快速重入时把刚启动的 fade-out 动画瞬间抢断。
        _ring.BeginAnimation(UIElement.OpacityProperty, null);
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;
        _overlaySpinnerText.Visibility = Visibility.Collapsed;

        // 恢复上一次 color overlay 的暂存值(动画过渡,不瞬时硬切)
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
    /// 四段串联动画(参考 https://css-tricks.com 成功动画 + CSS 圆环+对勾+文本模式):
    ///   - Phase A (0~220ms):fade-out 当前内容(_label.Opacity &amp; _ring.Opacity 1→0 ease-out)
    ///   - Phase B (220ms tick):重置 ring 状态(Value=0, ForegroundBrush=success-color, ResetCheckmark)
    ///   - Phase C (220~820ms):圆环填充 Value 0→1 (600ms ease-in-out,绘制"圆环"+对勾 400ms ease-out BeginTime=350ms)
    ///                          + ring Opacity 0→1 fade-in(200ms ease-in-out,与圆环填充并行)
    ///   - Phase D (820ms tick 起的 350ms):文本 ease-in-out 淡入
    ///   - 总时长 ~1.17s,符合用户选的"紧凑 ~1.5s"节奏。
    ///
    /// 为何强制 Value=0 起步而不是从当前 0.5 补弧:
    /// 余额用得差不多时(如 Value=0.85),补弧只有 54° 视觉幅度,且圆角端点糊掉开始/结束锚点,
    /// 人眼感知不到"画圆环"动画。从 0 起步 → 完整 360° 画圆 → 视觉锚点固定在 12 点钟方向,
    /// 用户能明确感知到笔尖沿圆走一圈。
    /// </summary>
    private void ShowColorOverlay(string text, string ringColorHex, int durationSeconds)
    {
        _storedLabelText = _label.Text;
        _storedRingColorHex = _ring.ForegroundBrush is SolidColorBrush sb ? sb.Color.ToString() : null;
        _storedRingArc = _ring.Value;
        _lastHookColorHex = ringColorHex;

        // Phase A:fade-out 当前内容(只对现有 _label + _ring 做 Opacity 动画,不动内容)
        BeginLabelFadeOut(220);
        var ringFadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _ring.BeginAnimation(UIElement.OpacityProperty, ringFadeOut);

        // 阶段串行调度:220ms 后 OnFadeOutCompleteTick 触发 Phase B+C,然后再 820ms 后调 OnFadeInTick 淡入 label
        _fadeInTimer.Stop();
        _fadeInTimer.Tick -= OnFadeInTick;
        _fadeInTimer.Tick -= OnFadeOutCompleteTick;
        _fadeInTimer.Tick += OnFadeOutCompleteTick;
        _pendingFadeInText = text;
        _fadeInTimer.Interval = TimeSpan.FromMilliseconds(220);
        _fadeInTimer.Start();

        // 倒计时恢复余额快照:复用 _overlayCountdown,到时清掉 _label.Opacity 与 _ring.Opacity 还原成 1
        _overlayCountdown ??= new DispatcherTimer();
        _overlayCountdown.Stop();
        _overlayCountdown.Tick -= OnCountdownTick;
        _overlayCountdown.Tick += OnCountdownTick;
        _overlayCountdown.Interval = TimeSpan.FromSeconds(durationSeconds);
        _overlayCountdown.Start();

        PaperTodo.Plugin.ApiBalanceMonitor.Services.HookTrace.Write(
            $"ShowColorOverlay AFTER text='{text}' ring={ringColorHex} (fade-out→fade-in flow)");
    }

    /// <summary>
    /// 恢复暂存的 _label.Text / _ring 颜色 / arc;若未暂存则不动。
    ///
    /// 关键改动（用户反馈「复原但带过渡动画」）：圆环 Value 用 400ms QuadraticEase EaseOut
    /// 从 1 平滑插值回 _storedRingArc,而不是瞬时硬切。前景色 / 文字 / 对勾 仍瞬时切。
    /// 这避免了用户观察到的"圆环突然不见"的顿挫感。
    /// </summary>
    private void RestoreColorOverlayIfAny()
    {
        if (_storedLabelText != null)
        {
            _label.Text = _storedLabelText;
            _storedLabelText = null;
        }
        if (_storedRingColorHex != null)
        {
            // 颜色瞬时切:Brush 是冻结的,且 ToBrush 会冻结;若要走颜色过渡需创建 mutable brush 临时挂上,
            // 复杂度高收益低。这里保瞬时。
            _ring.ForegroundBrush = ToBrush(_storedRingColorHex, "#9E9E9E");

            // Value 用 400ms ease-out 过渡:1 → _storedRingArc,让用户看到圆环从满到当前的"收缩"。
            // 如果 _storedRingArc 已经是 1.0(本来就是满的),不用动画,直接显式清除并赋值即可。
            if (_storedRingArc < 1.0)
            {
                var restoreAnim = new DoubleAnimation
                {
                    To = _storedRingArc,
                    Duration = TimeSpan.FromMilliseconds(400),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                _ring.BeginAnimation(BalanceProgressRing.ValueProperty, restoreAnim);
            }
            else
            {
                _ring.BeginAnimation(BalanceProgressRing.ValueProperty, null);
                _ring.Value = _storedRingArc;
            }

            // 中断可能正在跑的 Opacity 动画,并把 ring 重置为不透明。
            // 若刚 SetHookOverlay 进来,这个 1 是为了让 ShowColorOverlay 的 fade-out 从可见开始。
            // 若在 OnCountdownTick 进来,这个 1 是为了保证 ring 在恢复过程中仍可见。
            _ring.BeginAnimation(UIElement.OpacityProperty, null);
            _ring.Opacity = 1;

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
        // 中断可能正在跑的 ring Opacity 动画,reset 到 1(避免叠 spinner 后 ring 卡在 0)
        _ring.BeginAnimation(UIElement.OpacityProperty, null);
        _ring.Opacity = 1;
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
        // 圆环弧度变更超过 1‰ 时触发"从 0 顺时针绘制"动画（用户要求"绘制而不是凭空出现"）。
        // 颜色由 ForegroundBrush 决定（绿色 Safe、橙色 Warn、红色 Risk），与最终弧度色一致。
        // BeginAnimation 启动的 ValueProperty 动画带 AffectsRender,自动逐帧重绘,无需显式 InvalidateVisual。
        if (Math.Abs(clampedArc - _lastAppliedArc) > 0.001)
        {
            _ring.BeginDrawAnimation(clampedArc);
            _lastAppliedArc = clampedArc;
        }
        // 颜色变化才覆盖前景 brush:同一 string 多次 ToBrush 颜色相同但实例不同，
        // ToBrush 已 Freeze，引用变化时仍触发圆环重渲染，这里仅当真正改变时覆盖。
        if (!string.Equals(_lastRingColorHex, ringColorHex, StringComparison.OrdinalIgnoreCase))
        {
            _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
            _lastRingColorHex = ringColorHex;
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

    /// <summary>
    /// 文字淡出:CubicEase EaseOut,默认 220ms。
    /// 不重设 _label.Opacity(动画从当前值插值到 0),避免重复触发时突变。
    /// 与 BeginLabelFadeInSlow 对称,共同支撑 fade-out → fade-in 的 hook overlay 串联。
    /// </summary>
    private void BeginLabelFadeOut(int durationMs = 220)
    {
        var anim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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

    /// <summary>
    /// _fadeInTimer Tick 实例方法:fade-out 完成回调,触发 Phase B + Phase C 起点。
    /// 设置新 ring 状态、ring Opacity 由 0 开始 fade-in、启动 BeginCheckmarkAnimation、
    /// 然后把 handler 切回 OnFadeInTick,并启动 820ms 后调用 → Phase D label 淡入。
    /// </summary>
    private void OnFadeOutCompleteTick(object? sender, EventArgs e)
    {
        _fadeInTimer.Stop();
        _fadeInTimer.Tick -= OnFadeOutCompleteTick;
        _fadeInTimer.Tick += OnFadeInTick;

        // Phase B+C 起点:重置 ring 状态 + 启动 Value 0→1(圆环填充动画)+ ring Opacity 0→1 fade-in
        _label.Text = "";
        _label.Opacity = 0;
        _ring.ForegroundBrush = ToBrush(_lastHookColorHex ?? "#68E534", "#9E9E9E");
        _ring.ResetCheckmark();
        _ring.Value = 0; // 强制从 0 开始填充,Value→0→1 即可见出"画圆环"动画

        // ring Opacity 0→1 fade-in:200ms ease-in-out,与 Value 0→1 同时启动
        var ringFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        _ring.BeginAnimation(UIElement.OpacityProperty, ringFadeIn);

        // Phase C:Value 0→1(600ms ease-in-out 画圆环)+ Phase C 后对勾描边 400ms
        // 默认 fillDurationMs=600,strokeDurationMs=400,holdDurationMs=-50(对勾在末端与圆环接近同时结束)
        _ring.BeginCheckmarkAnimation();

        // Phase D 调度:820ms 后(220ms fade-out + 600ms ring fill)调 OnFadeInTick 淡入 label
        _fadeInTimer.Interval = TimeSpan.FromMilliseconds(820);
        _fadeInTimer.Start();
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