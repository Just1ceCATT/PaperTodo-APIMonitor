using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ApiBalanceMonitor;

/// <summary>
/// 余额监测插件：拉取 DeepSeek /user/balance 接口，
/// 在胶囊中显示「绿/黄/红圆环 + 货币 + 余额 + 可选百分比」。
///
/// 实现要点（不修改宿主）：
/// - 胶囊由宿主 1.6 模板渲染（ProgressRing + Text），文本使用宿主自家胶囊字体，
///   与宿主其它胶囊完全一致；宿主（v4.0.0 起）支持胶囊宽度随内容自适应，不会截断。
/// - 设置项由宿主自带的"插件"设置页绘制（boolean / string / number / select 四类）。
/// - 鉴权信息（apiKey）会随设置写入 plugins/data/api.balance.monitor.json（明文），
///   因此在 plugin.json 的 description 中明确告知用户并建议使用只读子 key。
/// </summary>
public sealed class ApiBalanceMonitorPlugin : IPaperBodyPlugin
{
    public string Id => "api.balance.monitor";
    public string DisplayName => "API 余额监测";
    public string Description =>
        "通过 DeepSeek /user/balance 接口拉取余额，按余额提醒阈值显示不同颜色的圆环。";
    public Version Version => new(1, 1, 0);
    public string ApiVersion => "1.7";
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new BalanceSession(context);
}

internal sealed record BalanceSettings(
    string ApiKey,
    string UsageToken,
    int PollSeconds,
    string CurrencySymbol,
    double BalanceThreshold,
    bool ShowPercentage);

internal sealed record BalanceSnapshot(
    double Remaining,
    double Total,
    bool HasRemaining,
    bool HasTotal,
    string StatusText)
{
    public static BalanceSnapshot Empty(string status) =>
        new(double.NaN, double.NaN, false, false, status);

    public static BalanceSnapshot Error(string status) =>
        new(double.NaN, double.NaN, false, false, "错误：" + status);

    public static BalanceSnapshot Ok(double remaining, double total) =>
        new(remaining, total, !double.IsNaN(remaining), !double.IsNaN(total), string.Empty);
}

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
/// 单小时 Token 用量（来自 /v1/usage 接口的小时粒度明细）。
/// </summary>
internal sealed record HourlyUsage(string Date, int Hour, double Tokens);

internal sealed class BalanceSession : IPaperBodySession
{
    private readonly PaperBodyContext _context;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private BalanceSettings _settings;
    private BalanceSnapshot _snapshot = BalanceSnapshot.Empty("尚未拉取");
    private string _lastCapsuleSignature = "";
    private int _polling;
    private UsageDay[]? _usageDays;
    private CostDay[]? _costDays;
    private HourlyUsage[]? _hourlyUsage;
    private PaperBodyTheme _theme;

    // WebView2 监视面板
    private Grid _viewRoot = null!;
    private WebView2CompositionControl _webView = null!;
    private readonly object _environmentGate = new();
    private Task<CoreWebView2Environment>? _environmentTask;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _webViewInitializationStarted;
    private bool _webViewReady;
    private bool _documentReady;
    private string? _pendingPayload;
    private bool _disposed;

    public BalanceSession(PaperBodyContext context)
    {
        _context = context;
        _theme = context.Body.Theme;
        _settings = ReadSettings(context.SettingsJson);

        BuildWebView();

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PaperTodo.Plugin.ApiBalanceMonitor/1.0");

        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += async (_, _) => await PollAsync();

        ApplySettings(_settings);
        // WebView2 在 View 首次布局后初始化（TryStartWebView），构造时不主动拉取，
        // 等 timer 首次触发，避免阻塞宿主启动。
    }

    public FrameworkElement View => _viewRoot;

