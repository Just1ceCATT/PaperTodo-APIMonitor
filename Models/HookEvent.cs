namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// Claude Code hook 事件载荷：从 HTTP hook POST 接收并归一化。
/// 来源：用户配置的 ~/.claude/settings.json hook 脚本（脚本从 stdin 读 JSON 后转发）。
/// 时间窗口：200ms 内从 hook 触发到 BalanceSession 缓存。
/// </summary>
internal sealed record HookEvent(
    string EventName,
    string? ToolName,
    string Summary,
    DateTime ReceivedAt)
{
    /// <summary>空状态：未收到任何 hook 或服务器关闭。</summary>
    public static readonly HookEvent Empty = new(
        EventName: "",
        ToolName: null,
        Summary: "",
        ReceivedAt: DateTime.MinValue);
}
