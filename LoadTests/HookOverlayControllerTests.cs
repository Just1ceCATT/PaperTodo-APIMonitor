using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo.Plugin;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Rendering;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using Xunit;

namespace PaperTodo.Plugin.ApiBalanceMonitor.LoadTests;

/// <summary>
/// Hook overlay 状态机的回归用例，覆盖 2026-11 修复的一批 overlay 缺陷：
///
/// 1. 沙漏动画残留：spinner → 任务完成 切换时，蓝色沙漏（visual child index 1，在对勾之上）
///    未被停止，一直盖住 Phase C 画出的对勾（index 0），两个动画同屏重叠约 2.6 秒。
/// 2. 失败事件画的是绿对勾而非红叉，语义自相矛盾。
/// 3. Color overlay 期间轮询会冲掉 overlay 的文本与颜色。
/// 4. 配额型 provider 的沙漏可能永久残留（OnPollingUpdate 被关在 signature 去重分支内）。
/// 5. Dot 胶囊（DeepSeek 等）收不到完成/等待/失败反馈。
/// 6. provider 切换后 controller 仍持有已被宿主丢弃的孤儿 view 引用。
/// 8. None overlay 事件把 host PlainText 推成空串。
///
/// 所有用例都在 STA 线程上执行：WPF 的 DispatcherTimer 需要一个跑着消息循环的
/// Dispatcher，否则测试进程无法退出（见 StaThread 夹具）。
/// </summary>
public sealed class HookOverlayControllerTests : IDisposable
{
    private readonly StaThread _sta;

    public HookOverlayControllerTests()
    {
        _sta = new StaThread();
        _sta.Start();
    }

    public void Dispose()
    {
        _sta.Dispose();
    }

    // ---------------- 测试夹具 ----------------

    private static HookEventPayload Payload(HookOverlayKind kind, string toolName = "Bash") =>
        new("TestEvent", toolName, "summary", DateTime.Now, kind);

    /// <summary>构造一个可直接驱动渲染的胶囊视图上下文。</summary>
    private static PaperCapsuleViewContext ViewContext() => new(
        PaperCapsuleSurfaceKind.Regular,
        120.0,
        28.0,
        new PaperBodyTheme(
            IsDark: false,
            PaperColor: "#FFFFFF",
            TextColor: "#000000",
            WeakTextColor: "#888888",
            AccentColor: "#0078D4",
            BorderColor: "#CCCCCC",
            FontFamily: "Segoe UI",
            FontScale: 1.0));

