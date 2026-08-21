namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 单日 Token 用量（来自 platform.deepseek.com 用量接口的 days 汇总）。
/// CacheHit / CacheMiss 用于缓存命中率可视化。
/// </summary>
internal sealed record UsageDay(string Date, double Tokens, double CacheHit = 0, double CacheMiss = 0);

/// <summary>
/// 单日消费金额（元，来自 platform.deepseek.com 消费接口的 days 汇总）。
/// </summary>
internal sealed record CostDay(string Date, double Cost);

/// <summary>
/// 消费接口解析结果：每日总额 + 今日各模型明细。
/// TodayByModel 仅在"今天"那天的解析结果中保留，其他日子丢弃。
/// </summary>
internal sealed record CostParseResult(
    CostDay[] Days,
    System.Collections.Generic.Dictionary<string, double>? TodayByModel);