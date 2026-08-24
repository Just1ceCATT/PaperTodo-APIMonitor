namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 每张 paper的独立状态：当前选哪个供应商。写入 per-paper StateJson（与全局 settings 隔离）。
/// Provider 常量同时被 Session / SettingsReader / Web 消息解析共用，集中在此避免散落。
/// ZhiPu / MiMo / CodeX 是预留入口（与 OpenCode Go 同模式），监视面板返回「尚未适配该供应商」，
/// Key 已存全局 settings，但 BalanceSession 暂不发起真实请求。
/// </summary>
internal sealed record PaperState(string Provider)
{
    public const string DefaultProvider = "deepseek";
    public const string DeepSeek = "deepseek";
    public const string MiniMax = "minimax";
    public const string OpenCode = "opencode";
    public const string ZhiPu = "zhipu";
    public const string MiMo = "mimo";
    public const string CodeX = "codex";
}