using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// platform.deepseek.com 用量 / 消费客户端（上月 + 本月合并拉取）。
/// FetchUsageForRecentMonthsAsync 返回每日 token 用量（用于缓存命中率可视化）；
/// FetchCostForRecentMonthsAsync 返回每日消费 + 今日各模型明细（用于三列卡片）。
/// </summary>
internal sealed class UsageProvider
{
    private readonly HttpClient _http;

    public UsageProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 拉取上个月 + 本月两个月的每日用量并合并，供前端按"今天/昨天/近 7 天/近 30 天/本月/上月/自定义"筛选。
    /// </summary>
    public async Task<UsageDay[]?> FetchUsageForRecentMonthsAsync(string token, DateTime now)
    {
        var thisMonth = new DateTime(now.Year, now.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var currentTask = FetchUsageAsync(token, thisMonth.Year, thisMonth.Month);
        var lastTask = FetchUsageAsync(token, lastMonth.Year, lastMonth.Month);
        await Task.WhenAll(currentTask, lastTask).ConfigureAwait(false);
        var current = currentTask.Result;
        var last = lastTask.Result;
        if (current == null && last == null)
        {
            return null;
        }
        var list = new List<UsageDay>();
        if (last != null) list.AddRange(last);
        if (current != null) list.AddRange(current);
        return list.ToArray();
    }

    /// <summary>
    /// 拉取上个月 + 本月的每日消费并合并，同时保留今日各模型明细。
    /// </summary>
    public async Task<(CostDay[]? Days, Dictionary<string, double>? TodayByModel)>
        FetchCostForRecentMonthsAsync(string token, DateTime now)
    {
        var thisMonth = new DateTime(now.Year, now.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var currentTask = FetchCostAsync(token, thisMonth.Year, thisMonth.Month);
        var lastTask = FetchCostAsync(token, lastMonth.Year, lastMonth.Month);
        await Task.WhenAll(currentTask, lastTask).ConfigureAwait(false);
        var current = currentTask.Result;
        var last = lastTask.Result;
        if (current == null && last == null)
        {
            return (null, null);
        }
        var list = new List<CostDay>();
        if (last != null) list.AddRange(last.Days);
        if (current != null) list.AddRange(current.Days);
        return (list.ToArray(), current?.TodayByModel);
    }

    /// <summary>单月用量（平台 deepseek 用量接口）。</summary>
    public async Task<UsageDay[]?> FetchUsageAsync(string token, int year, int month)
    {
        var url =
            $"https://platform.deepseek.com/api/v0/usage/amount?month={month:D2}&year={year}";
        var body = await HttpFetcher.FetchJsonAsync(_http, url, token, platformHeader: true);
        return body == null ? null : ParseUsageResponse(body);
    }

    /// <summary>单月消费（平台 deepseek 消费接口）。</summary>
    public async Task<CostParseResult?> FetchCostAsync(string token, int year, int month)
    {
        var url =
            $"https://platform.deepseek.com/api/v0/usage/cost?month={month:D2}&year={year}";
        var body = await HttpFetcher.FetchJsonAsync(_http, url, token, platformHeader: true);
        return body == null ? null : ParseCostResponse(body);
    }

    /// <summary>
    /// 解析用量接口响应，汇总每天的 token 总量。
    /// 响应：{ data: { biz_data: { days: [ { date, data: [ { model, usage: [ { type, amount } ] } ] } ] } } }
    /// </summary>
    private static UsageDay[]? ParseUsageResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("biz_data", out var biz) ||
                !biz.TryGetProperty("days", out var days) ||
                days.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<UsageDay>();
            foreach (var day in days.EnumerateArray())
            {
                var date = day.TryGetProperty("date", out var d) &&
                           d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                if (date == null)
                {
                    continue;
                }
                double total = 0;
                double hit = 0;
                double miss = 0;
                if (day.TryGetProperty("data", out var dataArr) &&
                    dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modelUsage in dataArr.EnumerateArray())
                    {
                        if (!modelUsage.TryGetProperty("usage", out var usageArr) ||
                            usageArr.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }
                        foreach (var entry in usageArr.EnumerateArray())
                        {
                            var type = entry.TryGetProperty("type", out var t) &&
                                       t.ValueKind == JsonValueKind.String
                                ? t.GetString()
                                : null;
                            if (!HttpFetcher.IsTokenType(type))
                            {
                                continue;
                            }
                            if (entry.TryGetProperty("amount", out var a) &&
                                a.ValueKind == JsonValueKind.String &&
                                double.TryParse(a.GetString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var v))
                            {
                                total += v;
                                if (type == "PROMPT_CACHE_HIT_TOKEN")
                                {
                                    hit += v;
                                }
                                else if (type == "PROMPT_CACHE_MISS_TOKEN")
                                {
                                    miss += v;
                                }
                            }
                        }
                    }
                }
                result.Add(new UsageDay(date, total, hit, miss));
            }
            return result.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析消费接口响应，汇总每天的金额；同时保留今日各模型明细。
    /// 响应：{ data: { biz_data: [ { days: [ { date, data: [ { model, usage: [ { type, amount } ] } ] } ] } ] } }
    /// amount 为元；费用类型与用量一致，逐条汇总即可。
    /// </summary>
    private static CostParseResult? ParseCostResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("biz_data", out var bizArr) ||
                bizArr.ValueKind != JsonValueKind.Array ||
                !bizArr.EnumerateArray().Any())
            {
                return null;
            }
            var biz = bizArr.EnumerateArray().First();
            var daily = new List<CostDay>();
            Dictionary<string, double>? todayByModel = null;
            var todayKey = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!biz.TryGetProperty("days", out var days) ||
                days.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var day in days.EnumerateArray())
            {
                var date = day.TryGetProperty("date", out var d) &&
                           d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                if (date == null)
                {
                    continue;
                }
                double total = 0;
                var perModel = new Dictionary<string, double>();
                if (day.TryGetProperty("data", out var dataArr) &&
                    dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modelUsage in dataArr.EnumerateArray())
                    {
                        var model = modelUsage.TryGetProperty("model", out var m) &&
                                    m.ValueKind == JsonValueKind.String
                            ? m.GetString() ?? ""
                            : "";
                        if (!modelUsage.TryGetProperty("usage", out var usageArr) ||
                            usageArr.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }
                        double modelTotal = 0;
                        foreach (var entry in usageArr.EnumerateArray())
                        {
                            var type = entry.TryGetProperty("type", out var t) &&
                                       t.ValueKind == JsonValueKind.String
                                ? t.GetString()
                                : null;
                            if (!HttpFetcher.IsTokenType(type))
                            {
                                continue;
                            }
                            if (entry.TryGetProperty("amount", out var a) &&
                                a.ValueKind == JsonValueKind.String &&
                                double.TryParse(a.GetString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var v))
                            {
                                total += v;
                                modelTotal += v;
                            }
                        }
                        if (modelTotal > 0)
                        {
                            perModel[model] = modelTotal;
                        }
                    }
                }
                daily.Add(new CostDay(date, total));
                if (date == todayKey)
                {
                    todayByModel = perModel;
                }
            }
            return new CostParseResult(daily.ToArray(), todayByModel);
        }
        catch
        {
            return null;
        }
    }
}