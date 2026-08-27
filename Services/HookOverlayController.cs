using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// Hook overlay 动画状态机。
///
/// 集中持有所有 overlay 状态字段(原 BalanceSession._pendingOverlay* /
/// _activeOverlayText / _activeOverlayTimer + Ring/Dot view 的 _stored* 等)，
/// 派生 <see cref="HookOverlayPlan"/> 派发到 4 个 view,管理倒计时。
///
/// 设计目标:
/// 1. 状态机单一真相源——BalanceSession 和 view 不再各自"猜"对方
/// 3. 状态变更经过 Controller,留 HookTrace 日志,bug 复盘有迹可循
/// 4. Tick handler 用实例方法(对齐 Ring 的 OnFadeInTick / OnFadeOutCompleteTick 模式)
///
/// 实例生命周期:
/// - BalanceSession 构造时 new,传入 _uiDispatcher
/// - AttachViews 在 view 创建后调(可多次——每次 CreateCapsuleView 后重置)
/// - Dispose 在 BalanceSession.Dispose 时调
/// </summary>
internal sealed class HookOverlayController
{
    private readonly Dispatcher _uiDispatcher;

    // 当前激活的 plan(view 渲染源)。null = 无 overlay。
    private HookOverlayPlan? _activePlan;
    // view 还没创建时缓存的 plan,AttachViews 时补发一次后清空。
    private HookOverlayPlan? _pendingPlan;

    // Color overlay 倒计时。Spinner 持续型不启动 timer。
    private DispatcherTimer? _colorCountdown;

    // 4 个 view 引用 + 它们的 dispatcher 缓存(避免每次派发重新查询)
    private BalanceRingCapsuleView? _regularRing;
    private BalanceRingCapsuleView? _dockedRing;
    private BalanceDotCapsuleView? _regularDot;
    private BalanceDotCapsuleView? _dockedDot;

    /// <summary>当前激活的 plan,供 BalanceSession 读 host PlainText 用。</summary>
    public HookOverlayPlan? ActivePlan => _activePlan;

    /// <summary>是否有 spinner 持续型 overlay(用于 polling Update 清理路径判断)。</summary>
    public bool HasSpinnerOverlay =>
        _activePlan is { Kind: HookOverlayKind.PreToolSpinner or HookOverlayKind.PostToolSpinner };

    public HookOverlayController(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
    }

    /// <summary>缓存 4 个 view 引用 + 补发 PendingPlan。可多次调(每次 CreateCapsuleView 后重置)。</summary>
    public void AttachViews(
        BalanceRingCapsuleView? regularRing,
        BalanceRingCapsuleView? dockedRing,
        BalanceDotCapsuleView? regularDot,
        BalanceDotCapsuleView? dockedDot)
    {
        _regularRing = regularRing;
        _dockedRing = dockedRing;
        _regularDot = regularDot;
        _dockedDot = dockedDot;
        // 补发 PendingPlan(view 刚创建时还有未消费的 hook overlay)
        if (_pendingPlan != null)
        {
            DispatchToViews(_pendingPlan);
            _pendingPlan = null;
        }
    }

    /// <summary>
    /// 处理 hook 事件:派生 plan,停旧倒计时,派发到 4 view,启新倒计时(若是 Color overlay)。
    /// 必须在 BalanceSession 缓存完 _latestHookEvent 后调,这样 ToolTip 第二行能正确拿 summary。
    /// </summary>
    public void OnHookEvent(HookEventPayload payload, int defaultDurationSeconds)
    {
        var plan = BuildPlan(payload, defaultDurationSeconds);

        // 停旧 Color overlay timer + 退订 Tick(handler 残留 race 防御)
        if (_colorCountdown != null)
        {
            _colorCountdown.Stop();
            _colorCountdown.Tick -= OnColorCountdown;
            _colorCountdown = null;
        }

        // 派发到 4 view。view 都未创建时缓存为 PendingPlan,AttachViews 时补发。
        if (HasAnyView())
        {
            DispatchToViews(plan);
        }
        else
        {
            _pendingPlan = plan;
            HookTrace.Write($"[overlay-controller] pending kind={plan.Kind} text='{plan.Text}'");
        }

        _activePlan = plan;

        // Color overlay 启动倒计时;Spinner 不启动(持续到下次 polling Update 清理)
        if (plan.DurationSeconds.HasValue)
        {
            _colorCountdown = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(plan.DurationSeconds.Value)
            };
            _colorCountdown.Tick += OnColorCountdown;
            _colorCountdown.Start();
        }

