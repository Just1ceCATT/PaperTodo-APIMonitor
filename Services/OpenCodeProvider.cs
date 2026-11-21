using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// OpenCode Go 订阅用量客户端。
///
/// 端点：GET https://opencode.ai/zen/go/v1/usage
/// 鉴权：Authorization: Bearer &lt;api_key&gt;。
/// 注意：推理端 /messages 只认 x-api-key，用量端只认 Bearer，两者不能互换。
///
/// 响应只返回三个时间窗口的「已用百分比」，不回传金额：
/// {
///   "usage": {
///     "rolling": { "status": "ok", "percent": 23, "resetsAt": "2026-11-21T08:00:00.000Z" },
///     "weekly":  { "status": "ok", "percent": 41, "resetsAt": "..." },
///     "monthly": { "status": "ok", "percent": 12, "resetsAt": "..." }
///   }
/// }
///
/// 归一化：上游 percent 是已用百分比，转换为 remaining = 100 - percent，
/// 与 MiniMax / ZhiPu / Kimi 的「剩余百分比」语义保持一致，供胶囊 / MiniView 直接复用。
/// status == "rate-limited" 时上游已把 percent 钉在 100，无需特判。
/// percent == 0 时上游 resetsAt 是「当前时间 + 窗口时长」的占位值，不是真实重置时间，
/// 本类会丢弃它（HoursToReset = 0），前端据此不展示倒计时。
/// </summary>
internal sealed class OpenCodeProvider
{
    private const string OpenCodeUsageUrl = "https://opencode.ai/zen/go/v1/usage";

    /// <summary>
    /// 与 MiniMax / ZhiPu / Kimi 同构的单条 modelRemains（Model="default"）：
    /// 5h 剩余% + 5h 重置时长 + 周剩余% + 周重置时长，供 MiniView 双进度条与 Web 面板复用。
    /// </summary>
    public List<(string Model, double Percent, double Hours,
        double WeeklyPercent, double WeeklyHours)>? ModelRemains { get; private set; }

    /// <summary>主窗口（优先 rolling，其次 weekly / monthly）的剩余百分比 0-100。</summary>
    public double? RemainingPercent { get; private set; }

    /// <summary>月窗口剩余百分比 0-100；响应缺少 monthly 窗口时为 null。</summary>
    public double? MonthlyPercent { get; private set; }

    /// <summary>月窗口距重置的小时数；percent == 0 或缺少窗口时为 null/0。</summary>
    public double? MonthlyHours { get; private set; }

    /// <summary>OpenCode 不返回套餐等级；始终 null。保留接口统一。</summary>
    public string? CredentialLevel => null;

    private readonly HttpClient _http;

