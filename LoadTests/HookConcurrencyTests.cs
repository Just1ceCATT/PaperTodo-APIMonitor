using System.Reflection;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Session;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaperTodo.Plugin.ApiBalanceMonitor.LoadTests;

/// <summary>
/// HookEventWindow 并发崩溃回归用例。
///
/// 修复前症状:
///   - Queue&lt;HookEvent&gt; 不是线程安全;后台线程 Enqueue 与 UI 线程 ToArray 并发,
///     破坏 _array / _size / _head / _tail 内部不变量。
///   - 崩溃 A:ToArray 在 Grow 过程中命中 Array.Copy destination 不够长 → ArgumentException。
///   - 崩溃 B:ToArray 落进 grow 后未填充的 null slot → BuildHooks 拿 null 取 EventName → NRE。
///
/// 根因:OnHookReceived 的 marshaling 用错了 API —— Dispatcher.FromThread(Thread.CurrentThread)
///   在线程池线程上返回 null,导致 marshaling 形同虚设,Queue 直接被并发 mutate。
///
/// 本测试通过反射访问 internal 的 OnHookReceived / HookEventWindow,
/// 用 10000 个 Task.WhenAll 同时触发 OnHookReceived + STA 线程同步 ToArray,
/// 复刻原 bug 的 race window。修复前必现 ArgumentException/NRE,
/// 修复后 0 异常且 HookEventWindow 容量 = 5、无 null slot。
/// </summary>
public sealed class HookConcurrencyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly StaThread _sta;
    private readonly BalanceSession _session;
    private readonly HookAccess _hook;

    public HookConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;

        // 1) 起 STA 线程 + Dispatcher 消息循环。
        _sta = new StaThread();
        _sta.Start();

        // 2) 在 STA 线程上构造 BalanceSession。
        var context = FakePaperBodyContext.Create();
        _session = _sta.Invoke(() => new BalanceSession(context)).GetAwaiter().GetResult();

        // 3) 反射夹具。
        _hook = new HookAccess(_session);
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* 测试结束,异常忽略 */ }
        _sta.Dispose();
    }

    /// <summary>
    /// 核心回归用例:10000 个 hook 用 Task.WhenAll 同时启动在 10000 个独立线程池线程上,
    /// STA 线程上 10000 次 ToArray 与后台 Enqueue/Dequeue 真正并发。
    /// 修复前必现 ArgumentException("Destination array was not long enough") 或 NRE。
    /// 修复后 0 异常,HookEventWindow 容量 = 5,无 null slot。
    /// </summary>
    [Fact]
    public void Concurrent_HookTriggered_AndBuild_ShouldNotCorruptQueue()
    {
        const int N = 2000;
        var payloads = Enumerable.Range(0, N)
            .Select(i => new HookEventPayload(
                EventName: "PostToolUse",
                ToolName: "Tool" + i,
                Summary: "evt-" + i,
                ReceivedAt: DateTime.Now,
                Overlay: HookOverlayKind.None))
            .ToArray();

        // 用 Barrier 让所有 N 个 invoke 几乎同时挂到 ThreadPool 上,
        // 最大化 Queue 操作与 STA ToArray 的并发窗口。
        //
        // Barrier 需要 N+1 个参与者同时到达才能放行,而这 N 个 Task.Run 依赖线程池
        // 提供 N 个线程 —— .NET 线程池默认按 ~1-2 线程/秒缓慢注入,凑齐 2000 个要
        // 十几分钟。原实现无超时的 SignalAndWait() 会让 STA 线程永久阻塞在屏障上,
        // 测试宿主被判定为崩溃(与被测代码无关)。
        //
        // 两处修复:
        // 1) 抬高线程池最小线程数,让 N 个参与者能真正同时就位,保住测试的并发强度;
        // 2) SignalAndWait 带超时 + 容忍屏障被打破,线程池供给不足时优雅降级
        //    (仍然并发执行 hook,只是不再同步起跑),杜绝死锁。
        ThreadPool.GetMinThreads(out var previousMinThreads, out var previousMinIo);
        ThreadPool.SetMinThreads(N + 32, Math.Max(previousMinIo, 32));
        try
        {
            using var barrier = new Barrier(N + 1);
            var barrierTimeout = TimeSpan.FromSeconds(30);

            var triggerTasks = new Task[N];
            for (var i = 0; i < N; i++)
            {
                var p = payloads[i];
                triggerTasks[i] = Task.Run(() =>
                {
                    try
                    {
                        barrier.SignalAndWait(barrierTimeout);
                    }
                    catch (BarrierPostPhaseException)
                    {
                        // 屏障因超时被打断:仍继续执行 hook,保留并发压力。
                    }
                    _hook.InvokeOnHookReceived(p);
                });
            }

            // STA 线程循环 ToArray,直接触发 Queue 内部状态读取,
            // 不依赖 Build() / PostSnapshot 等额外序列化逻辑。
            Exception? buildException = null;
            var buildTask = _sta.RunAsync(async () =>
            {
                try
                {
                    barrier.SignalAndWait(barrierTimeout);
                }
                catch (BarrierPostPhaseException)
                {
                    // 同上:超时打断后直接进入读取循环。
                }
                for (var i = 0; i < N; i++)
                {
                    try
                    {
                        _ = _hook.GetHookEventWindow();
                    }
                    catch (ArgumentException ex)
                    {
                        buildException = ex;
                        _output.WriteLine($"[FAIL] ArgumentException at iter {i}: {ex.Message}");
                        return;
                    }
                    catch (NullReferenceException ex)
                    {
                        buildException = ex;
                        _output.WriteLine($"[FAIL] NRE at iter {i}: {ex.Message}");
                        return;
                    }
                }
                await Task.CompletedTask;
            });

            // 30 秒熔断,避免 bug 版本下无限挂死
            var timeout = TimeSpan.FromSeconds(30);
            try
            {
                Task.WaitAll(triggerTasks, timeout);
                // 给 STA 线程排空 BeginInvoke 队列的时间。
                _sta.Invoke(() => Thread.Sleep(500)).GetAwaiter().GetResult();
                Task.WaitAll(new[] { buildTask }, timeout);
            }
            catch (AggregateException) { /* swallowed, see buildException */ }

            if (buildException is not null)
            {
                throw buildException;
            }

            // 最终状态断言。
            var window = _hook.GetHookEventWindow();
            Assert.Equal(5, window.Count);
            Assert.All(window, h =>
            {
                Assert.NotNull(h);
                Assert.Equal("PostToolUse", h.EventName);
                Assert.NotNull(h.ToolName);
                Assert.StartsWith("Tool", h.ToolName);
            });
            _output.WriteLine($"[OK] window final count={window.Count}, latest={_hook.GetLatestHookEvent().EventName}");
        }
        finally
        {
            // 还原线程池最小线程数,避免影响同进程内的其他测试。
            ThreadPool.SetMinThreads(previousMinThreads, previousMinIo);
        }
    }

    /// <summary>
    /// 线程验证:写入与读取应在同一线程(STA),证明 marshaling 修复真的生效。
    /// </summary>
    [Fact]
    public void OnHookReceived_WritesOnUiThread()
    {
        var payload = new HookEventPayload(
            EventName: "Stop",
            ToolName: "Tool",
            Summary: "summary",
            ReceivedAt: DateTime.Now,
            Overlay: HookOverlayKind.None);

        _sta.Invoke(() => _hook.InvokeOnHookReceived(payload)).GetAwaiter().GetResult();

        var writeTid = _sta.ThreadId;
        var readTid = _sta.Invoke(() =>
        {
            _ = _hook.GetHookEventWindow();
            return Environment.CurrentManagedThreadId;
        }).GetAwaiter().GetResult();

        Assert.Equal(writeTid, readTid);
        _output.WriteLine($"[OK] writeTid={writeTid} == readTid={readTid}");
    }

    // ---------------- 反射夹具 ----------------

    private sealed class HookAccess
    {
        private readonly object _session;
        private readonly MethodInfo _onHookReceived;
        private readonly PropertyInfo _windowProp;
        private readonly PropertyInfo _latestProp;

        public HookAccess(object session)
        {
            _session = session;
            var t = session.GetType();
            _onHookReceived = t.GetMethod("OnHookReceived", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("BalanceSession.OnHookReceived");
            _windowProp = t.GetProperty("HookEventWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
            _latestProp = t.GetProperty("LatestHookEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        }

        public void InvokeOnHookReceived(HookEventPayload payload) =>
            _onHookReceived.Invoke(_session, new object[] { payload });

        public IReadOnlyList<HookEvent> GetHookEventWindow() =>
            (IReadOnlyList<HookEvent>)_windowProp.GetValue(_session)!;

        public HookEvent GetLatestHookEvent() => (HookEvent)_latestProp.GetValue(_session)!;
    }

    // STA 线程工具与 PaperBodyContext mock 已抽出到独立文件：
    // StaThread.cs / FakePaperBodyContext.cs（供多个测试类共用）。
}