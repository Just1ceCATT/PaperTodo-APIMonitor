using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// 智谱 GLM Coding Plan 余额客户端。
///
/// 端点矩阵（region × planType）：
/// - global + personal → https://api.z.ai/api/monitor/usage/quota/limit
/// - global + team     → https://api.z.ai/api/monitor/usage/quota/limit?type=2
/// - china + personal  → https://open.bigmodel.cn/api/monitor/usage/quota/limit
/// - china + team      → https://open.bigmodel.cn/api/monitor/usage/quota/limit?type=2
///
/// 鉴权：Authorization: &lt;api_key&gt;（**不加 Bearer 前缀**）。
///
/// 响应结构：
/// {
///   "success": true,
///   "data": {
///     "level": "max",
///     "limits": [
///       { "type": "TOKENS_LIMIT", "unit": 3, "percentage": 12.3, "nextResetTime": 1730000000000 },
///       { "type": "TOKENS_LIMIT", "unit": 6, "percentage": 47.8, "nextResetTime": 1730600000000 }
///     ]
///   }
/// }
///
/// 分类规则：unit=3 → FiveHour，unit=6 → Weekly；type ∈ TOKENS_LIMIT / CREDIT_LIMIT。
/// percentage 是"已用%"，归一化为剩余% (100 - x) 以与 MiniMax / Kimi 语义一致。
///
/// 输出：与 MiniMaxProvider 同构的 ModelRemains 单条（Model="default"），便于
/// 现有 MiniView / web/minimax.html 的"找 general → fallback 首条"逻辑直接复用。
/// </summary>
internal sealed class ZhiPuProvider
{
    // unit 字段语义：3 → 5h 会话窗口，6 → 周限额。
    private const int UnitFiveHour = 3;
    private const int UnitWeekly = 6;

    /// <summary>ZhiPu modelRemains 占位条目（Model="default"）。失败时为 null。</summary>
    public List<(string Model, double Percent, double Hours,
        double WeeklyPercent, double WeeklyHours)>? ModelRemains { get; private set; }

    /// <summary>5h 剩余百分比 0-100；BalanceSession 用来算"已消耗比例"与 MiniMax 共用。</summary>
    public double? RemainingPercent { get; private set; }

    /// <summary>套餐等级（max/pro/...），来自 data.level；供 web 面板 header 显示。</summary>
    public string? CredentialLevel { get; private set; }

    private readonly HttpClient _http;

    public ZhiPuProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>拉取 ZhiPu Coding Plan 余额。失败返回 BalanceSnapshot.Error。</summary>
    public async Task<BalanceSnapshot> FetchBalanceAsync(
        string apiKey, string region, string planType)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return BalanceSnapshot.Empty("未配置 ZhiPu API Key");
        }
        var url = BuildQuotaUrl(region, planType);
        var (status, body) = await HttpFetcher.FetchRawAsync(
            _http, url, authToken: apiKey, authBearer: false).ConfigureAwait(false);
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
            return BalanceSnapshot.Error("智谱服务异常");
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

    private static string BuildQuotaUrl(string region, string planType)
    {
        var isChina = string.Equals(region, "china", StringComparison.OrdinalIgnoreCase);
        var baseUrl = isChina
            ? "https://open.bigmodel.cn/api/monitor/usage/quota/limit"
            : "https://api.z.ai/api/monitor/usage/quota/limit";
        var isTeam = string.Equals(planType, "team", StringComparison.OrdinalIgnoreCase);
        return isTeam ? $"{baseUrl}?type=2" : baseUrl;
    }

    /// <summary>
    /// 解析 ZhiPu /quota/limit 响应。
    /// 步骤：
    /// 1. 取 data.level → CredentialLevel
    /// 2. 遍历 limits[]，按 unit 字段归桶（unit=3 → 5h，unit=6 → weekly）
    /// 3. percentage 是已用%，反转成剩余%（remaining = 100 - percentage）
    /// 4. 计算 nextResetTime - now 的差值 → 写入 hours
    /// 5. 把结果塞进单条 ModelRemains（Model="default"）
    /// </summary>
    private BalanceSnapshot ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var successEl) ||
                successEl.ValueKind != JsonValueKind.True)
            {
                var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                return BalanceSnapshot.Error(msg is null ? "智谱返回失败" : $"智谱返回：{msg}");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return BalanceSnapshot.Error("智谱响应缺少 data");
            }
            CredentialLevel = data.TryGetProperty("level", out var lvl) &&
                              lvl.ValueKind == JsonValueKind.String
                ? lvl.GetString()
                : null;
            if (!data.TryGetProperty("limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Array)
            {
                return BalanceSnapshot.Error("智谱响应缺少 limits");
            }
            double? fiveHourRemaining = null;
            double? weeklyRemaining = null;
            double fiveHourHours = 0;
            double weeklyHours = 0;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var entry in limits.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!HttpFetcher.TryReadNumber(entry, "unit").HasValue) continue;
                var unit = (int)HttpFetcher.TryReadNumber(entry, "unit")!.Value;
                var usedPct = HttpFetcher.TryReadNumber(entry, "percentage");
                if (!usedPct.HasValue) continue;
                var remainingPct = Math.Clamp(100 - usedPct.Value, 0, 100);
                var resetMs = HttpFetcher.TryReadNumber(entry, "nextResetTime");
                var hours = resetMs.HasValue
                    ? Math.Max(0, (resetMs.Value - nowMs) / 3600000.0)
                    : 0;
                if (unit == UnitFiveHour)
                {
                    fiveHourRemaining = remainingPct;
                    fiveHourHours = hours;
                }
                else if (unit == UnitWeekly)
                {
                    weeklyRemaining = remainingPct;
                    weeklyHours = hours;
                }
            }
            if (!fiveHourRemaining.HasValue && !weeklyRemaining.HasValue)
            {
                return BalanceSnapshot.Error("智谱响应无有效 limits");
            }
            var fiveHour = fiveHourRemaining ?? 0;
            var weekly = weeklyRemaining ?? 0;
            RemainingPercent = fiveHour;
            ModelRemains = new()
            {
                ("default", fiveHour, fiveHourHours, weekly, weeklyHours)
            };
            return BalanceSnapshot.Ok(fiveHour);
        }
        catch
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
        }
    }
}
