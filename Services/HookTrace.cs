using System.IO;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// hook overlay 诊断日志：写到 %TEMP%\api-balance-hook.log。
/// 用户可以 cat 该文件看 hook 是否真接到 view,以及哪一步出问题。
/// </summary>
internal static class HookTrace
{
    private static readonly object _gate = new();

    public static void Write(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "api-balance-hook.log");
            var line = $"{System.DateTime.Now:HH:mm:ss.fff} [{System.Environment.CurrentManagedThreadId}] {msg}\n";
            lock (_gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }
}