        HookTrace.Write($"[overlay-controller] active kind={plan.Kind} text='{plan.Text}' dur={(plan.DurationSeconds?.ToString() ?? "none")}");
    }

    /// <summary>
    /// Polling Update 路径:有 spinner overlay 就清掉(view 回到余额),Color overlay 不动(等 timer)。
    /// 由 BalanceSession.UpdateSnapshot 在顶部调一次,内部决定是否走 view.Update 路径。
    /// </summary>
    public void OnPollingUpdate()
    {
        if (!HasSpinnerOverlay) return;

        // 停 spinner 持续型。清空 _activePlan + 让 view 走 ClearOverlay。
        HookTrace.Write($"[overlay-controller] polling-update clear spinner active='{_activePlan?.Text}'");
        _activePlan = null;
        DispatchToViewsClear();
    }

    /// <summary>外部强制清除(用于 BalanceSession 重置 hook 状态、Dispose)。</summary>
    public void ClearAll()
    {
        if (_colorCountdown != null)
        {
            _colorCountdown.Stop();
            _colorCountdown.Tick -= OnColorCountdown;
            _colorCountdown = null;
        }
        _activePlan = null;
        _pendingPlan = null;
        DispatchToViewsClear();
    }

    public void Dispose()
    {
        ClearAll();
        _regularRing = null;
        _dockedRing = null;
        _regularDot = null;
        _dockedDot = null;
    }

    // ---------------- 私有 ----------------

    private bool HasAnyView() =>
        _regularRing != null || _dockedRing != null ||
        _regularDot != null || _dockedDot != null;

    /// <summary>
    /// 派生 plan:HookEventPayload 字段映射到 RingColorHex / Glyph / DurationSeconds,
    /// spinner text 走 HookTextResolver(已包含 MCP 命名解析、兜底、大小写不敏感)。
    /// </summary>
    private static HookOverlayPlan BuildPlan(HookEventPayload payload, int defaultDurationSeconds)
    {
        var kind = payload.Overlay; // HookEventServer 已映射
        var text = kind switch
        {
            HookOverlayKind.StopImage => "✓ 任务完成",
            HookOverlayKind.PermissionImage => "等待用户回应",
            HookOverlayKind.FailureImage => "✗ 执行异常",
            HookOverlayKind.PreToolSpinner => HookTextResolver.ResolvePre(payload.ToolName),
            HookOverlayKind.PostToolSpinner => HookTextResolver.ResolvePost(payload.ToolName),
            _ => string.Empty,
        };
        var ringColorHex = kind switch
        {
            HookOverlayKind.StopImage => "#68E534",
            HookOverlayKind.PermissionImage => "#FFC107",
            HookOverlayKind.FailureImage => "#F44336",
            _ => null, // spinner 走 SpinnerBadgeBrush(view 内部固定)
        };
        var glyph = kind switch
        {
            HookOverlayKind.StopImage => HookGlyphKind.Check,
            HookOverlayKind.PermissionImage => HookGlyphKind.Question,
            HookOverlayKind.FailureImage => HookGlyphKind.Check,
            _ => HookGlyphKind.None,
        };
        var durationSeconds = kind switch
        {
            HookOverlayKind.StopImage => defaultDurationSeconds,
            HookOverlayKind.PermissionImage => defaultDurationSeconds,
            HookOverlayKind.FailureImage => defaultDurationSeconds,
            _ => (int?)null,
        };
        // ToolTip 由 BalanceSession 在 PushCapsulePresentation 路径构造,plan 不重复带。
        return new HookOverlayPlan(
            Kind: kind,
            Text: text,
            ToolTip: string.Empty,
            RingColorHex: ringColorHex,
            Glyph: glyph,
            DurationSeconds: durationSeconds,
            PreferredWidth: 0);
    }

    private void DispatchToViews(HookOverlayPlan plan)
    {
        // 4 个 view 派发:Color overlay 走 Ring 双视图(避免对勾动画干扰 Dot 圆点);
        // Spinner 走 4 个全部。全部 marshal 到 view dispatcher。
        DispatchToView(_regularRing, plan);
        DispatchToView(_dockedRing, plan);
        var isColorOverlay = plan.Kind is HookOverlayKind.StopImage
            or HookOverlayKind.PermissionImage
            or HookOverlayKind.FailureImage;
        if (!isColorOverlay)
        {
            DispatchToView(_regularDot, plan);
            DispatchToView(_dockedDot, plan);
        }
    }

    private void DispatchToView<T>(T? view, HookOverlayPlan plan) where T : FrameworkElement
    {
        if (view == null) return;
        var dispatcher = view.Dispatcher;
        if (dispatcher == _uiDispatcher)
        {
            InvokePushOverlay(view, plan);
        }
        else
        {
            // 后台线程 → view 线程 marshal。
            dispatcher.BeginInvoke(() => InvokePushOverlay(view, plan));
        }
    }

    private void InvokePushOverlay<T>(T view, HookOverlayPlan plan) where T : FrameworkElement
    {
        switch (view)
        {
            case BalanceRingCapsuleView r: r.PushOverlay(plan); break;
            case BalanceDotCapsuleView d: d.PushOverlay(plan); break;
        }
    }

    private void DispatchToViewsClear()
    {
        DispatchClearTo(_regularRing);
        DispatchClearTo(_dockedRing);
        DispatchClearTo(_regularDot);
        DispatchClearTo(_dockedDot);
    }

    private void DispatchClearTo<T>(T? view) where T : FrameworkElement
    {
        if (view == null) return;
        var dispatcher = view.Dispatcher;
        if (dispatcher == _uiDispatcher)
        {
            InvokeClear(view);
        }
        else
        {
            dispatcher.BeginInvoke(() => InvokeClear(view));
        }
    }

    private void InvokeClear<T>(T view) where T : FrameworkElement
    {
        switch (view)
        {
            case BalanceRingCapsuleView r: r.ClearOverlay(); break;
            case BalanceDotCapsuleView d: d.ClearOverlay(); break;
        }
    }

    /// <summary>_colorCountdown Tick 实例方法:Color overlay 倒计时到时清掉。</summary>
    private void OnColorCountdown(object? sender, EventArgs e)
    {
        if (_colorCountdown != null)
        {
            _colorCountdown.Stop();
            _colorCountdown.Tick -= OnColorCountdown;
            _colorCountdown = null;
        }
        _activePlan = null;
        HookTrace.Write("[overlay-controller] color-countdown fired, clearing");
        DispatchToViewsClear();
    }
}