    /// <summary>读取私有字段，测试用。</summary>
    private static object? GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(target);
    }

    /// <summary>在 STA 线程上执行并同步等结果。</summary>
    private T OnSta<T>(Func<T> func) => _sta.Invoke(func).GetAwaiter().GetResult();

    /// <summary>
    /// 在 STA 线程上建一个 controller 并执行 action，最后 Dispose。
    /// controller 内部有 DispatcherTimer（Color overlay 3 秒倒计时），必须释放。
    /// </summary>
    private void WithController(Action<HookOverlayController> action)
    {
        OnSta(() =>
        {
            var controller = new HookOverlayController(Dispatcher.CurrentDispatcher);
            try
            {
                action(controller);
            }
            finally
            {
                controller.Dispose();
            }
            return 0;
        });
    }

    // ---------------- 缺陷 2：失败事件应画红叉 ----------------

    [Fact]
    public void BuildPlan_FailureImage_UsesCrossGlyph()
    {
        var plan = HookOverlayController.BuildPlan(Payload(HookOverlayKind.FailureImage), 3);
        Assert.Equal(HookGlyphKind.Cross, plan.Glyph);
        // 失败必须是红叉，不能复用绿对勾（否则"执行异常"看起来像"任务完成"）。
        Assert.NotEqual(HookGlyphKind.Check, plan.Glyph);
    }

    [Fact]
    public void BuildPlan_StopImage_UsesCheckGlyph()
    {
        var plan = HookOverlayController.BuildPlan(Payload(HookOverlayKind.StopImage), 3);
        Assert.Equal(HookGlyphKind.Check, plan.Glyph);
    }

    [Fact]
    public void BuildPlan_PermissionImage_UsesQuestionGlyph()
    {
        var plan = HookOverlayController.BuildPlan(Payload(HookOverlayKind.PermissionImage), 3);
        Assert.Equal(HookGlyphKind.Question, plan.Glyph);
    }

    // ---------------- 缺陷 3：Color overlay 状态可被识别 ----------------

    [Fact]
    public void OnHookEvent_ColorOverlay_HasColorOverlayTrue()
    {
        WithController(controller =>
        {
            controller.OnHookEvent(Payload(HookOverlayKind.StopImage), 3);
            Assert.True(controller.HasColorOverlay);
            Assert.False(controller.HasSpinnerOverlay);
        });
    }

    [Fact]
    public void OnHookEvent_Spinner_HasColorOverlayFalse()
    {
        WithController(controller =>
        {
            controller.OnHookEvent(Payload(HookOverlayKind.PreToolSpinner), 3);
            Assert.False(controller.HasColorOverlay);
            Assert.True(controller.HasSpinnerOverlay);
        });
    }

    // ---------------- 缺陷 4：轮询应能清掉 spinner ----------------

    [Fact]
    public void OnPollingUpdate_SpinnerPlan_ClearsActivePlan()
    {
        WithController(controller =>
        {
            controller.OnHookEvent(Payload(HookOverlayKind.PreToolSpinner), 3);
            Assert.NotNull(controller.ActivePlan);

            // 模拟下一轮 polling 到达：spinner 是"持续型"overlay，必须被清掉。
            controller.OnPollingUpdate();
            Assert.Null(controller.ActivePlan);
            Assert.False(controller.HasSpinnerOverlay);
        });
    }

    // ---------------- 缺陷 6：provider 切换后不留孤儿引用 ----------------

    [Fact]
    public void AttachViews_Null_DoesNotKeepOldViews()
    {
        WithController(controller =>
        {
            controller.AttachViews(
                new BalanceRingCapsuleView(ViewContext()),
                new BalanceRingCapsuleView(ViewContext()),
                new BalanceDotCapsuleView(ViewContext()),
                new BalanceDotCapsuleView(ViewContext()));

            // provider 切换时 BalanceSession 会调 AttachViews(null, null, null, null)。
            controller.AttachViews(null, null, null, null);

            Assert.Null(GetField(controller, "_regularRing"));
            Assert.Null(GetField(controller, "_dockedRing"));
            Assert.Null(GetField(controller, "_regularDot"));
            Assert.Null(GetField(controller, "_dockedDot"));
        });
    }

    // ---------------- 缺陷 8：None 事件不应产生 overlay ----------------

    [Fact]
    public void OnHookEvent_NoneOverlay_LeavesActivePlanNull()
    {
        WithController(controller =>
        {
            controller.OnHookEvent(Payload(HookOverlayKind.None), 3);

            // None plan 的 Text 是空串，若被当成 active overlay 会把 host PlainText 推成空串。
            Assert.Null(controller.ActivePlan);
            Assert.False(controller.HasSpinnerOverlay);
            Assert.False(controller.HasColorOverlay);
        });
    }

    [Fact]
    public void OnHookEvent_NoneOverlay_DoesNotInterruptRunningColorOverlay()
    {
        WithController(controller =>
        {
            controller.OnHookEvent(Payload(HookOverlayKind.StopImage), 3);
            Assert.True(controller.HasColorOverlay);

            // None 事件（如 Notification）不应打断正在计时的 Color overlay。
            controller.OnHookEvent(Payload(HookOverlayKind.None), 3);
            Assert.True(controller.HasColorOverlay);
            Assert.NotNull(controller.ActivePlan);
        });
    }

    // ---------------- 缺陷 1：沙漏残留（主 bug）----------------

    [Fact]
    public void PushOverlay_ColorAfterSpinner_StopsHourglass()
    {
        OnSta(() =>
        {
            var view = new BalanceRingCapsuleView(ViewContext());
            var ring = (BalanceProgressRing)GetField(view, "_ring")!;

            // 先推 spinner：沙漏开始无限循环。
            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.PreToolSpinner), 3));
            Assert.True(ring.IsHourglassAnimating);

            // 再推任务完成：沙漏必须被停掉，否则它（visual child index 1）会盖住对勾（index 0）。
            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.StopImage), 3));
            Assert.False(ring.IsHourglassAnimating);
            return 0;
        });
    }

    [Fact]
    public void PushOverlay_SpinnerAfterSpinner_KeepsSingleHourglass()
    {
        OnSta(() =>
        {
            var view = new BalanceRingCapsuleView(ViewContext());
            var ring = (BalanceProgressRing)GetField(view, "_ring")!;

            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.PreToolSpinner), 3));
            Assert.True(ring.IsHourglassAnimating);

            // PreToolUse → PostToolUse：沙漏重启（内部先停旧 Storyboard），仍只有一个在跑。
            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.PostToolSpinner), 3));
            Assert.True(ring.IsHourglassAnimating);
            return 0;
        });
    }

    [Fact]
    public void PushOverlay_None_StopsHourglass()
    {
        OnSta(() =>
        {
            var view = new BalanceRingCapsuleView(ViewContext());
            var ring = (BalanceProgressRing)GetField(view, "_ring")!;

            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.PreToolSpinner), 3));
            Assert.True(ring.IsHourglassAnimating);

            // plan 为 null / None：清理块仍须执行，沙漏必须停。
            view.PushOverlay(null);
            Assert.False(ring.IsHourglassAnimating);
            return 0;
        });
    }

    // ---------------- 缺陷 5：Dot 胶囊应收到完成反馈 ----------------

    [Fact]
    public void PushOverlay_ColorOverlay_UpdatesDotViewLabel()
    {
        OnSta(() =>
        {
            var view = new BalanceDotCapsuleView(ViewContext());
            var label = (TextBlock)GetField(view, "_label")!;

            // 修复前 Color overlay 只发给 Ring 视图，Dot 胶囊完全没有视觉反馈。
            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.StopImage), 3));
            Assert.Equal("✓ 任务完成", label.Text);

            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.FailureImage), 3));
            Assert.Equal("✗ 执行异常", label.Text);

            view.PushOverlay(HookOverlayController.BuildPlan(Payload(HookOverlayKind.PermissionImage), 3));
            Assert.Equal("等待用户回应", label.Text);
            return 0;
        });
    }

    // ---------------- 图元层不变量 ----------------

    /// <summary>
    /// 视觉子节点的索引必须与 GetVisualChild 的映射一一对应 ——
    /// WPF 用 GetVisualChild(i) 构建 VisualCollection，两者不一致会导致
    /// 绘制层级与预期不符（例如红叉被当成沙漏渲染）。
    /// </summary>
    [Fact]
    public void BalanceProgressRing_VisualChildOrder_MatchesGetVisualChild()
    {
        OnSta(() =>
        {
            var ring = new BalanceProgressRing();
            var type = typeof(BalanceProgressRing);

            // VisualChildrenCount / GetVisualChild 是 protected，用反射读取。
            var countProp = type.GetProperty("VisualChildrenCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(countProp);
            Assert.Equal(3, (int)countProp.GetValue(ring)!);

            var getChild = type.GetMethod("GetVisualChild", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(getChild);

            var child0 = getChild.Invoke(ring, new object[] { 0 });
            var child1 = getChild.Invoke(ring, new object[] { 1 });
            var child2 = getChild.Invoke(ring, new object[] { 2 });

            // 0 = 对勾 / 1 = 沙漏 / 2 = 叉（顺序即 Z 序：后加入者在上）
            Assert.Same(GetField(ring, "_checkmarkPath"), child0);
            Assert.Same(GetField(ring, "_hourglassPath"), child1);
            Assert.Same(GetField(ring, "_crossPath"), child2);

            // 三个图元互不相同，避免重复引用同一元素。
            Assert.NotSame(child0, child1);
            Assert.NotSame(child1, child2);
            return 0;
        });
    }
}
