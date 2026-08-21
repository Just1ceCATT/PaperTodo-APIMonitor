using System.Globalization;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Payload;

/// <summary>
/// 派生数据构造器：从已缓存的当日 / 当月 / 模型数组，派生 HTML 监视面板需要的文本与对象数组。
/// 所有方法 pure static，字段值由调用方传入（避免与 Session 直接耦合）。
/// </summary>
internal static class MetricsAggregator
{
    /// <summary>今日消费金额（¥XX.XX）；无数据返回空。</summary>
    public static string BuildCostTodayText(CostDay[]? costDays, string currencySymbol)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return "";
        }
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = Array.Find(costDays, c => c.Date == key);
        if (day == null || day.Cost <= 0)
        {
            return "";
        }
        return currencySymbol + day.Cost.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>今日 vs 昨日消费变化文案（↑/↓/→）；无昨日数据时返回空。</summary>
    public static string BuildCostTodayFoot(CostDay[]? costDays)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return "";
        }
        var now = DateTime.Now;
        var todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterdayKey = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = Array.Find(costDays, c => c.Date == todayKey);
        var yesterday = Array.Find(costDays, c => c.Date == yesterdayKey);
        if (today == null || yesterday == null || yesterday.Cost <= 0)
        {
            return "";
        }
        var diff = (today.Cost - yesterday.Cost) / yesterday.Cost * 100.0;
        var arrow = diff > 0 ? "↑" : (diff < 0 ? "↓" : "→");
        return "相较昨日 " + arrow +
            Math.Abs(diff).ToString("0.0", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>
    /// 今日 vs 昨日消费变化:(Direction, Percent)。Direction ∈ {"up","down","flat",null}。
    /// 当任一日缺失或昨日=0 时返回 (null, 0),调用方按 null 决定隐藏涨跌指示。
    /// </summary>
    public static (string? Direction, double Percent) BuildCostChange(CostDay[]? costDays)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return (null, 0);
        }
        var now = DateTime.Now;
        var todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterdayKey = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = Array.Find(costDays, c => c.Date == todayKey);
        var yesterday = Array.Find(costDays, c => c.Date == yesterdayKey);
        if (today == null || yesterday == null || yesterday.Cost <= 0)
        {
            return (null, 0);
        }
        var diff = (today.Cost - yesterday.Cost) / yesterday.Cost * 100.0;
        var dir = diff > 0 ? "up" : (diff < 0 ? "down" : "flat");
        return (dir, Math.Abs(diff));
    }

    /// <summary>
    /// 近 N 天(默认 7)每日消费金额数组,从最早到最新排列,缺日补 0;无数据返回空数组。
    /// 给 WPF sparkline 用:View 端按 max 归一化到控件高度,渲染折线。
    /// </summary>
    public static double[] BuildCostSparkline(CostDay[]? costDays, int days = 7)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return Array.Empty<double>();
        }
        var values = new double[days];
        var now = DateTime.Now.Date;
        for (var i = 0; i < days; i++)
        {
            var key = now.AddDays(-(days - 1 - i)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(costDays, c => c.Date == key);
            values[i] = day?.Cost ?? 0;
        }
        return values;
    }

    /// <summary>今日各模型消费明细（按金额降序）。</summary>
    public static object[] BuildCostTodayByModels(
        Dictionary<string, double>? costTodayByModel,
        string currencySymbol)
    {
        if (costTodayByModel == null || costTodayByModel.Count == 0)
        {
            return Array.Empty<object>();
        }
        return costTodayByModel
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new Dictionary<string, object?>
            {
                ["model"] = kv.Key,
                ["costText"] = currencySymbol + kv.Value.ToString("0.00", CultureInfo.CurrentCulture)
            })
            .ToArray();
    }

    /// <summary>MiniMax 各模型剩余额度（供 minimax.html 渲染）。</summary>
    public static object[] BuildMiniMaxModelRemains(
        List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? modelRemains)
    {
        if (modelRemains == null || modelRemains.Count == 0)
        {
            return Array.Empty<object>();
        }
        return modelRemains
            .Select(x => (object)new Dictionary<string, object?>
            {
                ["model"] = x.Model,
                ["percent"] = Math.Clamp(x.Percent, 0, 100),
                ["hours"] = Math.Round(x.Hours, 1),
                ["weeklyPercent"] = Math.Clamp(x.WeeklyPercent, 0, 100),
                ["weeklyHours"] = Math.Round(x.WeeklyHours, 1)
            })
            .ToArray();
    }

    /// <summary>今日用量明细；当天无数据返回 null。</summary>
    public static UsageDay? FindTodayUsage(UsageDay[]? usageDays)
    {
        if (usageDays == null || usageDays.Length == 0)
        {
            return null;
        }
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Array.Find(usageDays, u => u.Date == key);
    }

    /// <summary>今日总 Token 用量；无数据返回 0。</summary>
    public static double BuildTodayTokens(UsageDay[]? usageDays) => FindTodayUsage(usageDays)?.Tokens ?? 0;

    /// <summary>今日缓存命中 Token 数；无数据返回 0。</summary>
    public static double BuildTodayHit(UsageDay[]? usageDays) => FindTodayUsage(usageDays)?.CacheHit ?? 0;

    /// <summary>今日缓存命中率（0~1）；当天无缓存数据返回 null。</summary>
    public static double? BuildTodayCacheRate(UsageDay[]? usageDays)
    {
        var day = FindTodayUsage(usageDays);
        if (day == null || (day.CacheHit + day.CacheMiss) <= 0)
        {
            return null;
        }
        return day.CacheHit / (day.CacheHit + day.CacheMiss);
    }

    /// <summary>近 7 天（含今天）消费总额；无数据返回 0。</summary>
    public static double SumLast7DaysCost(CostDay[]? costDays)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return 0;
        }
        var now = DateTime.Now;
        var start = now.AddDays(-6).Date;
        double total = 0;
        for (var d = start; d <= now.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(costDays, c => c.Date == key);
            if (day != null)
            {
                total += day.Cost;
            }
        }
        return total;
    }

    /// <summary>近 7 天消费总额文本；无数据返回空。</summary>
    public static string BuildCost7dText(CostDay[]? costDays, string currencySymbol)
    {
        var total = SumLast7DaysCost(costDays);
        if (total <= 0)
        {
            return "";
        }
        return currencySymbol + total.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>近 7 天日均消费文案（"日均 ¥X.XX"）；无数据返回空。</summary>
    public static string BuildCost7dFoot(CostDay[]? costDays, string currencySymbol)
    {
        if (costDays == null || costDays.Length == 0)
        {
            return "";
        }
        var avg = SumLast7DaysCost(costDays) / 7.0;
        return "日均 " + currencySymbol +
            avg.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>全量每日 Token 用量数组（含 date 字段），供 HTML 按所选时段筛选。</summary>
    public static object[] BuildUsageArray(UsageDay[]? usageDays)
    {
        if (usageDays == null || usageDays.Length == 0)
        {
            return Array.Empty<object>();
        }
        var items = new List<Dictionary<string, object?>>();
        foreach (var day in usageDays.OrderBy(u => u.Date, StringComparer.Ordinal))
        {
            items.Add(new Dictionary<string, object?>
            {
                ["date"] = day.Date,
                ["tokens"] = Services.RiskClassifier.Finite(day.Tokens)
            });
        }
        return items.Cast<object>().ToArray();
    }
}