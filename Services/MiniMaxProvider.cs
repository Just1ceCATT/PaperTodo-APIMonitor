using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// MiniMax Coding Plan 余额客户端。
/// MiniMax 同时返回所有模型（model_remains），本 provider：
/// - 收集全部 modelList（供 ViewPayload modelRemains 字段）；
/// - 优先选 "general" 模型作为主余额；找不到时回退到首个；
/// - 暴露 RemainingPercent（供 CapsuleRiskRatioForCurrent 计算"已消耗比例"）。
/// </summary>
internal sealed class MiniMaxProvider
{
    private const string MiniMaxRemainsUrl =
        "https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains";

    /// <summary>MiniMax 各模型剩余额度，供 HtmlPayload modelRemains 字段使用。</summary>
    public List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? ModelRemains { get; private set; }

    /// <summary>general 模型（或首个）剩余百分比 0-100；BalanceSession 用来算"已消耗比例"。</summary>
    public double? RemainingPercent { get; private set; }

    private readonly HttpClient _http;

    public MiniMaxProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>拉取 Coding Plan 余额。失败返回 BalanceSnapshot.Error。</summary>
    public async Task<BalanceSnapshot> FetchBalanceAsync(string apiKey)
    {
        var body = await HttpFetcher.FetchJsonAsync(_http, MiniMaxRemainsUrl, apiKey);
        return body == null ? BalanceSnapshot.Error("请求失败") : ParseResponse(body);
    }

    /// <summary>
    /// 解析 MiniMax Coding Plan 响应。
    /// 实测结构：{ "model_remains": [ { "model_name": "general", "remains_time": <毫秒>,
    ///   "current_interval_remaining_percent": <0-100>, ... }, ... ], "base_resp": {...} }
    /// 取 general 模型（coding plan 主模型），余额 = 剩余时长（小时）。
    /// </summary>
    private BalanceSnapshot ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("model_remains", out var remains) ||
                remains.ValueKind != JsonValueKind.Array)
            {
                return BalanceSnapshot.Error("未找到 model_remains");
            }
            JsonElement best = default;
            var found = false;
            var modelList = new List<(
                string Model, double Percent, double Hours,
                double WeeklyPercent, double WeeklyHours)>();
            foreach (var m in remains.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var name = m.TryGetProperty("model_name", out var n) &&
                           n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : "";
                var ms = HttpFetcher.TryReadNumber(m, "remains_time");
                var pct = HttpFetcher.TryReadNumber(m, "current_interval_remaining_percent");
                var weeklyPct = HttpFetcher.TryReadNumber(m, "current_weekly_remaining_percent");
                var weeklyMs = HttpFetcher.TryReadNumber(m, "weekly_remains_time");
                if (ms.HasValue)
                {
                    modelList.Add((
                        string.IsNullOrEmpty(name) ? "model" : name,
                        pct ?? 100,
                        ms.Value / 3600000.0,
                        weeklyPct ?? 100,
                        (weeklyMs ?? 0) / 3600000.0));
                }
                if (!found || name == "general")
                {
                    best = m;
                    found = true;
                }
            }
            ModelRemains = modelList;
            if (!found)
            {
                return BalanceSnapshot.Error("无模型数据");
            }
            var remainsMs = HttpFetcher.TryReadNumber(best, "remains_time");
            var percent = HttpFetcher.TryReadNumber(best, "current_interval_remaining_percent");
            if (!remainsMs.HasValue)
            {
                return BalanceSnapshot.Error("未找到剩余额度");
            }
            RemainingPercent = percent ?? 100;
            var hours = remainsMs.Value / 3600000.0;
            return BalanceSnapshot.Ok(hours);
        }
        catch
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
        }
    }
}