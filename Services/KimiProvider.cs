using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// Kimi For Coding 用量客户端。
///
/// 端点：https://api.kimi.com/coding/v1/usages
/// 鉴权：Authorization: Bearer &lt;api_key&gt;（标准 Bearer）。
///
/// 响应结构：
/// {
///   "limits": [{ "detail": { "limit": 100.0, "remaining": 87.7,
///                             "resetTime": "2026-08-25T00:00:00Z" } }],
///   "usage":  { "limit": 1000.0, "remaining": 522.0,
///                "resetTime": "2026-08-31T00:00:00Z" }
/// }
///
/// 分类：
/// - limits[] → 5h（会话窗口，取第一条 detail）
/// - usage   → weekly_limit
///
/// utilization = (limit - remaining) / limit × 100；归一化为 remaining = 100 - utilization。
///
/// resetTime 走 RFC3339 DateTime.Parse（HttpFetcher.TryReadNumber 不支持日期）。
/// </summary>
internal sealed class KimiProvider
{
    private const string KimiUsageUrl = "https://api.kimi.com/coding/v1/usages";

    /// <summary>单条 ModelRemains（Model="default"），与 MiniMax / ZhiPu 同构。</summary>
    public List<(string Model, double Percent, double Hours,
        double WeeklyPercent, double WeeklyHours)>? ModelRemains { get; private set; }

    /// <summary>5h 剩余百分比 0-100。</summary>
    public double? RemainingPercent { get; private set; }

    /// <summary>Kimi 不返回套餐等级；始终 null。</summary>
    public string? CredentialLevel => null;

    private readonly HttpClient _http;

    public KimiProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>拉取 Kimi 用量。失败返回 BalanceSnapshot.Error。</summary>
    public async Task<BalanceSnapshot> FetchBalanceAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return BalanceSnapshot.Empty("未配置 Kimi API Key");
        }
        var (status, body) = await HttpFetcher.FetchRawAsync(
            _http, KimiUsageUrl, authToken: apiKey, authBearer: true).ConfigureAwait(false);
        if (status is null)
        {
            return BalanceSnapshot.Error("请求失败");
        }
        if (status == 401)
        {
            return BalanceSnapshot.Error("API Key 无效或已过期");
        }
        if (status == 403)
        {
            return BalanceSnapshot.Error("API Key 无权访问该接口");
        }
        if (status >= 500)
        {
            return BalanceSnapshot.Error("Kimi 服务异常");
        }
        if (status is 400 or 404 or 422)
        {
            return BalanceSnapshot.Error($"请求被拒（HTTP {status}）");
        }
        if (body == null)
        {
            return BalanceSnapshot.Error("请求失败");
        }
        return ParseResponse(body);
    }

    /// <summary>
    /// 解析 Kimi /usages 响应：
    /// - limits[0].detail → 5h (limit, remaining, resetTime)
    /// - usage           → weekly (limit, remaining, resetTime)
    /// remaining / limit × 100 直接是剩余百分比；resetTime 走 DateTime.Parse。
    /// </summary>
    private BalanceSnapshot ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;
            var fiveHour = ReadQuota(root, "limits", 0, now);
            var weekly = ReadQuota(root, "usage", -1, now);
            if (fiveHour is null && weekly is null)
            {
                return BalanceSnapshot.Error("Kimi 响应无有效数据");
            }
            var fiveHourPct = fiveHour?.RemainingPct ?? 0;
            var weeklyPct = weekly?.RemainingPct ?? 0;
            var fiveHourHours = fiveHour?.HoursToReset ?? 0;
            var weeklyHours = weekly?.HoursToReset ?? 0;
            RemainingPercent = fiveHourPct;
            ModelRemains = new()
            {
                ("default", fiveHourPct, fiveHourHours, weeklyPct, weeklyHours)
            };
            return BalanceSnapshot.Ok(fiveHourPct);
        }
        catch
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
        }
    }

    /// <summary>
    /// 从 root.{property}[index].detail 或 root.{property}.detail 读 (limit, remaining, resetTime)。
    /// index=-1 表示直接读 property 顶层对象（usage 是单数）。
    /// 返回 null 表示该桶无数据。
    /// </summary>
    private static (double RemainingPct, double HoursToReset)? ReadQuota(
        JsonElement root, string property, int index, DateTimeOffset now)
    {
        if (!root.TryGetProperty(property, out var container) ||
            container.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            return null;
        }
        JsonElement detail;
        if (container.ValueKind == JsonValueKind.Array)
        {
            if (index < 0 || index >= container.GetArrayLength())
            {
                return null;
            }
            var entry = container[index];
            if (entry.ValueKind != JsonValueKind.Object) return null;
            if (!entry.TryGetProperty("detail", out detail) ||
                detail.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }
        else
        {
            detail = container;
        }
        var limit = HttpFetcher.TryReadNumber(detail, "limit");
        var remaining = HttpFetcher.TryReadNumber(detail, "remaining");
        if (!limit.HasValue || !remaining.HasValue || limit.Value <= 0)
        {
            return null;
        }
        var remainingPct = Math.Clamp(remaining.Value / limit.Value * 100, 0, 100);
        var hours = TryReadResetHours(detail, "resetTime", now);
        return (remainingPct, hours);
    }

    /// <summary>resetTime 是 RFC3339 字符串；解析失败时返回 0（不阻塞配额展示）。</summary>
    private static double TryReadResetHours(JsonElement obj, string prop, DateTimeOffset now)
    {
        if (!obj.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return 0;
        }
        var raw = v.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }
        if (DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
        {
            return Math.Max(0, (reset - now).TotalHours);
        }
        return 0;
    }
}
