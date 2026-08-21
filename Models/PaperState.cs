namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 每张 paper的独立状态：当前选哪个供应商。写入 per-paper StateJson（与全局 settings 隔离）。
/// Provider 常量同时被 Session / SettingsReader / Web 消息解析共用，集中在此避免散落。
/// </summary>
internal sealed record PaperState(string Provider)
{
    public const string DefaultProvider = "deepseek";
    public const string DeepSeek = "deepseek";
    public const string MiniMax = "minimax";
    public const string OpenCode = "opencode";
}