    public void Commit() { /* 设置由宿主管理，正文无草稿 */ }
    public void RefreshFromModel() { /* 无外部数据源需要刷新 */ }
    public void CancelInteractions() { /* 无交互状态 */ }
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _timer.Stop();
        _http.Dispose();
        try
        {
            _webView?.Dispose();
        }
        catch
        {
        }
    }

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnVisibilityChanged(bool visible)
    {
        // 折叠成胶囊后仍按 backgroundUpdates 继续轮询，无需特别处理。
    }
    public void OnPresentationChanged(bool expanded) { }
    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        PushView();
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);
    public void OnDpiChanged() { }

    public void OnSettingsChanged(string settingsJson)
    {
        ApplySettings(ReadSettings(settingsJson));
    }

    // ---------------- 设置解析 ----------------

    private static BalanceSettings ReadSettings(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            return new BalanceSettings(
                ReadString(root, "apiKey", ""),
                ReadString(root, "usageToken", ""),
                ReadInt(root, "pollSeconds", 60),
                ReadString(root, "currencySymbol", "¥"),
                ReadDouble(root, "balanceThreshold", 20.0),
                ReadBool(root, "showPercentage", true));
        }
        catch
        {
            return new BalanceSettings("", "", 60, "¥", 20.0, true);
        }
    }

    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out var n))
        {
            return n;
        }
        return fallback;
    }

    private static double ReadDouble(JsonElement root, string name, double fallback)
    {
        if (root.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) &&
                double.IsFinite(n))
            {
                return n;
            }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var s) &&
                double.IsFinite(s))
            {
                return s;
            }
        }
        return fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    // ---------------- 设置应用 ----------------

    private void ApplySettings(BalanceSettings s)
    {
        _settings = s;
        var interval = TimeSpan.FromSeconds(
            Math.Max(15, Math.Min(3600, s.PollSeconds)));
        _timer.Interval = interval;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
            // 启动后立即拉一次（不等满 interval）
            _ = PollAsync();
        }
        else
        {
            // 配置变更后也立即重拉
            _ = PollAsync();
        }
    }

    // ---------------- HTTP 拉取 ----------------

    // DeepSeek 余额接口固定写死，不再允许用户配置 URL。
    private const string DeepSeekBalanceUrl = "https://api.deepseek.com/user/balance";

    private async Task PollAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            UpdateSnapshot(BalanceSnapshot.Empty("未配置 API Key"));
            return;
        }

        // 并发保护：上一次请求尚未完成时跳过本次，避免请求堆积与相互取消。
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            // 余额 / 用量 / 消费并行拉取（v3.1 收集方式）；用量 Token 未配置时只拉余额。
            // 用量与消费拉取上个月 + 本月，覆盖所有预置时段（近 30 天 / 本月 / 上月）。
            var now = DateTime.Now;
            var balanceTask = FetchBalanceAsync();
            var usageTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<UsageDay[]?>(null)
                : FetchUsageForRecentMonthsAsync(_settings.UsageToken, now);
            var costTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<CostDay[]?>(null)
                : FetchCostForRecentMonthsAsync(_settings.UsageToken, now);
            // 小时粒度用量（/v1/usage，用 API Key），用于"今天/昨天/单日"按 2 小时分柱。
            // 小时粒度用量：探测过所有常用端点均无小时粒度数据（只 /v1/usage 返回 404，
            // platform/usage/amount 无论是否带 hour/granularity 等参数都返回日粒度），
            // 因此这里保留调用链与容错，失败时单日时段自动回退到单根日柱。
            var hourlyTask = string.IsNullOrWhiteSpace(_settings.ApiKey)
                ? Task.FromResult<HourlyUsage[]?>(null)
                : FetchHourlyUsageAsync(_settings.ApiKey);

            await Task.WhenAll(balanceTask, usageTask, costTask, hourlyTask).ConfigureAwait(true);
            _usageDays = usageTask.Result;
            _costDays = costTask.Result;
            _hourlyUsage = hourlyTask.Result;
            UpdateSnapshot(balanceTask.Result);
        }
        catch (TaskCanceledException)
        {
            // HttpClient 超时在 .NET 中以 TaskCanceledException 呈现（而非 TimeoutException），
            // 单独捕获并给出友好提示，而不是把异常类型名展示给用户。
            UpdateSnapshot(BalanceSnapshot.Error("请求超时，请检查网络连接"));
        }
        catch (Exception ex)
        {
            UpdateSnapshot(BalanceSnapshot.Error(ex.GetType().Name));
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task<BalanceSnapshot> FetchBalanceAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekBalanceUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseResponse(body);
        }
        catch (Exception ex)
        {
            return BalanceSnapshot.Error(ex.GetType().Name);
        }
    }

    /// <summary>
    /// 拉取上个月 + 本月两个月的每日用量并合并，供前端按"今天/昨天/近 7 天/近 30 天/本月/上月/自定义"筛选。
    /// </summary>
    private async Task<UsageDay[]?> FetchUsageForRecentMonthsAsync(string token, DateTime now)
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
    /// 拉取上个月 + 本月的每日消费并合并，供"近 7 天消费"统计。
    /// </summary>
    private async Task<CostDay[]?> FetchCostForRecentMonthsAsync(string token, DateTime now)
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
            return null;
        }
        var list = new List<CostDay>();
        if (last != null) list.AddRange(last);
        if (current != null) list.AddRange(current);
        return list.ToArray();
    }

    /// <summary>
    /// v3.1 收集方式：调用 platform.deepseek.com 消费接口拉取指定月份每日消费（元）。
    /// </summary>
    private async Task<CostDay[]?> FetchCostAsync(string token, int year, int month)
    {
        try
        {
            var url =
                $"https://platform.deepseek.com/api/v0/usage/cost?month={month:D2}&year={year}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("x-app-version", "1.0.0");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseCostResponse(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析消费接口响应，汇总每天的金额。
    /// 响应：{ data: { biz_data: [ { days: [ { date, data: [ { usage: [ { type, amount } ] } ] } ] } ] } }
    /// amount 为元；费用类型与用量一致，逐条汇总即可。
    /// </summary>
    private static CostDay[]? ParseCostResponse(string body)
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
            var result = new List<CostDay>();
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
                            if (type is not ("PROMPT_TOKEN" or "PROMPT_CACHE_HIT_TOKEN"
                                or "PROMPT_CACHE_MISS_TOKEN" or "RESPONSE_TOKEN"))
                            {
                                continue;
                            }
                            if (entry.TryGetProperty("amount", out var a) &&
                                a.ValueKind == JsonValueKind.String &&
                                double.TryParse(a.GetString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var v))
                            {
                                total += v;
                            }
                        }
                    }
                }
                result.Add(new CostDay(date, total));
            }
            return result.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉取小时粒度用量（公开接口 /v1/usage，用 API Key 认证）。
    /// 用于"今天 / 昨天 / 自定义单日"时段按 2 小时分柱绘制柱状图。
    /// 该端点字段可能随平台调整，解析做了容错；失败返回 null（前端回退单日柱）。
    /// </summary>
    private async Task<HourlyUsage[]?> FetchHourlyUsageAsync(string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/v1/usage");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseHourlyUsage(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 /v1/usage 响应，把 items 转成 (日期, 小时, token 数)。
    /// 字段名做多候选容错：时间戳尝试 timestamp/created/date 等，token 取 prompt+completion。
    /// </summary>
    private static HourlyUsage[]? ParseHourlyUsage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<HourlyUsage>();
            foreach (var item in items.EnumerateArray())
            {
                var time = TryReadTimestamp(item);
                if (time == null)
                {
                    continue;
                }
                double tokens = 0;
                TryReadTokens(item, ref tokens);
                list.Add(new HourlyUsage(
                    time.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    time.Value.Hour,
                    tokens));
            }
            return list.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement item)
    {
        foreach (var key in new[] { "timestamp", "created_at", "created", "time", "date" })
        {
            if (!item.TryGetProperty(key, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var raw))
            {
                // 毫秒或秒级 Unix 时间戳
                return raw > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw);
            }
            if (v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    v.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static void TryReadTokens(JsonElement item, ref double tokens)
    {
        foreach (var key in new[] { "prompt_tokens", "completion_tokens" })
        {
            if (item.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.Number &&
                v.TryGetDouble(out var n))
            {
                tokens += n;
            }
        }
    }

    /// <summary>
    /// v3.1 收集方式：调用 platform.deepseek.com 用量接口拉取指定月份每日 Token 用量。
    /// </summary>
    private async Task<UsageDay[]?> FetchUsageAsync(string token, int year, int month)
    {
        try
        {
            var url =
                $"https://platform.deepseek.com/api/v0/usage/amount?month={month:D2}&year={year}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("x-app-version", "1.0.0");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseUsageResponse(body);
        }
        catch
        {
            return null;
        }
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
                            if (type is not ("PROMPT_TOKEN" or "PROMPT_CACHE_HIT_TOKEN"
                                or "PROMPT_CACHE_MISS_TOKEN" or "RESPONSE_TOKEN"))
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

    private BalanceSnapshot ParseResponse(string body)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
        }

        // DeepSeek /user/balance 响应：
        //   { "is_sufficient": true, "balance_infos": [
        //     { "currency": "CNY", "total_balance": "84.00",
        //       "granted_balance": "0.00", "topped_up_balance": "84.00" },
        //     { "currency": "USD", ... } ] }
        // total_balance 是字符串数字；按 currencySymbol 选币种（¥→CNY，$→USD），
        // 找不到匹配时回退到第一个非零余额；不计算百分比。
        if (!root.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array)
        {
            return BalanceSnapshot.Error("缺少 balance_infos");
        }
        var targetCurrency = MapCurrencySymbolToCode(_settings.CurrencySymbol);
        double? picked = null;
        double? firstNonZero = null;
        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object) continue;
            var amount = TryReadNumber(info, "total_balance");
            if (!amount.HasValue) continue;
            var code = info.TryGetProperty("currency", out var cv) &&
                cv.ValueKind == JsonValueKind.String
                    ? cv.GetString()
                    : null;
            if (targetCurrency != null &&
                string.Equals(code, targetCurrency, StringComparison.OrdinalIgnoreCase))
            {
                picked = amount;
                break;
            }
            if (!firstNonZero.HasValue && amount.Value > 0)
            {
                firstNonZero = amount;
            }
        }
        if (!picked.HasValue) picked = firstNonZero;
        if (picked.HasValue)
        {
            return BalanceSnapshot.Ok(picked.Value, double.NaN);
        }
        return BalanceSnapshot.Error("未找到 total_balance");
    }

    private static double? TryReadNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return ExtractNumber(value);
    }

    private static double? ExtractNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var n) => n,
        JsonValueKind.String when double.TryParse(
            value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
        _ => null
    };

    /// <summary>
    /// 把设置里的货币符号映射为 DeepSeek balance_infos[*].currency 的币种代码。
    /// 找不到映射返回 null，调用方会回退到第一个非零余额。
    /// </summary>
    private static string? MapCurrencySymbolToCode(string symbol) => symbol switch
    {
        "¥" => "CNY",
        "$" => "USD",
        "€" => "EUR",
        "£" => "GBP",
        "₩" => "KRW",
        _ => null
    };

    // ---------------- 快照 & 胶囊渲染 ----------------

    private void UpdateSnapshot(BalanceSnapshot snapshot)
    {
        _snapshot = snapshot;

        // v3.1 算法：risk = threshold / balance（"阈值占余额的比例"）。
        // 例：余额=120、阈值=20 → 0.167 Safe（绿）；余额=40、阈值=20 → 0.5 Warming 边缘（黄）；
        //     余额=20、阈值=20 → 1.0 Overrun（红，满圆）。
        double riskRatio = ComputeRiskRatio(snapshot.Remaining, _settings.BalanceThreshold);
        var ringColor = RingColor(riskRatio);
        var ringArc = RingArcValue(riskRatio);

        var text = BuildCapsuleText(snapshot, _settings, riskRatio);
        var signature = text + "|" + riskRatio.ToString("F3", CultureInfo.InvariantCulture) + "|" + ringColor + "|" + snapshot.StatusText;
        if (!string.Equals(signature, _lastCapsuleSignature, StringComparison.Ordinal))
        {
            // 胶囊只在内容真正变化时更新，避免无谓的宿主布局抖动。
            _lastCapsuleSignature = signature;
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = EstimateCapsuleWidth(text),
                PlainText = text,
                ToolTip = string.IsNullOrEmpty(snapshot.StatusText)
                    ? text
                    : $"{text}\n{snapshot.StatusText}",
                Components = new[]
                {
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.ProgressRing,
                        Value = ringArc,
                        Color = ringColor,
                        Width = 18
                    },
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = text,
                        Fill = true
                    }
                }
            });
        }

        // 面板（HTML）每次拉取后都推送：余额可能不变但用量/时间变了。
        PushView();
    }

    /// <summary>
    /// 估算胶囊内容宽度（DIP）并给足余量。
    /// 宿主 1.6 模板固定占位约 35px（左右 padding 12 + ProgressRing 18 + 间距 5），
    /// 文本按平均字符宽 7px 估算 + 6px 余量，既避免边缘胶囊截断，又保持胶囊紧凑。
    /// </summary>
    private static double EstimateCapsuleWidth(string text) =>
        Math.Ceiling(35 + text.Length * 7.0 + 6);

    /// <summary>
    /// 胶囊文本：货币符号 + 余额 +（可选）百分比，v3.1 风格 "¥12.34 · 6%"。
    /// 文本由宿主 1.6 模板用宿主胶囊字体渲染；宿主按 PreferredWidth 给定内容宽度，
    /// 配合估算余量，" · " 分隔不会截断。
    /// </summary>
    private static string BuildCapsuleText(
        BalanceSnapshot snapshot,
        BalanceSettings settings,
        double riskRatio)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(settings.CurrencySymbol))
        {
            sb.Append(settings.CurrencySymbol);
        }
        // 无数据时 FormatAmount(NaN) 输出 "—"。
        sb.Append(FormatAmount(snapshot.Remaining));
        if (settings.ShowPercentage && snapshot.HasRemaining && !double.IsNaN(snapshot.Remaining))
        {
            var percent = (int)Math.Round(
                Math.Clamp(riskRatio, 0, 1) * 100.0, MidpointRounding.AwayFromZero);
            sb.Append(" · ");
            sb.Append(percent.ToString(CultureInfo.CurrentCulture));
            sb.Append('%');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 风险比例 v3.1 语义：threshold / balance。
    /// balance <= 0 或 threshold <= 0 → 视为未配置，返回 0。
    /// </summary>
    private static double ComputeRiskRatio(double balance, double threshold)
    {
        if (threshold <= 0 || balance <= 0)
        {
            return 0;
        }
        var raw = threshold / balance;
        return raw < 0 ? 0 : raw;
    }

    private enum RiskState { NoThreshold, Safe, Warming, Danger, Overrun }

    private static RiskState ClassifyRisk(double ratio)
    {
        if (ratio >= 1.0) return RiskState.Overrun;
        if (ratio >= 0.8) return RiskState.Danger;
        if (ratio >= 0.5) return RiskState.Warming;
        return RiskState.Safe;
    }

    /// <summary>
    /// v3.1 颜色：Safe 绿 / Warming 黄 / Danger 橙 / Overrun 红 / NoThreshold 灰。
    /// 宿主 ProgressRing 只接受单色，放弃 v3.1 的颜色渐变。
    /// </summary>
    private static string RingColor(double ratio) => ClassifyRisk(ratio) switch
    {
        RiskState.Overrun     => "#F44336",
        RiskState.Danger      => "#FF9800",
        RiskState.Warming     => "#FFC107",
        RiskState.Safe        => "#4CAF50",
        _                     => "#9E9E9E"
    };

    /// <summary>
    /// 宿主 ProgressRing 的 Value：Overrun 时传 1（满弧），其余钳到 [0,1]。
    /// </summary>
    private static double RingArcValue(double ratio)
    {
        if (ratio >= 1.0) return 1.0;
        return Math.Clamp(ratio, 0, 1);
    }
    private static string FormatAmount(double amount)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount))
        {
            return "—";
        }
        // v3.1 风格：整数省小数位，否则保留 2 位小数。
        var asDecimal = (decimal)amount;
        return asDecimal % 1m == 0
            ? asDecimal.ToString("F0", CultureInfo.CurrentCulture)
            : asDecimal.ToString("F2", CultureInfo.CurrentCulture);
    }

    // 风险色（v3.1 语义）
    private static readonly Color RiskGreen = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color RiskYellow = Color.FromRgb(0xFF, 0xC1, 0x07);
    private static readonly Color RiskOrange = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color RiskRed = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color RiskGray = Color.FromRgb(0x9E, 0x9E, 0x9E);

    // ---------------- WebView2 监视面板 ----------------

    /// <summary>
    /// 构建正文容器：插件自建 WebView2，加载本地 web/index.html 渲染监视面板。
    /// 数据由 C# 侧拉取后通过 PostWebMessageAsJson 推给页面 JS，页面自身不发网络请求
    /// （规避 WebView2 的 DenyCors / CORS 限制，且不修改宿主）。
    /// </summary>
    private void BuildWebView()
    {
        _webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = true
        };

        _viewRoot = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _viewRoot.Children.Add(_webView);
        _viewRoot.Loaded += OnViewRootLoaded;
        _viewRoot.SizeChanged += OnViewRootSizeChanged;
    }

    private void OnViewRootLoaded(object sender, RoutedEventArgs e) => TryStartWebView();

    private void OnViewRootSizeChanged(object sender, SizeChangedEventArgs e) => TryStartWebView();

    private void TryStartWebView()
    {
        if (_webViewInitializationStarted ||
            _disposed ||
            !_viewRoot.IsLoaded ||
            _viewRoot.ActualWidth <= 0 ||
            _viewRoot.ActualHeight <= 0)
        {
            return;
        }
        _webViewInitializationStarted = true;
        _viewRoot.SizeChanged -= OnViewRootSizeChanged;
        _ = InitializeWebViewAsync(_lifetime.Token);
    }

    private async Task InitializeWebViewAsync(CancellationToken token)
    {
        try
        {
            var environment = await GetWebViewEnvironmentAsync();
            token.ThrowIfCancellationRequested();

            await _webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();

            var core = _webView.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 初始化失败。");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationCompleted += OnWebViewNavigationCompleted;
            core.ProcessFailed += OnWebViewProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;

            var pluginDirectory =
                Path.GetDirectoryName(typeof(ApiBalanceMonitorPlugin).Assembly.Location)
                ?? AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(pluginDirectory, "web", "monitor.html")))
            {
                throw new InvalidOperationException("缺少 web/monitor.html。");
            }

            // 对齐宿主 web 插件方式：虚拟主机映射到插件目录，避免 file:// 的潜在限制。
            const string hostName = "papertodo.balance.monitor.local";
            core.SetVirtualHostNameToFolderMapping(
                hostName,
                pluginDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // HTML body 是透明的，背景必须透明才能融入便签底色，否则显示白底块。
            try
            {
                _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            catch
            {
                // 个别环境不支持时忽略，仅影响背景色。
            }

            _webViewReady = true;
            core.Navigate($"https://{hostName}/web/monitor.html");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            // WebView2 初始化失败不致命：胶囊仍由宿主渲染，仅展开面板不可用。
            _webViewInitializationStarted = false;
        }
    }

    private async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        Task<CoreWebView2Environment> task;
        lock (_environmentGate)
        {
            task = _environmentTask ??= CreateWebViewEnvironmentAsync();
        }
        try
        {
            return await task;
        }
        catch
        {
            lock (_environmentGate)
            {
                if (ReferenceEquals(_environmentTask, task))
                {
                    _environmentTask = null;
                }
            }
            throw;
        }
    }

    private static Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        var pluginDirectory =
            Path.GetDirectoryName(typeof(ApiBalanceMonitorPlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var userDataFolder = Path.Combine(pluginDirectory, ".runtime", "webview2");
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: null);
    }

    private void OnWebViewNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }
        _documentReady = true;
        if (_pendingPayload != null)
        {
            var payload = _pendingPayload;
            _pendingPayload = null;
            PostPayload(payload);
        }
    }

    private void OnWebViewProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        // 渲染进程异常退出时重新加载页面恢复。
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            _documentReady = false;
            try
            {
                _webView.CoreWebView2?.Reload();
            }
            catch
            {
            }
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 页面 JS 就绪后发送 {type:"ready"}，宿主立即补发最新数据。
        // 页面能发出 ready 说明消息监听已挂载，无需再等 NavigationCompleted。
        try
        {
            if (e.WebMessageAsJson.IndexOf("\"ready\"", StringComparison.Ordinal) >= 0 &&
                _webViewReady)
            {
                PostPayload(BuildViewPayload());
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 把最新主题与数据推给 HTML 面板。WebView2 未初始化或页面未就绪时缓存，
    /// 就绪后自动补发。
    /// </summary>
    private void PushView()
    {
        if (_disposed)
        {
            return;
        }
        var payload = BuildViewPayload();
        if (!_webViewReady || !_documentReady)
        {
            // WebView2 未初始化或页面未就绪时缓存最新数据，
            // 就绪后由 OnWebViewNavigationCompleted 补发。
            _pendingPayload = payload;
            return;
        }
        PostPayload(payload);
    }

    private void PostPayload(string payload)
    {
        // 主通道：postMessage 事件。
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(payload);
        }
        catch
        {
        }
        // 备用通道：直接调用页面暴露的 __renderBalance，绕过消息监听。
        try
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"window.__renderBalance && window.__renderBalance({payload});");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 组装推给 HTML 的 JSON：{ theme, data }。data 含状态、余额、风险、用量、更新时间。
    /// </summary>
    private string BuildViewPayload()
    {
        var status = _snapshot.StatusText ?? "";
        var hasData = _snapshot.HasRemaining && !double.IsNaN(_snapshot.Remaining);
        var ratio = ComputeRiskRatio(_snapshot.Remaining, _settings.BalanceThreshold);
        var riskColor = ToHex(RiskColor(ratio));

        string statusKind;
        if (string.IsNullOrWhiteSpace(status))
        {
            statusKind = "ok";
        }
        else if (status.StartsWith("错误：", StringComparison.Ordinal))
        {
            statusKind = "error";
        }
        else
        {
            statusKind = "warn";
        }

        var theme = new Dictionary<string, object?>
        {
            ["dark"] = _theme.IsDark,
            ["text"] = NormalizeHex(_theme.TextColor, "#202020"),
            ["weak"] = NormalizeHex(_theme.WeakTextColor, "#707070"),
            ["accent"] = NormalizeHex(_theme.AccentColor, "#B07A31"),
            ["paper"] = NormalizeHex(_theme.PaperColor, "#FFF8E6"),
            ["fontScale"] = Math.Clamp(_theme.FontScale, 0.85, 1.2)
        };

        var data = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["statusKind"] = statusKind,
            ["hasBalance"] = hasData,
            ["balance"] = hasData ? FormatAmount(_snapshot.Remaining) : "—",
            ["currency"] = MapCurrencySymbolToCode(_settings.CurrencySymbol) ?? _settings.CurrencySymbol,
            ["currencySymbol"] = _settings.CurrencySymbol,
            ["ratio"] = ratio,
            ["riskColor"] = riskColor,
            ["riskState"] = RiskStateText(ratio),
            ["threshold"] = _settings.BalanceThreshold > 0
                ? $"{_settings.CurrencySymbol}{FormatAmount(_settings.BalanceThreshold)}"
                : "未设置提醒阈值，可在设置页配置",
            ["updateTime"] = hasData
                ? "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "",
            ["costToday"] = BuildCostTodayText(),
            ["costTodayFoot"] = BuildCostTodayFoot(),
            ["cost7d"] = BuildCost7dText(),
            ["cost7dFoot"] = BuildCost7dFoot(),
            ["costDays7"] = BuildCostDays7Array(),
            ["cacheRate"] = BuildTodayCacheRate(),
            ["hourly"] = BuildHourlyArray(),
            ["usage"] = BuildUsageArray()
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["theme"] = theme,
            ["data"] = data
        });
    }

    /// <summary>
    /// 今日（当天）消费文本；无数据返回空字符串。
    /// </summary>
    private string BuildCostTodayText()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return "";
        }
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = Array.Find(_costDays, c => c.Date == key);
        if (day == null || day.Cost <= 0)
        {
            return "";
        }
        return _settings.CurrencySymbol + day.Cost.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 今日 vs 昨日消费变化箭头文案：上升"↑xx.x%"，下降"↓xx.x%"，持平"→0.0%"。
    /// 无数据或昨日为 0 时返回空。
    /// </summary>
    private string BuildCostTodayFoot()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return "";
        }
        var now = DateTime.Now;
        var todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterdayKey = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = Array.Find(_costDays, c => c.Date == todayKey);
        var yesterday = Array.Find(_costDays, c => c.Date == yesterdayKey);
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
    /// 小时粒度用量数组（date + hour + tokens），供前端单日时段按 2 小时分柱。
    /// </summary>
    private object[] BuildHourlyArray()
    {
        if (_hourlyUsage == null || _hourlyUsage.Length == 0)
        {
            return Array.Empty<object>();
        }
        return _hourlyUsage
            .Select(h => (object)new Dictionary<string, object?>
            {
                ["date"] = h.Date,
                ["hour"] = h.Hour,
                ["tokens"] = h.Tokens
            })
            .ToArray();
    }

    /// <summary>
    /// 今日缓存命中率（0~1）；当天无缓存数据返回 null。
    /// </summary>
    private double? BuildTodayCacheRate()
    {
        if (_usageDays == null || _usageDays.Length == 0)
        {
            return null;
        }
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = Array.Find(_usageDays, u => u.Date == key);
        if (day == null || (day.CacheHit + day.CacheMiss) <= 0)
        {
            return null;
        }
        return day.CacheHit / (day.CacheHit + day.CacheMiss);
    }

    /// <summary>
    /// 近 7 天（含今天）消费总额文本；无数据返回空字符串。
    /// </summary>
    private string BuildCost7dText()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return "";
        }
        var now = DateTime.Now;
        var start = now.AddDays(-6).Date;
        double total = 0;
        for (var d = start; d <= now.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(_costDays, c => c.Date == key);
            if (day != null)
            {
                total += day.Cost;
            }
        }
        if (total <= 0)
        {
            return "";
        }
        return _settings.CurrencySymbol + total.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 近 7 天日均消费文案（"日均 ¥X.XX"，两位小数）。
    /// 只要消费数据源存在就返回（可能为 ¥0.00）；完全无数据时才隐藏。
    /// </summary>
    private string BuildCost7dFoot()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return "";
        }
        var now = DateTime.Now;
        var start = now.AddDays(-6).Date;
        double total = 0;
        for (var d = start; d <= now.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(_costDays, c => c.Date == key);
            if (day != null)
            {
                total += day.Cost;
            }
        }
        var avg = total / 7.0;
        return "日均 " + _settings.CurrencySymbol +
            avg.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 近 7 天每日消费明细数组（date + 格式化金额），供"近 7 天消费"悬停展开。
    /// </summary>
    private object[] BuildCostDays7Array()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return Array.Empty<object>();
        }
        var now = DateTime.Now;
        var start = now.AddDays(-6).Date;
        var items = new List<Dictionary<string, object?>>();
        for (var d = start; d <= now.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(_costDays, c => c.Date == key);
            items.Add(new Dictionary<string, object?>
            {
                ["date"] = d.ToString("MM-dd", CultureInfo.InvariantCulture),
                ["costText"] = _settings.CurrencySymbol +
                    (day != null
                        ? day.Cost.ToString("0.00", CultureInfo.CurrentCulture)
                        : "0.00")
            });
        }
        return items.Cast<object>().ToArray();
    }

    /// <summary>
    /// 全量每日 Token 用量数组（含 date 字段），供 HTML 按所选时段筛选渲染柱状图。
    /// </summary>
    private object[] BuildUsageArray()
    {
        if (_usageDays == null || _usageDays.Length == 0)
        {
            return Array.Empty<object>();
        }
        var items = new List<Dictionary<string, object?>>();
        foreach (var day in _usageDays.OrderBy(u => u.Date, StringComparer.Ordinal))
        {
            items.Add(new Dictionary<string, object?>
            {
                ["date"] = day.Date,
                ["tokens"] = day.Tokens
            });
        }
        return items.Cast<object>().ToArray();
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return value.StartsWith("#") ? value : "#" + value;
    }

    /// <summary>
    /// v3.1 风险环颜色（含过渡渐变）：绿 → 黄 → 橙 → 红；未配置阈值时灰。
    /// </summary>
    private static Color RiskColor(double ratio)
    {
        if (ratio <= 0)
        {
            return RiskGray;
        }
        if (ratio >= 1.0)
        {
            return RiskRed;
        }
        if (ratio >= 0.8)
        {
            return Lerp(RiskYellow, RiskOrange, (ratio - 0.8) / 0.2);
        }
        if (ratio >= 0.5)
        {
            return Lerp(RiskGreen, RiskYellow, (ratio - 0.5) / 0.3);
        }
        return RiskGreen;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    /// <summary>
    /// 风险状态文案（v3.1 语义）。
    /// </summary>
    private static string RiskStateText(double ratio) => ratio switch
    {
        <= 0 => "未配置提醒阈值",
        >= 1.0 => "余额低于提醒阈值",
        >= 0.8 => "余额偏低",
        >= 0.5 => "接近提醒阈值",
        _ => "余额充足"
    };

}
