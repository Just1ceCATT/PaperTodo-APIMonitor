using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Payload;

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
    // Permission overlay 黄色问号覆盖层：放在 _ring 上层(同一 Column 1,Panel.ZIndex=1),
    // 默认隐藏。PermissionRequest hook 触发时与 Color overlay 同步淡入显示,
    // 由 HideHookOverlay / RestoreColorOverlayIfAny 隐藏。
    private readonly TextBlock _questionGlyph;
    // Permission 黄色(#FFC107 Material Amber 500):与 Ring / Glyph 共用,保证视觉一致。
    private static readonly SolidColorBrush PermissionGlyphBrush = CreateFrozenPermissionBrush();
    private static SolidColorBrush CreateFrozenPermissionBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        brush.Freeze();
        return brush;
    }

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

        // Permission 黄色问号覆盖层：与 _ring 同列(Column 1,Panel.ZIndex=1,叠在 ring 上方),
        // 默认隐藏。字符 "?"、字号 14、Foreground 黄色 #FFC107、半粗,视觉等价 PNG 等待图标。
        // 不参与 ring measure/arrange(IsHitTestVisible=false),纯视觉装饰。
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

        // spinner overlay 已改为"替换模式"(改 _label.Text + _ring.ForegroundBrush),
        // 不再需要 _overlayLayer 覆盖层与 BuildOverlayLayer。

        ApplyTheme(context.Theme);
    }

    // ----- Overlay 渲染（hook 触发时临时覆盖胶囊） -----
    // spinner 蓝(#2196F3):冻结后跨帧复用,渲染期不再做线程检查;
    // 提取为静态字段,允许多实例共用同一 frozen 实例。
    // ShowColorOverlay / ShowSpinnerOverlay 共用此 brush 作为 foregroundColorHex 入参替代。
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
    private string? _storedRingColorHex;
    private double _storedRingArc;
    // spinner 期间把 _ring.TrackBrush 临时切为 Brushes.Transparent 隐藏底圈,
    // HideHookOverlay / countdown tick 通过 RestoreColorOverlayIfAny 恢复。
    // 直接存 Brush 引用,避免重新 ToBrush+Freeze 触发 Pen 缓存多一次 miss。
    private Brush? _storedRingTrackBrush;
    // Update 节流字段:仅当颜色字符串/弧值真正变化时才覆盖/重绘
    private string? _lastRingColorHex;
    private double _lastAppliedArc = -1.0;
    // 当前正在 fade-in 的符号类型:决定 OnFadeOutCompleteTick 在 Phase C 末显示对勾还是问号。
    // None/Check = 默认对勾(StopImage 走默认);Question = 黄色问号(PermissionImage 走这个)。
    private HookGlyphKind _pendingHookGlyph = HookGlyphKind.None;
    private enum HookGlyphKind { None, Check, Question }
    // 复用 DispatcherTimer 实例:fade-in / countdown timer 不再每次 new,
    // Tick handler 用实例方法代替 lambda,避免高频 hook 事件下的 GC 分配。
    private readonly DispatcherTimer _fadeInTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string? _pendingFadeInText;
    // 当前 hook 事件对应的圆环颜色,在 fade-out → fade-in 串联过程中跨 tick 持有,
    // 由 SetHookOverlay 写入,OnFadeOutCompleteTick 读出用于 ForegroundBrush。
    private string? _lastHookColorHex;

    /// <summary>
    /// 设置 hook overlay：用 WPF 原生控件实现 PNG 描述的效果,不是直接显示 PNG。
    /// - Color overlay (Stop/Permission/Failure):改 _label.Text + _ring 颜色,带倒计时自动恢复
    /// - Spinner overlay (PreTool/PostTool):改 _label.Text + _ring.ForegroundBrush 为蓝色,持续到下次 Update()
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

        // 恢复上一次 color / spinner overlay 的暂存值(动画过渡,不瞬时硬切)
        RestoreColorOverlayIfAny();

        if (kind == HookOverlayKind.None)
        {
            return;
        }

        // 颜色方案:参考 HTML 示范用 #68E534(鲜艳绿)。Permission/Failure 仍按现有色系保留风险辨识度。
        if (kind == HookOverlayKind.StopImage)
        {
            ShowColorOverlay("任务完成", "#68E534", durationSeconds, HookGlyphKind.Check);
        }
        else if (kind == HookOverlayKind.PermissionImage)
        {
            // PermissionRequest:黄色 #FFC107 + "等待用户回应" + 中心问号(替代对勾)。
            ShowColorOverlay("等待用户回应", "#FFC107", durationSeconds, HookGlyphKind.Question);
        }
        else if (kind == HookOverlayKind.FailureImage)
        {
            ShowColorOverlay("执行异常", "#F44336", durationSeconds, HookGlyphKind.Check);
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
    ///   - Phase C (220~820ms):圆环填充 Value 0→1 (600ms ease-in-out,绘制"圆环"+对勾/问号 BeginTime=350ms)
    ///                          + ring Opacity 0→1 fade-in(200ms ease-in-out,与圆环填充并行)
    ///   - Phase D (820ms tick 起的 350ms):文本 ease-in-out 淡入
    ///   - 总时长 ~1.17s,符合用户选的"紧凑 ~1.5s"节奏。
    ///
    /// glyph 决定 Phase C 末显示的视觉符号:Check=绿色对勾(任务完成语义),Question=黄色问号(等待用户回应语义)。
    ///
    /// 为何强制 Value=0 起步而不是从当前 0.5 补弧:
    /// 余额用得差不多时(如 Value=0.85),补弧只有 54° 视觉幅度,且圆角端点糊掉开始/结束锚点,
    /// 人眼感知不到"画圆环"动画。从 0 起步 → 完整 360° 画圆 → 视觉锚点固定在 12 点钟方向,
    /// 用户能明确感知到笔尖沿圆走一圈。
    /// </summary>
    private void ShowColorOverlay(string text, string ringColorHex, int durationSeconds, HookGlyphKind glyph)
    {
        _storedLabelText = _label.Text;
        _storedRingColorHex = _ring.ForegroundBrush is SolidColorBrush sb ? sb.Color.ToString() : null;
        // 修复 A:先停 ValueProperty 活动动画再抓真实本地值(不是动画插值)
        _ring.BeginAnimation(BalanceProgressRing.ValueProperty, null);
        _storedRingArc = _ring.Value;
        _lastHookColorHex = ringColorHex;
        // 把符号选择写到字段上,OnFadeOutCompleteTick 在 Phase C 末读取,
        // 决定显示对勾还是问号。
        _pendingHookGlyph = glyph;

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
            _ring.ForegroundBrush = Format.ToFrozenBrush(_storedRingColorHex, "#9E9E9E");

            // 修复 C:移除原先的 Value 0→_storedRingArc 启动动画(被 HideHookOverlay 末尾
            // 的 BeginAnimation(null) 立即取消,无视觉意义)。Value 由 HideHookOverlay 末尾
            // 或 view.Update 中的 BeginDrawAnimation 统一驱动。

            // 中断可能正在跑的 Opacity 动画,并把 ring 重置为不透明。
            // 若刚 SetHookOverlay 进来,这个 1 是为了让 ShowColorOverlay 的 fade-out 从可见开始。
            // 若在 OnCountdownTick 进来,这个 1 是为了保证 ring 在恢复过程中仍可见。
            _ring.BeginAnimation(UIElement.OpacityProperty, null);
            _ring.Opacity = 1;

            _storedRingColorHex = null;
        }
        // spinner 期间把 TrackBrush 临时改成 Brushes.Transparent 隐藏底圈,
        // 这里恢复 ApplyTheme 设置的原始 brush。
        // Color overlay 路径不写 _storedRingTrackBrush(null 判断天然隔离)。
        if (_storedRingTrackBrush != null)
        {
            _ring.TrackBrush = _storedRingTrackBrush;
            _storedRingTrackBrush = null;
        }
        // 重置对勾动画:隐藏 + 停止正在跑的 StrokeDashOffset 动画
        _ring.ResetCheckmark();
        // 重置问号 glyph:隐藏 + 停止 fade-in 动画,避免下次 Permission 触发时残留半透明。
        if (_questionGlyph.Visibility == Visibility.Visible)
        {
            _questionGlyph.BeginAnimation(UIElement.OpacityProperty, null);
            _questionGlyph.Opacity = 0;
            _questionGlyph.Visibility = Visibility.Collapsed;
        }
        // 同步清空符号选择,避免下次 SetHookOverlay 进入 Phase C 时用错符号。
        _pendingHookGlyph = HookGlyphKind.None;
    }

    private void ShowSpinnerOverlay(string text)
    {
        // Spinner overlay（替换模式）：与 ShowColorOverlay 共用 RestoreColorOverlayIfAny 路径。
        // - 暂存 _label.Text / _ring 当前颜色 / 圆环 Value / 圆环底圈 TrackBrush
        // - 把 _label.Text 替换为 spinner 文本
        // - _ring.ForegroundBrush 替换为 spinner 蓝(#2196F3)
        // - 圆环视觉清空:TrackBrush=Brushes.Transparent(底圈不画) + Value=0(OnRender 早 return,弧段不画)
        // - 沙漏描边→旋转循环动画(1秒描边 + 1秒旋转,RepeatBehavior=Forever)
        //   —— 替代圆环填充动画,传达"工具调用进行中"语义。
        // 不创建覆盖层、不带倒计时 —— 与 StopImage 等 Color overlay 走同一恢复路径。
        // 不动 _ring.Opacity —— FrameworkElement.Opacity 继承会让挂在 _ring 下的 _hourglassPath
        // (Path visual child)也变透明,沙漏完全不显示(已踩坑)。
        _storedLabelText = _label.Text;
        _storedRingColorHex = _ring.ForegroundBrush is SolidColorBrush sb ? sb.Color.ToString() : null;
        // 修复 A:捕获 _storedRingArc 之前先停掉 ValueProperty 活动动画。
        // WPF DP 语义:活动动画 > 本地值;BeginAnimation(null) 把动画输出从基值栈剥离,
        // 此时 Value 读到的是上一次 SetValue 的本地值(即 polling Update 设的 clampedArc),
        // 是真实最新数据值,而不是动画插值。
        _ring.BeginAnimation(BalanceProgressRing.ValueProperty, null);
        _storedRingArc = _ring.Value;
        _storedRingTrackBrush = _ring.TrackBrush;

        _label.Text = text;
        _ring.ForegroundBrush = SpinnerBadgeBrush;
        _ring.TrackBrush = Brushes.Transparent; // 底圈临时透明(沙漏独占视觉);RestoreColorOverlayIfAny 恢复

        // 修复:先停掉 ValueProperty 上可能残存的 BeginDrawAnimation(500ms 圆环绘制动画),
        // 否则 SetValue(0) 被活动动画输出压住(DP 优先级:活动动画 > 本地值),
        // 圆环仍按插值画蓝色弧段,视觉上变成"蓝沙漏盖在蓝圆环上"。
        _ring.BeginAnimation(BalanceProgressRing.ValueProperty, null);
        _ring.Value = 0; // OnRender 早 return,弧段不画

        _ring.BeginHourglassAnimation();
        // Spinner 类型无倒计时:持续到下次 Update 由外部 HideHookOverlay 清掉。
    }

    /// <summary>外部在 Update() 时清掉 spinner overlay(spinner 是"持续型")。</summary>
    public void HideHookOverlay()
    {
        StopOverlayCountdown();
        _fadeInTimer.Stop();
        // 中断可能正在跑的 ring Opacity 动画,reset 到 1(避免叠 spinner 后 ring 卡在 0)
        _ring.BeginAnimation(UIElement.OpacityProperty, null);
        _ring.Opacity = 1;
        // spinner 期间改动的 _label.Text / _ring.ForegroundBrush 由 RestoreColorOverlayIfAny 恢复。
        RestoreColorOverlayIfAny();
        // 沙漏是 spinner 期间的瞬时动画,结束必须隐藏并停止描边动画。
        _ring.StopHourglassAnimation();

        // 修复 B:fallback 用 _lastAppliedArc 而不是 _storedRingArc。
        // _lastAppliedArc 永远是 Update 时设的 clampedArc,是"真实最新数据值"。
        // _storedRingArc 即使经过修复 A 仍可能因后续 activity 失准(动画 in-flight 时捕获)。
        // 双重保险,避免普通 polling tick 因 signature diff=0 跳过 BeginDrawAnimation 导致 Value 永久卡在错值。
        _ring.BeginAnimation(BalanceProgressRing.ValueProperty, null);
        _ring.Value = _lastAppliedArc < 0 ? 0 : _lastAppliedArc;
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
            _ring.ForegroundBrush = Format.ToFrozenBrush(ringColorHex, "#9E9E9E");
            _lastRingColorHex = ringColorHex;
        }
        // spinner overlay 改用替换模式后不再有 _overlayLayer / _overlaySpinner* 状态可检测,
        // 清理由 BalanceSession.UpdateSnapshot spinner 分支显式触发(Ring + Dot 双视图)。
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
        _ring.ForegroundBrush = Format.ToFrozenBrush(_lastHookColorHex ?? "#68E534", "#9E9E9E");
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

        // Phase C:Value 0→1(600ms ease-in-out 画圆环)+ Phase C 后符号动画
        // Check 走对勾描边 400ms;Question 走静态问号(无动画,等待语义不需要动态笔尖)。
        if (_pendingHookGlyph == HookGlyphKind.Question)
        {
            // 问号静态显示:不画描边动画,直接 fade-in(由 ring Opacity 0→1 带动)。
            _questionGlyph.Visibility = Visibility.Visible;
            _questionGlyph.Opacity = 0;
            var glyphFadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                BeginTime = TimeSpan.FromMilliseconds(550),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _questionGlyph.BeginAnimation(UIElement.OpacityProperty, glyphFadeIn);
        }
        else
        {
            // 默认对勾(StopImage / FailureImage 走这里)。
            _ring.BeginCheckmarkAnimation();
        }

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
        _label.Foreground = Format.ToFrozenBrush(theme.WeakTextColor, "#707070");

        var accent = Format.ToFrozenBrush(theme.AccentColor, "#B07A31");
        var track = new SolidColorBrush(
            Color.FromArgb(38, accent.Color.R, accent.Color.G, accent.Color.B));
        track.Freeze();
        _ring.TrackBrush = track;
        _ring.InvalidateVisual();
    }
}