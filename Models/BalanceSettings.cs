namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 全局设置 DTO（按当前 provider 取对应的 API Key；非当前供应商的 Key 也保留以备切换）。
/// 字段顺序与默认值与原 BalanceSession.ReadSettings 一一对应，重构不改变任何默认行为。
/// </summary>
internal sealed record BalanceSettings(
    string ApiKey,
    string UsageToken,
    int PollSeconds,
    string CurrencySymbol,
    double BalanceThreshold,
    bool ShowPercentage,
    string MiniViewFontFamily);