namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 全局设置 DTO（按当前 provider 取对应的 API Key；非当前供应商的 Key 也保留以备切换）。
/// 字段顺序与默认值与原 BalanceSession.ReadSettings 一一对应，重构不改变任何默认行为。
///
/// 字段顺序说明：前 7 个字段保持 v1.2.0 兼容性（已存在的 settings.json 仍能解析）。
/// DisableRing 与 ZhiPu/MiMo/CodeX Key 是 setting.md 新增项，C# 继续读，但 plugin.json 不再
/// 声明 CurrencySymbol / BalanceThreshold / ShowPercentage / MiniViewFontFamily 四个"高级"字段，
/// 保留 C# 解析路径以便老用户 settings.json 中的这些字段被读到。
/// </summary>
internal sealed record BalanceSettings(
    string ApiKey,
    string UsageToken,
    int PollSeconds,
    string CurrencySymbol,
    double BalanceThreshold,
    bool ShowPercentage,
    string MiniViewFontFamily,
    bool DisableRing,
    string ZhiPuApiKey,
    string MiMoApiKey,
    string CodeXApiKey);