namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// hook 事件触发的胶囊临时覆盖类型：
/// - None: 默认（不覆盖，仅追加 ToolTip 第二行）
/// - StopImage / PermissionImage / FailureImage: 显示 PNG（停留时间由 settings 控制）
/// - PreToolSpinner / PostToolSpinner: 显示蓝色旋转沙漏 + 固定文本（一直显示直到下次 Update）
/// </summary>
internal enum HookOverlayKind
{
    None,
    StopImage,
    PermissionImage,
    FailureImage,
    PreToolSpinner,
    PostToolSpinner
}

/// <summary>
/// Claude Code hook 事件载荷：从 HTTP hook POST 接收并归一化。
/// 来源：用户配置的 ~/.claude/settings.json hook 脚本（脚本从 stdin 读 JSON 后转发）。
/// 时间窗口：200ms 内从 hook 触发到 BalanceSession 缓存。
/// </summary>
internal sealed record HookEvent(
    string EventName,
    string? ToolName,
    string Summary,
    DateTime ReceivedAt,
    HookOverlayKind Overlay)
{
    /// <summary>空状态：未收到任何 hook 或服务器关闭。</summary>
    public static readonly HookEvent Empty = new(
        EventName: "",
        ToolName: null,
        Summary: "",
        ReceivedAt: DateTime.MinValue,
        Overlay: HookOverlayKind.None);
}