    public OpenCodeProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>拉取 OpenCode Go 订阅用量。失败返回 BalanceSnapshot.Error。</summary>
    public async Task<BalanceSnapshot> FetchBalanceAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return BalanceSnapshot.Empty("未配置 OpenCode API Key");
        }
        var (status, body) = await HttpFetcher.FetchRawAsync(
            _http, OpenCodeUsageUrl, authToken: apiKey, authBearer: true).ConfigureAwait(false);
        if (status is null)
        {
            return BalanceSnapshot.Error("请求失败");
        }
        if (status == 401)
        {
            return BalanceSnapshot.Error("API Key 无效或已过期");
        }
        // 403 语义是「Key 有效但没有订阅」，与 401 的鉴权失败区分开。
        if (status == 403)
        {
            return BalanceSnapshot.Error("API Key 有效，但没有 OpenCode Go 订阅");
        }
        if (status >= 500)
        {
            return BalanceSnapshot.Error("OpenCode 服务异常");
        }
        if (status is < 200 or >= 300)
        {
            return BalanceSnapshot.Error($"OpenCode 请求失败（HTTP {status}）");
        }
        if (body == null)
        {
            return BalanceSnapshot.Error("请求失败");
        }
        return ApplyParsed(ParseUsage(body, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 把解析结果写入实例缓存并返回余额快照。
    /// 注意 BalanceSnapshot.Remaining 在这里表示「主窗口剩余百分比」，
    /// 与 MiniMax 的「剩余小时数」口径不同，胶囊文本会按 provider 单独格式化。
    /// </summary>
    private BalanceSnapshot ApplyParsed(ParsedUsage parsed)
    {
        if (!parsed.HasAny)
        {
            return BalanceSnapshot.Error("无法识别的 OpenCode 用量响应结构");
        }
        var rolling = parsed.Rolling;
        var weekly = parsed.Weekly;
        var monthly = parsed.Monthly;
        // 正常情况 rolling 一定存在；缺失时依次回退，保证胶囊仍有百分比可展示。
        var primaryRemaining = rolling?.RemainingPercent
            ?? weekly?.RemainingPercent
            ?? monthly?.RemainingPercent
            ?? 0;
        var weeklyRemaining = weekly?.RemainingPercent ?? 0;
        RemainingPercent = primaryRemaining;
        ModelRemains = new()
        {
            ("default", primaryRemaining, rolling?.HoursToReset ?? 0,
                weeklyRemaining, weekly?.HoursToReset ?? 0)
        };
        MonthlyPercent = monthly?.RemainingPercent;
        MonthlyHours = monthly?.HoursToReset;
        return BalanceSnapshot.Ok(primaryRemaining);
    }

    /// <summary>
    /// 解析 OpenCode Go 三窗口响应。逐窗口防御：缺失 / 畸形窗口跳过，不影响其他窗口。
    /// 顶层没有 usage 时返回三个 null（调用方据此报「无法识别的响应结构」）。
    /// now 由调用方注入，便于单元测试固定时间。
    /// </summary>
    internal static ParsedUsage ParseUsage(string body, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (!doc.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return new ParsedUsage(null, null, null);
            }
            return new ParsedUsage(
                ReadWindow(usage, "rolling", now),
                ReadWindow(usage, "weekly", now),
                ReadWindow(usage, "monthly", now));
        }
        catch
        {
            return new ParsedUsage(null, null, null);
        }
    }

    /// <summary>
    /// 读取单个窗口：percent 缺失 / 非法时返回 null；percent 支持数字与字符串数字。
    /// 上游 percent 是已用百分比，这里同时换算 remaining 与 reset 小时数。
    /// </summary>
    private static UsageWindow? ReadWindow(JsonElement usage, string key, DateTimeOffset now)
    {
        if (!usage.TryGetProperty(key, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var used = HttpFetcher.TryReadNumber(window, "percent");
        if (!used.HasValue || !double.IsFinite(used.Value))
        {
            return null;
        }
        var usedPercent = Math.Clamp(used.Value, 0, 100);
        var remainingPercent = 100 - usedPercent;
        // percent == 0 时上游 resetsAt 是占位值，丢弃，不展示倒计时。
        var hours = usedPercent > 0 ? ReadResetHours(window, "resetsAt", now) : 0;
        return new UsageWindow(usedPercent, remainingPercent, hours);
    }

    /// <summary>
    /// 读取 resetsAt：兼容 ISO 8601 字符串、秒级 / 毫秒级 Unix 时间戳。
    /// 解析失败、时间为 0/负值或已过期时返回 0（不阻塞百分比展示）。
    /// </summary>
    private static double ReadResetHours(JsonElement window, string property, DateTimeOffset now)
    {
        if (!window.TryGetProperty(property, out var value))
        {
            return 0;
        }
        DateTimeOffset reset;
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw) ||
                !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out reset))
            {
                return 0;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 value.TryGetDouble(out var numeric) && numeric > 0)
        {
            // 秒级时间戳约 1.7e9，毫秒级约 1.7e12；以 1e12 为界区分。
            var milliseconds = numeric > 1e12 ? numeric : numeric * 1000;
            if (milliseconds > long.MaxValue)
            {
                return 0;
            }
            reset = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
        }
        else
        {
            return 0;
        }
        var hours = (reset - now).TotalHours;
        return hours > 0 ? hours : 0;
    }

    /// <summary>单个时间窗口的归一化结果。UsedPercent 为上游已用百分比。</summary>
    internal readonly record struct UsageWindow(
        double UsedPercent,
        double RemainingPercent,
        double HoursToReset);

    /// <summary>三个窗口的解析结果；任一窗口缺失/畸形时为 null。</summary>
    internal sealed record ParsedUsage(
        UsageWindow? Rolling,
        UsageWindow? Weekly,
        UsageWindow? Monthly)
    {
        public bool HasAny => Rolling.HasValue || Weekly.HasValue || Monthly.HasValue;
    }
}
