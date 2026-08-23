namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 趋势分析用的余额样本：只有余额 API 请求成功且余额有效时才创建。
/// 与 BalanceSnapshot 分开——后者带 StatusText 且包含失败态，喂进趋势会污染数据。
/// </summary>
internal readonly record struct BalanceSample(DateTime Timestamp, double Balance);
