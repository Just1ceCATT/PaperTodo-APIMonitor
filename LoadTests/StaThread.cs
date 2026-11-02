using System.Windows.Threading;

namespace PaperTodo.Plugin.ApiBalanceMonitor.LoadTests;

/// <summary>
/// 测试用 STA 线程工具：起一条带 WPF Dispatcher 消息循环的 STA 线程，
/// 让 BalanceSession 等依赖 WPF 线程亲和性的类型能在测试里构造和驱动。
///
/// 从 HookConcurrencyTests 抽出为独立文件，供多个测试类共用。
/// </summary>
internal sealed class StaThread : IDisposable
{
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new();
    private int _threadId;

    public int ThreadId => _threadId;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _threadId = Environment.CurrentManagedThreadId;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        })
        {
            Name = "LoadTests-STA",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _dispatcher!.BeginInvoke(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public async Task RunAsync(Func<Task> func)
    {
        await Invoke(func).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _dispatcher?.InvokeShutdown(); } catch { }
    }
}
