using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ApiBalanceMonitor;

/// <summary>
/// 余额监测插件：拉取 DeepSeek /user/balance 接口，
/// 在胶囊中显示「绿/黄/红圆环 + 货币 + 余额 + 可选百分比」。
///
/// 协议 1.7 自渲染胶囊视图（IPaperCapsuleViewProvider），1.8 自渲染 MiniView
/// （IPaperMiniViewProvider）；设置页由宿主绘制，鉴权 Key 明文写入插件数据文件。
/// </summary>
public sealed class ApiBalanceMonitorPlugin : IPaperBodyPlugin
{
    public string Id => "api.balance.monitor";
    public string DisplayName => "API 余额监测";
    public string Description =>
        "通过 DeepSeek /user/balance 接口拉取余额，按余额提醒阈值显示不同颜色的圆环。" +
        "模型供应商在每张纸的监视面板顶部切换；各供应商 Key 独立存储于全局设置。";
    public Version Version => new(1, 2, 0);
    public string ApiVersion => "1.8";
    public int StateVersion => 2;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new BalanceSession(context);

    /// <summary>
    /// 旧 v1 没有 state 字段；升级后清空回退到默认 deepseek，让用户在监视面板中按需切换。
    /// </summary>
    public string MigrateState(string stateJson, int fromVersion) => "{}";
}

internal sealed record BalanceSettings(
    string ApiKey,
    string UsageToken,
    int PollSeconds,
    string CurrencySymbol,
    double BalanceThreshold,
    bool ShowPercentage,
    string MiniViewFontFamily);

/// <summary>
/// 每张 paper 的独立状态：当前选哪个供应商。写入 per-paper StateJson（与全局 settings 隔离）。
/// </summary>
internal sealed record PaperState(string Provider)
{
    public const string DefaultProvider = "deepseek";
    public const string DeepSeek = "deepseek";
    public const string MiniMax = "minimax";
    public const string OpenCode = "opencode";
}

internal sealed record BalanceSnapshot(
    double Remaining,
    bool HasRemaining,
    string StatusText)
{
    public static BalanceSnapshot Empty(string status) =>
        new(double.NaN, false, status);

    public static BalanceSnapshot Error(string status) =>
        new(double.NaN, false, "错误：" + status);

    public static BalanceSnapshot Ok(double remaining) =>
        new(remaining, !double.IsNaN(remaining), string.Empty);
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

internal sealed class BalanceSession : IPaperBodySession, IPaperCapsuleViewProvider, IPaperMiniViewProvider
{
    private readonly PaperBodyContext _context;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    // 高峰时段哨兵：每 30 秒检查一次 UTC+8 是否进入/离开 9-12 / 14-18 高峰窗口，
    // 让胶囊在 9:00 / 12:00 / 14:00 / 18:00 边界附近 30 秒内自动显隐太阳图标，
    // 不必等下一次数据拉取（默认 pollSeconds=60）。
    private readonly DispatcherTimer _peakCheckTimer;
    private bool _lastIsPeakHour;
    private BalanceSettings _settings;
    private PaperState _state;
    // MiniView 字体覆盖：来自 plugin.json 设置。空字符串表示跟随主题。
    private string _miniViewFontFamily = "";
    private BalanceSnapshot _snapshot = BalanceSnapshot.Empty("尚未拉取");
    private string _lastCapsuleSignature = "";
    private int _polling;
    private UsageDay[]? _usageDays;
    private CostDay[]? _costDays;
    // 今日各模型消费明细：model -> cost（元）。仅保留今日与昨日，便于卡片展示。
    private Dictionary<string, double>? _costTodayByModel;
    private double? _minimaxRemainingPercent;
    private List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? _minimaxModelRemains;
    private PaperBodyTheme _theme;

    // 1.7 胶囊自定义视图：宿主为每个 surface 至多请求一次并缓存，宽度变化时重建。
    // 在 UpdateSnapshot 里原地更新它们，避免 SetCapsulePresentation 触发重建抖动。
    private BalanceCapsuleView? _regularCapsuleView;
    private BalanceCapsuleView? _dockedCapsuleView;
    // 1.8 边缘预览视图：仅 MiniMax 场景保留；非 MiniMax 时 CreateMiniView 返回 null
    // 让宿主切到 1.6/1.7 放大胶囊回退（DescribePluginCapsuleFallback）。
    private BalanceMiniView? _miniView;
    // CreateCapsuleView 在首次被宿主调用前就需要拿到最新状态，所以单独缓存一份快照。
    private string _capsuleText = "—";
    private string _capsuleRingColorHex = "#9E9E9E";
    private double _capsuleRingArc;

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
        _state = ReadState(context.StateJson);
        _settings = ReadSettings(context.SettingsJson, _state.Provider);

        BuildWebView();

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PaperTodo.Plugin.ApiBalanceMonitor/1.0");

        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += async (_, _) => await PollAsync();

        // 30 秒粒度足以覆盖 9:00 / 12:00 / 14:00 / 18:00 四个时段切换点。
        _peakCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _peakCheckTimer.Tick += (_, _) => RefreshPeakHour();

        ApplySettings(_settings);
        // 哨兵始终运行，首次启动同步当前状态。
        RefreshPeakHour();
        if (!_peakCheckTimer.IsEnabled)
        {
            _peakCheckTimer.Start();
        }
        // WebView2 延迟到首次布局后初始化，避免阻塞宿主启动。
    }

    public FrameworkElement View => _viewRoot;

    public void Commit() { /* 设置由宿主管理，正文无草稿 */ }
    public void RefreshFromModel() { /* 无外部数据源需要刷新 */ }
    public void CancelInteractions() { /* 无交互状态 */ }

    /// <summary>
    /// DeepSeek 高峰时段哨兵占位（UTC+8 9-12 / 14-18）：状态变化时复用 UpdateSnapshot。
    /// 当前未驱动 UI 渲染，保留以便后续业务扩展（自定义视图可直接读取 _lastIsPeakHour）。
    /// </summary>
    private void RefreshPeakHour()
    {
        if (_disposed)
        {
            return;
        }
        var isPeakHour = string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal)
            && IsPeakHourUtc8();
        if (isPeakHour == _lastIsPeakHour)
        {
            return;
        }
        _lastIsPeakHour = isPeakHour;
        UpdateSnapshot(_snapshot);
    }
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _timer.Stop();
        _peakCheckTimer.Stop();
        _http.Dispose();
        // 清空 1.7 视图缓存：宿主在下次 body session 重建时会请求新的 view，
        // 旧引用指向的元素已经被宿主丢弃，保留只会徒增引用计数。
        _regularCapsuleView = null;
        _dockedCapsuleView = null;
        _miniView = null;
        try
        {
            _webView?.Dispose();
        }
        catch
        {
            // 释放阶段不应抛异常干扰宿主卸载。
        }
    }

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnVisibilityChanged(bool visible) { }
    public void OnPresentationChanged(bool expanded) { }
    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        _regularCapsuleView?.ApplyTheme(theme);
        _dockedCapsuleView?.ApplyTheme(theme);
        _miniView?.ApplyTheme(theme);
        PushView();
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);
    public void OnDpiChanged() { }

    public void OnSettingsChanged(string settingsJson)
    {
        // Provider 来自 per-paper state，不再从全局 settings 读取。
        ApplySettings(ReadSettings(settingsJson, _state.Provider));
        _miniView?.ApplyTheme(_theme);
    }

    /// <summary>MiniView 字体覆盖（来自设置项 miniViewFontFamily），留空跟随主题。</summary>
    public string MiniViewFontFamily => _miniViewFontFamily;

    // ---------------- 设置解析 ----------------

    private static BalanceSettings ReadSettings(string? json, string provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            // 各供应商的 Key 独立存储；切换供应商读取对应 Key（未填则为空）。
            // 旧版单一 apiKey 字段作为 DeepSeek 的兼容迁移来源。
            var deepseekKey = ReadString(root, "deepseekApiKey", "");
            if (string.IsNullOrEmpty(deepseekKey))
            {
                deepseekKey = ReadString(root, "apiKey", "");
            }
            var minimaxKey = ReadString(root, "minimaxApiKey", "");
            var opencodeKey = ReadString(root, "opencodeApiKey", "");
            var apiKey = provider switch
            {
                PaperState.MiniMax => minimaxKey,
                PaperState.OpenCode => opencodeKey,
                _ => deepseekKey
            };
            return new BalanceSettings(
                apiKey,
                ReadString(root, "usageToken", ""),
                ReadInt(root, "pollSeconds", 60),
                ReadString(root, "currencySymbol", "¥"),
                ReadDouble(root, "balanceThreshold", 20.0),
                ReadBool(root, "showPercentage", true),
                ReadString(root, "miniViewFontFamily", ""));
        }
        catch
        {
            return new BalanceSettings("", "", 60, "¥", 20.0, true, "");
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

    // ---------------- Per-paper state 解析 ----------------

    /// <summary>
    /// 读取 per-paper state：当前选哪个供应商。非法值回退到默认。
    /// </summary>
    private static PaperState ReadState(string? json)
    {
        var fallback = new PaperState(PaperState.DefaultProvider);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var p = ReadString(doc.RootElement, "provider", PaperState.DefaultProvider);
            return new PaperState(IsValidProvider(p) ? p : PaperState.DefaultProvider);
        }
        catch
        {
            return fallback;
        }
    }

    private static string SerializeState(PaperState state) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["provider"] = state.Provider });

    // ---------------- 设置应用 ----------------

    private void ApplySettings(BalanceSettings s)
    {
        _settings = s;
        _miniViewFontFamily = s.MiniViewFontFamily;
        var interval = TimeSpan.FromSeconds(
            Math.Max(15, Math.Min(3600, s.PollSeconds)));
        _timer.Interval = interval;
        // Provider 变化已迁到 SetPaperProvider；此处只处理 timer/重拉。
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
        // 启动 / 配置变更后立即拉一次。
        _ = PollAsync();
    }

    // ---------------- HTTP 拉取 ----------------

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
            // 余额 / 用量 / 消费并行拉取；用量 Token 未配置时只拉余额。
            // 用量与消费拉取上个月 + 本月，覆盖所有预置时段（近 30 天 / 本月 / 上月）。
            var now = DateTime.Now;
            var balanceTask = FetchBalanceAsync();
            var usageTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<UsageDay[]?>(null)
                : FetchUsageForRecentMonthsAsync(_settings.UsageToken, now);
            var costTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<(CostDay[]? Days, Dictionary<string, double>? TodayByModel)>((null, null))
                : FetchCostForRecentMonthsAsync(_settings.UsageToken, now);

            await Task.WhenAll(balanceTask, usageTask, costTask).ConfigureAwait(true);
            _usageDays = usageTask.Result;
            _costDays = costTask.Result.Days;
            _costTodayByModel = costTask.Result.TodayByModel;
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

    /// <summary>
    /// 通用 GET + Bearer 请求：成功返回响应体，请求/网络异常返回 null。
    /// platform 接口需额外加 x-app-version 头（platformHeader: true）。
    /// </summary>
    private async Task<string?> FetchJsonAsync(string url, string token, bool platformHeader = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (platformHeader)
            {
                request.Headers.TryAddWithoutValidation("x-app-version", "1.0.0");
            }
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<BalanceSnapshot> FetchBalanceAsync()
    {
        if (string.Equals(_state.Provider, PaperState.MiniMax, StringComparison.Ordinal))
        {
            return await FetchMiniMaxBalanceAsync().ConfigureAwait(false);
        }
        if (string.Equals(_state.Provider, PaperState.OpenCode, StringComparison.Ordinal))
        {
            return BalanceSnapshot.Error("尚未适配该供应商");
        }
        // DeepSeek（默认）
        var body = await FetchJsonAsync(DeepSeekBalanceUrl, _settings.ApiKey);
        return body == null ? BalanceSnapshot.Error("请求失败") : ParseResponse(body);
    }

    /// <summary>
    /// MiniMax Coding Plan 用量接口：GET /v1/api/openplatform/coding_plan/remains。
    /// 返回各模型的剩余时长（remains_time）与剩余百分比。
    /// </summary>
    private async Task<BalanceSnapshot> FetchMiniMaxBalanceAsync()
    {
        var body = await FetchJsonAsync(
            "https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains",
            _settings.ApiKey);
        return body == null ? BalanceSnapshot.Error("请求失败") : ParseMiniMaxBalanceResponse(body);
    }

    /// <summary>
    /// 解析 MiniMax Coding Plan 响应。
    /// 实测结构：{ "model_remains": [ { "model_name": "general", "remains_time": <毫秒>,
    ///   "current_interval_remaining_percent": <0-100>, ... }, ... ], "base_resp": {...} }
    /// 取 general 模型（coding plan 主模型），余额 = 剩余时长（小时）。
    /// </summary>
    private BalanceSnapshot ParseMiniMaxBalanceResponse(string body)
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
                var ms = TryReadNumber(m, "remains_time");
                var pct = TryReadNumber(m, "current_interval_remaining_percent");
                var weeklyPct = TryReadNumber(m, "current_weekly_remaining_percent");
                var weeklyMs = TryReadNumber(m, "weekly_remains_time");
                if (ms.HasValue)
                {
                    modelList.Add((
                        string.IsNullOrEmpty(name) ? "model" : name,
                        pct ?? 100,
                        ms.Value / 3600000.0,
                        weeklyPct ?? 100,
                        (weeklyMs ?? 0) / 3600000.0));
                }
                // 收集所有模型（不 break）；best 优先取 general。
                if (!found || name == "general")
                {
                    best = m;
                    found = true;
                }
            }
            _minimaxModelRemains = modelList;
            if (!found)
            {
                return BalanceSnapshot.Error("无模型数据");
            }
            var remainsMs = TryReadNumber(best, "remains_time");
            var percent = TryReadNumber(best, "current_interval_remaining_percent");
            if (!remainsMs.HasValue)
            {
                return BalanceSnapshot.Error("未找到剩余额度");
            }
            _minimaxRemainingPercent = percent ?? 100;
            var hours = remainsMs.Value / 3600000.0;
            return BalanceSnapshot.Ok(hours);
        }
        catch
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
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
    /// 拉取上个月 + 本月的每日消费并合并，同时保留今日各模型明细。
    /// </summary>
    private async Task<(CostDay[]? Days, Dictionary<string, double>? TodayByModel)>
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

    /// <summary>
    /// v3.1 收集方式：调用 platform.deepseek.com 消费接口拉取指定月份每日消费（元）。
    /// 返回每日总额 + 今日/昨日各模型明细。
    /// </summary>
    private async Task<CostParseResult?> FetchCostAsync(string token, int year, int month)
    {
        var url =
            $"https://platform.deepseek.com/api/v0/usage/cost?month={month:D2}&year={year}";
        var body = await FetchJsonAsync(url, token, platformHeader: true);
        return body == null ? null : ParseCostResponse(body);
    }

    /// <summary>
    /// 消费接口解析结果：每日总额 + 今日各模型明细。
    /// </summary>
    private sealed record CostParseResult(
        CostDay[] Days,
        Dictionary<string, double>? TodayByModel);

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
                            if (!IsTokenType(type))
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

    /// <summary>
    /// v3.1 收集方式：调用 platform.deepseek.com 用量接口拉取指定月份每日 Token 用量。
    /// </summary>
    private async Task<UsageDay[]?> FetchUsageAsync(string token, int year, int month)
    {
        var url =
            $"https://platform.deepseek.com/api/v0/usage/amount?month={month:D2}&year={year}";
        var body = await FetchJsonAsync(url, token, platformHeader: true);
        return body == null ? null : ParseUsageResponse(body);
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
                            if (!IsTokenType(type))
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
            return BalanceSnapshot.Ok(picked.Value);
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
    /// 是否为计入用量/消费的 token 类型（输入 / 缓存命中 / 缓存未命中 / 输出）。
    /// </summary>
    private static bool IsTokenType(string? type) => type is
        "PROMPT_TOKEN" or "PROMPT_CACHE_HIT_TOKEN" or "PROMPT_CACHE_MISS_TOKEN" or "RESPONSE_TOKEN";

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

    /// <summary>
/// 按主题字体精确测量文本宽度（DIP），与 customView 中 TextBlock 同源以避免亚像素舍入差异。
/// 失败回退为每个字符 7 DIP 的线性估算。
/// </summary>
    private double MeasureTextWidth(string text)
    {
        try
        {
            var probe = new TextBlock
            {
                FontFamily = new FontFamily(_theme.FontFamily),
                FontSize = 12.0 * Math.Clamp(_theme.FontScale, 0.85, 1.2),
                FontWeight = FontWeights.Normal,
                Text = text
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }
        catch
        {
            // 测量失败时保留线性估算兜底。
            return text.Length * 7.0;
        }
    }

    private void UpdateSnapshot(BalanceSnapshot snapshot)
    {
        _snapshot = snapshot;

        // v3.1 算法：risk = threshold / balance（"阈值占余额的比例"）。
        // 例：余额=120、阈值=20 → 0.167 Safe（绿）；余额=40、阈值=20 → 0.5 Warming 边缘（黄）；
        //     余额=20、阈值=20 → 1.0 Overrun（红，满圆）。
        // MiniMax：额度按时长计费，风险用"已消耗比例"（100 − 剩余百分比），
        // 圆环弧值用剩余百分比（current_interval_remaining_percent / 100）。
        var isMiniMax = IsMiniMax;
        double riskRatio = ComputeRiskRatioForCurrent();
        var ringColor = RingColor(riskRatio);
        var ringArc = isMiniMax
            ? Math.Clamp(_minimaxRemainingPercent ?? 100, 0, 100) / 100.0
            : RingArcValue(riskRatio);

        var text = BuildCapsuleText(snapshot, _settings, _state, riskRatio);
        var toolTip = string.IsNullOrEmpty(snapshot.StatusText)
            ? text
            : $"{text}\n{snapshot.StatusText}";

        // 胶囊 signature 不再包含 isPeakHour（太阳图标已移除）。
        var signature = text + "|" + riskRatio.ToString("F3", CultureInfo.InvariantCulture) + "|" + ringColor + "|" + snapshot.StatusText;
        if (!string.Equals(signature, _lastCapsuleSignature, StringComparison.Ordinal))
        {
            // 胶囊只在内容真正变化时更新，避免无谓的宿主布局抖动。
            _lastCapsuleSignature = signature;

            // 1) 写共享字段：CreateCapsuleView 首次被宿主调用时会从这里取值。
            _capsuleText = text;
            _capsuleRingColorHex = ringColor;
            _capsuleRingArc = ringArc;

            // 2) 原地更新两个已缓存的 1.7 自定义视图（Regular / Docked）。
            //    宿主会优先使用 customView 渲染胶囊，这里保证视图跟随状态刷新。
            _regularCapsuleView?.Update(text, ringColor, ringArc);
            _dockedCapsuleView?.Update(text, ringColor, ringArc);

            // 3) 协议层通道：SetCapsulePresentation 必须调用，否则宿主判定
            //    `_pluginCapsulePresentation == null` 会清空胶囊槽、不请求 customView。
            //    PreferredWidth = 全部固定列宽(33) + textWidth + 0.1 余量。
            //    Grid 列布局 [6 pad][18 ring][5 gap][* text][4 right pad]，
            //    固定列总宽 = 6+18+5+4 = 33。差额 0.1 极致贴边。
            //    textWidth 用 MeasureTextWidth(主题字体 TextBlock.Measure + DesiredSize.Width)
            //    与 customView 渲染完全同源，避免亚像素舍入差异导致省略。
            //    Components 保留 1 项最小 Text 占位（Length > 0 让 Normalize 不返回 null，
            //    customView != null 时宿主跳过 1.6 模板不渲染它们）。
            //    ToolTip 由宿主写到外壳 Border（1.7 视图 IsHitTestVisible=false 无法自己挂 ToolTip）；
            //    PlainText 用于跨队列拖动的纯文字回退。
            var textWidth = Math.Ceiling(MeasureTextWidth(text));
            var preferredWidth = 6 + 18 + 5 + textWidth + 4 + 0.1;
            _context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = preferredWidth,
                PlainText = text,
                ToolTip = toolTip,
                Components = new[]
                {
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = text,
                        Fill = true
                    }
                }
            });
        }

        // 1.8 边缘预览视图刷新：依赖 _minimaxModelRemains / _minimaxRemainingPercent，
        // 非 MiniMax 时 _miniView 为 null 自然空操作。
        ApplyMiniViewSnapshot();

        // 面板（HTML）每次拉取后都推送：余额可能不变但用量/时间变了。
        PushView();
    }

    /// <summary>
    /// 协议 1.7 自定义胶囊视图：宿主为 Regular / Docked 各调一次并缓存；宽度变化时重新调用。
    /// 必须返回 fresh unparented FrameworkElement（宿主校验 Parent==null）。
    /// </summary>
    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        var view = new BalanceCapsuleView(context);
        // 首次返回时立即填入最新状态，避免宿主先展示空 view 再被 Update 刷新。
        view.Update(_capsuleText, _capsuleRingColorHex, _capsuleRingArc);
        if (context.Surface == PaperCapsuleSurfaceKind.Docked)
        {
            _dockedCapsuleView = view;
        }
        else
        {
            _regularCapsuleView = view;
        }
        return view;
    }

    /// <summary>
    /// <summary>
/// 协议 1.8 自定义边缘预览视图：胶囊悬停时暴露 brief 卡片。OpenCode 返回 null 让宿主走 1.6/1.7 回退。
/// </summary>
    public PaperMiniViewSize PreferredMiniViewSize => new(440, 180);

    public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
    {
        if (string.Equals(_state.Provider, PaperState.OpenCode, StringComparison.Ordinal))
        {
            return null;
        }
        var view = new BalanceMiniView(this, context);
        view.ApplyTheme(context.Theme);
        // 字段先赋值，确保 ApplyMiniViewSnapshot 内的 Update 调用不被早 return。
        _miniView = view;
        ApplyMiniViewSnapshot();
        return view;
    }

    /// <summary>
    /// 1.8 边缘预览显隐通知：本插件业务状态由胶囊/监视面板可见性统一驱动，无需响应。
    /// </summary>
    public void OnMiniViewVisibilityChanged(bool visible) { }

    /// <summary>
    /// 把当前 snapshot 推给 1.8 边缘预览视图。MiniMax 走 5h+周双模块;
    /// DeepSeek 走三列卡片(今日消费/近7日/今日消耗)。非二者时 _miniView 为 null 空操作。
    /// </summary>
    private void ApplyMiniViewSnapshot()
    {
        if (_miniView == null)
        {
            return;
        }

        if (IsMiniMax && _minimaxModelRemains != null && _minimaxModelRemains.Count > 0)
        {
            for (var i = 0; i < _minimaxModelRemains.Count; i++)
            {
                var item = _minimaxModelRemains[i];
                if (string.Equals(item.Model, "general", StringComparison.OrdinalIgnoreCase))
                {
                    var maxData = new BalanceMiniView.MiniMaxQuota(
                        Percent: Math.Clamp(item.Percent, 0, 100),
                        RemainingHours: item.Hours,
                        WeeklyPercent: Math.Clamp(item.WeeklyPercent, 0, 100),
                        WeeklyHours: item.WeeklyHours);
                    _miniView.Update(
                        new BalanceMiniView.MiniViewSnapshot(
                            Provider: PaperState.MiniMax,
                            MaxData: maxData,
                            DeepSeekData: null),
                        _snapshot.StatusText);
                    return;
                }
            }
        }

        if (string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
        {
            var todayCost = BuildCostTodayText();
            var costTodayFoot = BuildCostTodayFoot();
            var cost7d = BuildCost7dText();
            var cost7dFoot = BuildCost7dFoot();
            var todayTokens = BuildTodayTokens();
            var todayHit = BuildTodayHit();
            var cacheRate = BuildTodayCacheRate();

            var ds = new BalanceMiniView.DeepSeekMetrics(
                TodayCostText: string.IsNullOrEmpty(todayCost) ? "" : todayCost,
                CostTodayFoot: costTodayFoot,
                Cost7dText: string.IsNullOrEmpty(cost7d) ? "" : cost7d,
                Cost7dFoot: cost7dFoot,
                TodayTokensText: FormatTokens(todayTokens),
                TodayTokensWan: "≈ " + FormatWanYi(todayTokens),
                TodayHitText: "缓存命中: " + FormatThousands(todayHit) + " Tokens",
                TodayCacheRate: FormatCacheRate(cacheRate));
            _miniView.Update(
                new BalanceMiniView.MiniViewSnapshot(
                    Provider: PaperState.DeepSeek,
                    MaxData: null,
                    DeepSeekData: ds),
                _snapshot.StatusText);
        }
    }

    /// <summary>
    /// 胶囊文本：货币符号 + 余额 +（可选）百分比，v3.1 风格 "¥12.34 · 6%"。
    /// 文本由宿主 1.6 模板用宿主胶囊字体渲染；宿主按 PreferredWidth 给定内容宽度，
    /// 配合估算余量，" · " 分隔不会截断。
    /// </summary>
    private static string BuildCapsuleText(
        BalanceSnapshot snapshot,
        BalanceSettings settings,
        PaperState state,
        double riskRatio)
    {
        var sb = new StringBuilder();
        // MiniMax：胶囊显示 "xx% · xx时xx分"——百分比为 current_interval_remaining_percent，
        // 时长为 remains_time 转换的时分。圆环弧值由 UpdateSnapshot 用剩余百分比计算。
        if (string.Equals(state.Provider, PaperState.MiniMax, StringComparison.Ordinal))
        {
            if (!double.IsNaN(snapshot.Remaining) && snapshot.Remaining > 0)
            {
                var remain = (int)Math.Round(
                    Math.Clamp(1 - riskRatio, 0, 1) * 100.0, MidpointRounding.AwayFromZero);
                var hours = snapshot.Remaining;
                var h = (int)Math.Floor(hours);
                var m = (int)Math.Round((hours - h) * 60);
                if (m == 60)
                {
                    h += 1;
                    m = 0;
                }
                sb.Append(remain.ToString(CultureInfo.CurrentCulture));
                sb.Append("% · ");
                sb.Append(h.ToString(CultureInfo.CurrentCulture));
                sb.Append("时");
                sb.Append(m.ToString(CultureInfo.CurrentCulture));
                sb.Append("分");
            }
            else
            {
                sb.Append("—");
            }
            return sb.ToString();
        }
        // DeepSeek：百分数在前，货币余额在后，格式 "xx% · ¥xx.xx"（百分数由设置开关）。
        var hasPercent = settings.ShowPercentage
            && snapshot.HasRemaining
            && !double.IsNaN(snapshot.Remaining);
        if (hasPercent)
        {
            var percent = (int)Math.Round(
                Math.Clamp(riskRatio, 0, 1) * 100.0, MidpointRounding.AwayFromZero);
            sb.Append(percent.ToString(CultureInfo.CurrentCulture));
            sb.Append('%');
            sb.Append(" · ");
        }
        if (!string.IsNullOrEmpty(settings.CurrencySymbol))
        {
            sb.Append(settings.CurrencySymbol);
        }
        // 无数据时 FormatAmount(NaN) 输出 "—"。
        sb.Append(FormatAmount(snapshot.Remaining));
        return sb.ToString();
    }

    /// <summary>
    /// 高峰时段判断：UTC+8 的 9:00-12:00 / 14:00-18:00（半开区间，不含 12:00 与 18:00 整点）。
    /// 不依赖用户本地时区，始终按北京时间计算——不同地区使用同一时段标准。
    /// </summary>
    private static bool IsPeakHourUtc8()
    {
        var hour = DateTime.UtcNow.AddHours(8).Hour;
        return (hour >= 9 && hour < 12) || (hour >= 14 && hour < 18);
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
        return threshold / balance;
    }

    /// <summary>
    /// 当前供应商是否为 MiniMax（额度按时长计费）。
    /// </summary>
    private bool IsMiniMax =>
        string.Equals(_state.Provider, PaperState.MiniMax, StringComparison.Ordinal);

    /// <summary>
    /// 当前供应商的风险比例：MiniMax 用"已消耗比例"（100 − 剩余百分比），
    /// DeepSeek 用阈值/余额；统一过滤 NaN/Infinity。
    /// </summary>
    private double ComputeRiskRatioForCurrent()
    {
        if (IsMiniMax && _minimaxRemainingPercent.HasValue)
        {
            return Finite((100 - _minimaxRemainingPercent.Value) / 100.0);
        }
        return Finite(ComputeRiskRatio(_snapshot.Remaining, _settings.BalanceThreshold));
    }

    // v3.1 风险档位阈值（ClassifyRisk 与 RiskColor 共用）。
    private const double RiskWarmingRatio = 0.5;
    private const double RiskDangerRatio = 0.8;
    private const double RiskOverrunRatio = 1.0;

    private enum RiskState { Safe, Warming, Danger, Overrun }

    private static RiskState ClassifyRisk(double ratio)
    {
        if (ratio >= RiskOverrunRatio) return RiskState.Overrun;
        if (ratio >= RiskDangerRatio) return RiskState.Danger;
        if (ratio >= RiskWarmingRatio) return RiskState.Warming;
        return RiskState.Safe;
    }

    /// <summary>v3.1 颜色：Safe 绿 / Warming 黄 / Danger 橙 / Overrun 红。</summary>
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

    /// <summary>NaN/±Infinity 归一为 0，避免 JsonSerializer 序列化时抛异常。</summary>
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>Finite 的可空版本：非有限值返回 null。</summary>
    private static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

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

    /// <summary>万/亿简写（"12345" → "1.2万"），负数/NaN 返回 "—"。</summary>
    private static string FormatWanYi(double n)
    {
        if (!double.IsFinite(n) || n < 0)
        {
            return "—";
        }
        if (n >= 1e8)
        {
            return (n / 1e8).ToString("0.0", CultureInfo.CurrentCulture) + "亿";
        }
        if (n >= 1e4)
        {
            return (n / 1e4).ToString("0.0", CultureInfo.CurrentCulture) + "万";
        }
        return ((long)Math.Round(n)).ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>千分位逗号分隔（1000 → "1,000"），负数返回 "0"。</summary>
    private static string FormatThousands(double n)
    {
        if (!double.IsFinite(n) || n < 0)
        {
            return "0";
        }
        var rounded = (long)Math.Round(n);
        return rounded.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>整数 tokens 格式化：&lt;=0 显示 "—"，否则千分位。</summary>
    private static string FormatTokens(double n)
    {
        if (!double.IsFinite(n) || n <= 0)
        {
            return "—";
        }
        return FormatThousands(n);
    }

    /// <summary>缓存命中率 0..1 → "50.10%"；null/NaN 返回 null。</summary>
    private static string? FormatCacheRate(double? rate)
    {
        if (!rate.HasValue || !double.IsFinite(rate.Value))
        {
            return null;
        }
        return (rate.Value * 100).ToString("0.00", CultureInfo.CurrentCulture) + "%";
    }

    // 风险色（v3.1 语义）
    private static readonly Color RiskGreen = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color RiskYellow = Color.FromRgb(0xFF, 0xC1, 0x07);
    private static readonly Color RiskOrange = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color RiskRed = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color RiskGray = Color.FromRgb(0x9E, 0x9E, 0x9E);

    // ---------------- WebView2 监视面板 ----------------

    /// <summary>
    /// 构建正文容器：插件自建 WebView2 渲染监视面板，数据由 C# 推送给页面 JS（不发网络请求，规避 WebView2 CORS）。
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

    /// <summary>按供应商选择面板 HTML 文件。</summary>
    private static string HtmlFileNameFor(string provider) => provider switch
    {
        "minimax" => "minimax.html",
        "opencode" => "opencode.html",
        _ => "monitor.html"
    };

    /// <summary>供应商切换后重新导航 WebView2 到对应面板 HTML。</summary>
    private void ReloadPanelForProvider()
    {
        _documentReady = false;
        _pendingPayload = null;
        const string hostName = "papertodo.balance.monitor.local";
        var htmlFile = HtmlFileNameFor(_state.Provider);
        try
        {
            _webView.CoreWebView2?.Navigate($"https://{hostName}/web/{htmlFile}");
        }
        catch
        {
        }
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
                System.IO.Path.GetDirectoryName(typeof(ApiBalanceMonitorPlugin).Assembly.Location)
                ?? AppContext.BaseDirectory;
            var htmlFile = HtmlFileNameFor(_state.Provider);
            if (!File.Exists(System.IO.Path.Combine(pluginDirectory, "web", htmlFile)))
            {
                throw new InvalidOperationException($"缺少 web/{htmlFile}。");
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
            core.Navigate($"https://{hostName}/web/{htmlFile}");
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
            System.IO.Path.GetDirectoryName(typeof(ApiBalanceMonitorPlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var userDataFolder = System.IO.Path.Combine(pluginDirectory, ".runtime", "webview2");
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
                // 重载失败静默，页面将在下次导航尝试恢复。
            }
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (json.IndexOf("\"ready\"", StringComparison.Ordinal) >= 0 && _webViewReady)
            {
                PostPayload(BuildViewPayload());
                return;
            }
            if (json.IndexOf("\"switchProvider\"", StringComparison.Ordinal) >= 0)
            {
                var msg = JsonDocument.Parse(json);
                if (msg.RootElement.TryGetProperty("provider", out var p) &&
                    p.ValueKind == JsonValueKind.String)
                {
                    var newProvider = p.GetString() ?? "";
                    if (IsValidProvider(newProvider))
                    {
                        SetPaperProvider(newProvider);
                    }
                }
            }
        }
        catch
        {
            // 页面消息解析异常不影响面板主体。
        }
    }

    private static bool IsValidProvider(string p) =>
        p == PaperState.DeepSeek || p == PaperState.MiniMax || p == PaperState.OpenCode;

    /// <summary>切换当前 paper 的供应商：写 state + 重载面板 + 立即拉取。</summary>
    private void SetPaperProvider(string newProvider)
    {
        if (string.Equals(_state.Provider, newProvider, StringComparison.Ordinal))
        {
            return;
        }
        _state = new PaperState(newProvider);
        _settings = ReadSettings(_context.SettingsJson, _state.Provider);
        try
        {
            _context.SaveStateJson(SerializeState(_state));
        }
        catch
        {
            // 状态写失败不致命，本会话内仍按新 provider 工作。
        }
        ReloadPanelForProvider();
        // 重置 snapshot 避免显示旧 provider 的残留数据。
        _minimaxModelRemains = null;
        _minimaxRemainingPercent = null;
        _ = PollAsync();
    }

    /// <summary>把最新数据推给 HTML 面板；未就绪时缓存，就绪后自动补发。</summary>
    private void PushView()
    {
        if (_disposed)
        {
            return;
        }
        var payload = BuildViewPayload();
        if (!_webViewReady || !_documentReady)
        {
            // WebView2 未就绪时缓存，页面加载完成后由 OnWebViewNavigationCompleted 补发。
            _pendingPayload = payload;
            return;
        }
        PostPayload(payload);
    }

    private void PostPayload(string payload)
    {
        // 主通道 postMessage + 备用通道 ExecuteScriptAsync；WebView2 可能已销毁，吞异常。
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(payload);
        }
        catch
        {
        }
        try
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"window.__renderBalance && window.__renderBalance({payload});");
        }
        catch
        {
        }
    }

    /// <summary>组装推给 HTML 的 JSON：{ theme, data }。</summary>
    private string BuildViewPayload()
    {
        var status = _snapshot.StatusText ?? "";
        var hasData = _snapshot.HasRemaining && !double.IsNaN(_snapshot.Remaining);
        var isMiniMax = IsMiniMax;
        var ratio = ComputeRiskRatioForCurrent();
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
            ["paper"] = NormalizeHex(_theme.PaperColor, "#FFF8E6")
        };

        var data = new Dictionary<string, object?>
        {
            ["provider"] = _state.Provider,
            ["status"] = status,
            ["statusKind"] = statusKind,
            ["hasBalance"] = hasData,
            ["balance"] = hasData ? FormatAmount(_snapshot.Remaining) : "—",
            ["currency"] = isMiniMax ? "小时" : (MapCurrencySymbolToCode(_settings.CurrencySymbol) ?? _settings.CurrencySymbol),
            ["currencySymbol"] = isMiniMax ? "" : _settings.CurrencySymbol,
            ["ratio"] = ratio,
            ["riskColor"] = riskColor,
            ["updateTime"] = hasData
                ? "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "",
            ["costToday"] = BuildCostTodayText(),
            ["costTodayFoot"] = BuildCostTodayFoot(),
            ["costTodayModels"] = BuildCostTodayByModels(),
            ["cost7d"] = BuildCost7dText(),
            ["cost7dFoot"] = BuildCost7dFoot(),
            ["todayTokens"] = Finite(BuildTodayTokens()),
            ["todayHit"] = Finite(BuildTodayHit()),
            ["cacheRate"] = FiniteOrNull(BuildTodayCacheRate()),
            ["modelRemains"] = BuildMiniMaxModelRemains(),
            ["usage"] = BuildUsageArray()
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["theme"] = theme,
            ["data"] = data
        });
    }

    /// <summary>今日消费文本；无数据返回空。</summary>
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

    /// <summary>今日 vs 昨日消费变化文案（↑/↓/→）；无昨日数据时返回空。</summary>
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

    /// <summary>今日各模型消费明细（按金额降序）。</summary>
    private object[] BuildCostTodayByModels()
    {
        if (_costTodayByModel == null || _costTodayByModel.Count == 0)
        {
            return Array.Empty<object>();
        }
        var sym = _settings.CurrencySymbol;
        return _costTodayByModel
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new Dictionary<string, object?>
            {
                ["model"] = kv.Key,
                ["costText"] = sym + kv.Value.ToString("0.00", CultureInfo.CurrentCulture)
            })
            .ToArray();
    }

    /// <summary>MiniMax 各模型剩余额度（供 minimax.html 渲染）。</summary>
    private object[] BuildMiniMaxModelRemains()
    {
        if (_minimaxModelRemains == null || _minimaxModelRemains.Count == 0)
        {
            return Array.Empty<object>();
        }
        return _minimaxModelRemains
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
    private UsageDay? FindTodayUsage()
    {
        if (_usageDays == null || _usageDays.Length == 0)
        {
            return null;
        }
        var key = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Array.Find(_usageDays, u => u.Date == key);
    }

    /// <summary>今日总 Token 用量；无数据返回 0。</summary>
    private double BuildTodayTokens() => FindTodayUsage()?.Tokens ?? 0;

    /// <summary>今日缓存命中 Token 数；无数据返回 0。</summary>
    private double BuildTodayHit() => FindTodayUsage()?.CacheHit ?? 0;

    /// <summary>今日缓存命中率（0~1）；当天无缓存数据返回 null。</summary>
    private double? BuildTodayCacheRate()
    {
        var day = FindTodayUsage();
        if (day == null || (day.CacheHit + day.CacheMiss) <= 0)
        {
            return null;
        }
        return day.CacheHit / (day.CacheHit + day.CacheMiss);
    }

    /// <summary>近 7 天（含今天）消费总额；无数据返回 0。</summary>
    private double SumLast7DaysCost()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return 0;
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
        return total;
    }

    /// <summary>近 7 天消费总额文本；无数据返回空。</summary>
    private string BuildCost7dText()
    {
        var total = SumLast7DaysCost();
        if (total <= 0)
        {
            return "";
        }
        return _settings.CurrencySymbol + total.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>近 7 天日均消费文案（"日均 ¥X.XX"）；无数据返回空。</summary>
    private string BuildCost7dFoot()
    {
        if (_costDays == null || _costDays.Length == 0)
        {
            return "";
        }
        var avg = SumLast7DaysCost() / 7.0;
        return "日均 " + _settings.CurrencySymbol +
            avg.ToString("0.00", CultureInfo.CurrentCulture);
    }

    /// <summary>全量每日 Token 用量数组（含 date 字段），供 HTML 按所选时段筛选。</summary>
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
                ["tokens"] = Finite(day.Tokens)
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

    /// <summary>v3.1 风险环颜色（含过渡渐变）：绿 → 黄 → 橙 → 红；未配置阈值时灰。</summary>
    private static Color RiskColor(double ratio)
    {
        if (ratio <= 0)
        {
            return RiskGray;
        }
        if (ratio >= RiskOverrunRatio)
        {
            return RiskRed;
        }
        if (ratio >= RiskDangerRatio)
        {
            return Lerp(RiskYellow, RiskOrange, (ratio - RiskDangerRatio) / 0.2);
        }
        if (ratio >= RiskWarmingRatio)
        {
            return Lerp(RiskGreen, RiskYellow, (ratio - RiskWarmingRatio) / 0.3);
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

    // ----------------------------------------------------------------------
    // 协议 1.7 自定义胶囊视图：完全由插件渲染，宿主只在 Theme / Width / Surface
    // 这三个维度上提供上下文。视觉布局 [6px pad][18px ring][5px gap][* text][4px gap][auto sun]
    // 与宿主 1.6 模板的 (Padding 6/0/6/0) + (Component Gap 5) + ProgressRing(18) 一致；
    // 末尾的 sun 列承载 DeepSeek 高峰时段的太阳图标（用 SVG 几何绘制，非高峰时段隐藏）。
    // ----------------------------------------------------------------------

    private sealed class BalanceCapsuleView : Grid
    {
        private readonly TextBlock _label;
        private readonly BalanceProgressRing _ring;

        // FontFamily 缓存：theme.FontFamily 是 Source 字符串，按字符串相等判断避免重复构造。
        // 避免每次 ApplyTheme 都触发 WPF 字体回退链解析（首次解析可达 100ms 级）。
        private FontFamily? _cachedFontFamily;
        private string? _cachedFontFamilySource;

        public BalanceCapsuleView(PaperCapsuleViewContext context)
        {
            Background = Brushes.Transparent;
            ClipToBounds = true;
            // 宿主还会强制重置 IsHitTestVisible / Focusable / Stretch 对齐，本地保险。
            IsHitTestVisible = false;
            Focusable = false;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            // 列布局：[6 pad][18 ring][5 gap][* text][4 right pad]
            // 移除 sun 列后右 padding 缩为 4 DIP,差额 6 的原口径不再适用。
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

            _ring = new BalanceProgressRing
            {
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(_ring, 1);
            Children.Add(_ring);

            _label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left
            };
            Grid.SetColumn(_label, 3);
            Children.Add(_label);

            ApplyTheme(context.Theme);
        }

        /// <summary>
        /// 刷新胶囊文本与圆环颜色 / 弧值。
        /// </summary>
        public void Update(
            string text,
            string ringColorHex,
            double ringArc)
        {
            _label.Text = text;
            _ring.Value = Math.Clamp(ringArc, 0, 1);
            _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
            _ring.InvalidateVisual();
        }

        /// <summary>
        /// 主题切换：重设文本字体 / 字号 / 颜色，圆环底色（近似 Theme.Tint(38)）。
        /// 字号取 12 × FontScale，与宿主默认 CapsuleTextSize=Medium + AppTypography.Scale 一致。
        /// </summary>
        public void ApplyTheme(PaperBodyTheme theme)
        {
            var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
            // FontFamily 缓存：theme.FontFamily 是 Source 字符串，按字符串相等判断避免重复构造。
            if (_cachedFontFamilySource != theme.FontFamily || _cachedFontFamily == null)
            {
                _cachedFontFamily = new FontFamily(theme.FontFamily);
                _cachedFontFamilySource = theme.FontFamily;
            }
            _label.FontFamily = _cachedFontFamily;
            _label.FontSize = 12.0 * scale;
            _label.FontWeight = FontWeights.Normal;
            // BrightWeakTextBrush 在浅色下等于 WeakTextBrush，深色下浅化 22%。
            // 这里取 WeakTextColor 作为单一字段近似（浅色完全一致，深色略偏暗，但 1.6 模板
            // 对未设 Color 的 Text 组件也走这条 Tone 兜底，视觉同源）。
            // ToBrush 已返回冻结 Brush，可直接复用，无需每次重建。
            _label.Foreground = ToBrush(theme.WeakTextColor, "#707070");

            // TrackBrush 近似 Theme.Tint(38)：在当前 AccentColor 上叠加 alpha=38。
            // PaperBodyTheme 不暴露 Theme.Tint，使用最近的色板字段 AccentColor 做近似。
            var accent = ToBrush(theme.AccentColor, "#B07A31");
            var track = new SolidColorBrush(
                Color.FromArgb(38, accent.Color.R, accent.Color.G, accent.Color.B));
            track.Freeze();
            _ring.TrackBrush = track;
            _ring.InvalidateVisual();
        }

        private static SolidColorBrush ToBrush(string value, string fallback)
        {
            SolidColorBrush brush;
            try
            {
                brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(value)!);
            }
            catch
            {
                try
                {
                    brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(fallback)!);
                }
                catch
                {
                    brush = new SolidColorBrush(Colors.Gray);
                }
            }
            // 冻结 brush：让 WPF 渲染系统走快路径（避免每帧 IsFrozen 检查），
            // 并允许跨线程共享（GPU worker 线程可直接读取颜色与变换）。
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// 圆环进度控件。完全 1:1 复刻宿主 CapsuleProgressRing（PaperWindow.PluginCapsule.cs）：
    /// Pen 粗细 2，半径 = max(1, size/2 - 1.5)，起点 -90° 顺时针，value≥0.999 画整圆。
    ///
    /// 性能优化（避免 5 秒延迟）：
    /// - Pen 缓存：仅在 TrackBrush / ForegroundBrush 引用变化时重建并 Freeze。
    /// - StreamGeometry 缓存：仅在 value 变化时重建弧形几何，Freeze 后可跨帧复用。
    /// </summary>
    private sealed class BalanceProgressRing : FrameworkElement
    {
        public double Value { get; set; }
        public Brush ForegroundBrush { get; set; } = Brushes.Gray;
        public Brush TrackBrush { get; set; } = Brushes.LightGray;

        // Pen 缓存：仅在 brush 引用变化时重建（重建后 Freeze 启用渲染快路径）
        private Pen? _cachedTrackPen;
        private Pen? _cachedValuePen;
        private Brush? _cachedTrackBrushRef;
        private Brush? _cachedFgBrushRef;

        // Geometry 缓存：仅在 value 变化时重建（Freeze 后线程安全）
        private double _cachedGeometryValue = double.NaN;
        private StreamGeometry? _cachedGeometry;

        private Pen GetTrackPen()
        {
            if (_cachedTrackPen == null || !ReferenceEquals(_cachedTrackBrushRef, TrackBrush))
            {
                _cachedTrackPen = new Pen(TrackBrush, 2);
                _cachedTrackPen.Freeze();
                _cachedTrackBrushRef = TrackBrush;
            }
            return _cachedTrackPen;
        }

        private Pen GetValuePen()
        {
            if (_cachedValuePen == null || !ReferenceEquals(_cachedFgBrushRef, ForegroundBrush))
            {
                _cachedValuePen = new Pen(ForegroundBrush, 2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                _cachedValuePen.Freeze();
                _cachedFgBrushRef = ForegroundBrush;
            }
            return _cachedValuePen;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 2)
            {
                return;
            }

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var radius = Math.Max(1, size / 2 - 1.5);
            dc.DrawEllipse(null, GetTrackPen(), center, radius, radius);

            var value = Math.Clamp(Value, 0, 1);
            if (value <= 0)
            {
                return;
            }
            if (value >= 0.999)
            {
                dc.DrawEllipse(null, GetValuePen(), center, radius, radius);
                return;
            }

            // 弧形 Geometry 缓存：value 不变时复用上一次构建的 StreamGeometry，
            // 避免每次 OnRender 都走 Open/ArcTo/Freeze 路径。
            if (_cachedGeometry == null || _cachedGeometryValue != value)
            {
                var geo = new StreamGeometry();
                var startAngle = -90.0;
                var endAngle = startAngle + value * 360.0;
                using (var ctx = geo.Open())
                {
                    Point PointAt(double angle)
                    {
                        var radians = angle * Math.PI / 180.0;
                        return new Point(
                            center.X + Math.Cos(radians) * radius,
                            center.Y + Math.Sin(radians) * radius);
                    }
                    ctx.BeginFigure(PointAt(startAngle), false, false);
                    ctx.ArcTo(
                        PointAt(endAngle),
                        new Size(radius, radius),
                        0,
                        value > 0.5,
                        SweepDirection.Clockwise,
                        true,
                        false);
                }
                geo.Freeze();
                _cachedGeometry = geo;
                _cachedGeometryValue = value;
            }

            dc.DrawGeometry(null, GetValuePen(), _cachedGeometry);
        }
    }

    /// <summary>
    /// 协议 1.8 自定义边缘预览视图：MiniMax 场景下显示 5 小时 + 周额度两个模块。
    /// 7 行 Grid：0 5h 标题 / 1 5h 进度条 / 2 5h 倒计时 / 3 12 DIP 间距 /
    /// 4 周标题 / 5 周进度条 / 6 周倒计时。Margin 与 SampleClock / FocusTimer 一致。
    /// WebView2 / HwndHost 在 MiniView 里被协议层禁用，仅纯 WPF 控件。
    /// </summary>
    private sealed class BalanceMiniView : Border
    {
        private readonly BalanceSession _owner;
        private readonly Grid _root;
        private PaperBodyTheme _theme;

        // FontFamily 缓存：theme.FontFamily 是 Source 字符串，按字符串相等判断避免
        // 重复构造（首次解析可达 100ms 级）。
        private FontFamily? _cachedFontFamily;
        private string? _cachedFontFamilySource;

        // 颜色缓存：ApplyTheme 重建后冻结，可跨线程共享。
        private Brush _textBrush = Brushes.Black;
        private Brush _weakBrush = Brushes.Gray;
        private Brush _accentBrush = Brushes.Blue;
        private Brush _barTrackBrush = Brushes.LightGray;

        // 进度条 fill 固定为中性灰 #808080,冻结后跨线程共享,避免每次 Update 都重建。
        // 用户已确认不再按风险档变色,所以移除 RiskColor 依赖。
        private readonly Brush _grayBrush;
        // 记录最近 ratio:SizeChanged 事件按此值重算 fill.Width,与进度条 fill 颜色无关。
        private double _lastHourlyRatio = -1;
        private double _lastWeeklyRatio = -1;

        // 控件引用
        private readonly TextBlock _hourlyLabel;
        private readonly TextBlock _hourlyPercent;
        private readonly Grid _hourlyBarGrid;
        private readonly Rectangle _hourlyBarTrack;
        private readonly Rectangle _hourlyFill;
        private readonly TextBlock _hourlyReset;
        private readonly Border _divider;
        private readonly TextBlock _weeklyLabel;
        private readonly TextBlock _weeklyPercent;
        private readonly Grid _weeklyBarGrid;
        private readonly Rectangle _weeklyBarTrack;
        private readonly Rectangle _weeklyFill;
        private readonly TextBlock _weeklyReset;
        private readonly TextBlock _footer;

        // MiniMax 双模块模式下的两个 StackPanel（缓存以便按 provider 切可见性）。
        private readonly StackPanel _hourlyStack;
        private readonly StackPanel _weeklyStack;
        // MiniMax 双模块容器：3 行 Grid(5h / 1px 分割线 / 周),与 _dsRootGrid 平级放在 _root 内。
        private readonly Grid _maxRootGrid;

        // DeepSeek 三列卡片子树根：横跨整个 3-row _root;MiniMax 模式时 Collapsed,
        // DeepSeek 模式时 Visible 并把原 hourlyStack / divider / weeklyStack Collapsed。
        private readonly Grid _dsRootGrid;

        // DeepSeek 列 1:今日消费金额
        private readonly Grid _dsCol1;
        private readonly Border _dsCol1Divider;
        private readonly TextBlock _dsCol1Label;
        private readonly TextBlock _dsCol1Value;
        private readonly TextBlock _dsCol1Foot;

        // DeepSeek 列 2:近 7 日
        private readonly Grid _dsCol2;
        private readonly Border _dsCol2Divider;
        private readonly TextBlock _dsCol2Label;
        private readonly TextBlock _dsCol2Value;
        private readonly TextBlock _dsCol2Foot;

        // DeepSeek 列 3:今日消耗
        private readonly Grid _dsCol3;
        private readonly TextBlock _dsCol3Label;
        private readonly TextBlock _dsCol3ValueNumber;
        private readonly TextBlock _dsCol3ValueSuffix;
        private readonly TextBlock _dsCol3Foot1;   // ≈ 5.0万
        private readonly TextBlock _dsCol3Foot2;   // 缓存命中: ...
        private readonly TextBlock _dsCol3Foot3;   // 缓存命中率 ...

        public BalanceMiniView(BalanceSession owner, PaperMiniViewContext context)
        {
            _owner = owner;
            _theme = context.Theme;

            // 自身作为圆角容器：暗色下 12% 黑、浅色下 6% 黑,与胶囊外壳视觉分离。
            CornerRadius = new CornerRadius(10);
            Margin = new Thickness(4);
            // Padding 加大到 14/12/14/12 让 5h/周模块与圆角边缘留出呼吸空间,避免贴边。
            Padding = new Thickness(14, 12, 14, 12);
            Background = BuildContainerBackground(_theme.IsDark);
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            // 进度条 fill 固定灰:中性灰 #808080,冻结后跨线程共享。
            var gray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            gray.Freeze();
            _grayBrush = gray;

            // 内部 1 行 Grid:同时容纳 MiniMax 双模块(maxRootGrid)与 DeepSeek 三列(dsRootGrid)。
// 两个子树互斥:provider 是 MiniMax 时 maxRootGrid 可见 dsRootGrid 隐藏;DeepSeek 反之。
// 不再用跨行 SetRowSpan(3),避免 Collapsed 状态下 Grid layout 引擎在 hourlyStack/divider/weeklyStack 同 Grid 下产生
// row 分配冲突,导致 MiniMax 模式视觉错乱。
            _root = new Grid();
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // MiniMax 双模块内部 3 行 Grid:5h / 1px 分割线 / 周。
            _maxRootGrid = new Grid();
            _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Pixel) });
            _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _maxRootGrid.IsHitTestVisible = false;

            // === 5 小时模块(占据上半部 50%) ===
            _hourlyLabel = new TextBlock
            {
                Text = "每五小时额度",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _hourlyPercent = new TextBlock
            {
                Text = "—",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var hourlyHead = new Grid();
            hourlyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hourlyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_hourlyLabel, 0);
            Grid.SetColumn(_hourlyPercent, 1);
            hourlyHead.Children.Add(_hourlyLabel);
            hourlyHead.Children.Add(_hourlyPercent);

            (_hourlyBarGrid, _hourlyBarTrack, _hourlyFill) = BuildStyledProgressBar();
            _hourlyBarGrid.SizeChanged += OnHourlyBarSizeChanged;

            _hourlyReset = new TextBlock
            {
                Text = "尚未拉取",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var hourlyStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false
            };
            hourlyStack.Children.Add(hourlyHead);
            hourlyStack.Children.Add(_hourlyBarGrid);
            hourlyStack.Children.Add(_hourlyReset);
            Grid.SetRow(hourlyStack, 0);
            _hourlyStack = hourlyStack;
            _maxRootGrid.Children.Add(hourlyStack);

            // 5h / 周模块之间的 1px 分割线,弱色填充。
            _divider = new Border
            {
                Height = 1
            };
            Grid.SetRow(_divider, 1);
            _maxRootGrid.Children.Add(_divider);

            // === 周额度模块(占据下半部 50%) ===
            _weeklyLabel = new TextBlock
            {
                Text = "周额度",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _weeklyPercent = new TextBlock
            {
                Text = "—",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var weeklyHead = new Grid();
            weeklyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            weeklyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_weeklyLabel, 0);
            Grid.SetColumn(_weeklyPercent, 1);
            weeklyHead.Children.Add(_weeklyLabel);
            weeklyHead.Children.Add(_weeklyPercent);

            (_weeklyBarGrid, _weeklyBarTrack, _weeklyFill) = BuildStyledProgressBar();
            _weeklyBarGrid.SizeChanged += OnWeeklyBarSizeChanged;

            _weeklyReset = new TextBlock
            {
                Text = "尚未拉取",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // 底部 footer：更新于 HH:mm:ss,弱文字小字号,右对齐嵌在周模块底部。
            _footer = new TextBlock
            {
                Text = "尚未拉取",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            };

            // weeklyStack 不再含 _footer(避免自然高度超 row 高度导致上下溢出,
            // divider 1px 被内容覆盖)。_footer 改放到 _maxRootGrid Row 2 内右下角独立显示。
            var weeklyStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false
            };
            weeklyStack.Children.Add(weeklyHead);
            weeklyStack.Children.Add(_weeklyBarGrid);
            weeklyStack.Children.Add(_weeklyReset);
            Grid.SetRow(weeklyStack, 2);
            _weeklyStack = weeklyStack;
            _maxRootGrid.Children.Add(weeklyStack);

            // _footer 在 _maxRootGrid Row 2 内右下角:StackPanel 居左,_footer 靠右下,
            // 不撑高 weeklyStack 自然高度。
            Grid.SetRow(_footer, 2);
            Grid.SetColumn(_footer, 0);
            _footer.HorizontalAlignment = HorizontalAlignment.Right;
            _footer.VerticalAlignment = VerticalAlignment.Bottom;
            _footer.Margin = new Thickness(0, 0, 0, 2);
            _maxRootGrid.Children.Add(_footer);

            // === DeepSeek 三列卡片子树(默认 Collapsed,MiniMax 时不显示) ===
            // 整体横跨 _root 的 3 行,垂直居中。
            _dsRootGrid = new Grid();
            _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _dsRootGrid.VerticalAlignment = VerticalAlignment.Center;
            _dsRootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            _dsRootGrid.IsHitTestVisible = false;
            _dsRootGrid.Visibility = Visibility.Collapsed;

            BuildDeepSeekColumn(
                out _dsCol1, out _dsCol1Divider, out _dsCol1Label, out _dsCol1Value, out _dsCol1Foot,
                primaryInitial: "—", subText: "今日消费金额", footInitial: "",
                showDivider: false);
            BuildDeepSeekColumn(
                out _dsCol2, out _dsCol2Divider, out _dsCol2Label, out _dsCol2Value, out _dsCol2Foot,
                primaryInitial: "—", subText: "近 7 日", footInitial: "—",
                showDivider: true);
            BuildDeepSeekColumnWithTokens(
                out _dsCol3, out _dsCol3Label, out _dsCol3ValueNumber,
                out _dsCol3ValueSuffix, out _dsCol3Foot1, out _dsCol3Foot2, out _dsCol3Foot3);

            Grid.SetColumn(_dsCol1, 0); _dsRootGrid.Children.Add(_dsCol1);
            Grid.SetColumn(_dsCol2, 1); _dsRootGrid.Children.Add(_dsCol2);
            Grid.SetColumn(_dsCol3, 2); _dsRootGrid.Children.Add(_dsCol3);

            // _dsRootGrid 是 _root 的直接子节点(与 _maxRootGrid 平级),不需要 SetRowSpan(3),
            // 因为 _root 现在是 1 行 Grid,_maxRootGrid / _dsRootGrid 互斥显示。
            Grid.SetRow(_dsRootGrid, 0);
            _root.Children.Add(_dsRootGrid);

            // 把 _maxRootGrid 加入 _root(MiniMax 模式默认显示,_dsRootGrid 默认 Collapsed)。
            Grid.SetRow(_maxRootGrid, 0);
            _root.Children.Add(_maxRootGrid);

            Child = _root;
        }

        /// <summary>
        /// 构建 DeepSeek 单列：[1px 左竖线 | StackPanel]，showDivider 控制左竖线可见性。
        /// </summary>
        private void BuildDeepSeekColumn(
            out Grid column,
            out Border divider,
            out TextBlock label,
            out TextBlock value,
            out TextBlock foot,
            string primaryInitial,
            string subText,
            string footInitial,
            bool showDivider)
        {
            column = new Grid();
            column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Pixel) });
            column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            divider = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4),
                Visibility = showDivider ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetColumn(divider, 0);
            column.Children.Add(divider);

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(14, 0, 14, 0)
            };

            label = new TextBlock
            {
                Text = subText,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            value = new TextBlock
            {
                Text = primaryInitial,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            foot = new TextBlock
            {
                Text = footInitial,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            stack.Children.Add(label);
            stack.Children.Add(value);
            stack.Children.Add(foot);

            Grid.SetColumn(stack, 1);
            column.Children.Add(stack);
        }

        /// <summary>
        /// 构建 DeepSeek 第三列：StackPanel 内容依次为 Label / Value(Number + Suffix) / Foot1 / Foot2 / Foot3。
        /// </summary>
        private void BuildDeepSeekColumnWithTokens(
            out Grid column,
            out TextBlock label,
            out TextBlock valueNumber,
            out TextBlock valueSuffix,
            out TextBlock foot1,
            out TextBlock foot2,
            out TextBlock foot3)
        {
            column = new Grid();
            column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(14, 0, 14, 0)
            };

            label = new TextBlock
            {
                Text = "今日消耗",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            valueNumber = new TextBlock
            {
                Text = "—",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            valueSuffix = new TextBlock
            {
                Text = " Tokens",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var valueRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueRow.Children.Add(valueNumber);
            valueRow.Children.Add(valueSuffix);

            foot1 = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            foot2 = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            foot3 = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };

            stack.Children.Add(label);
            stack.Children.Add(valueRow);
            stack.Children.Add(foot1);
            stack.Children.Add(foot2);
            stack.Children.Add(foot3);

            Grid.SetColumn(stack, 0);
            column.Children.Add(stack);
        }

        /// <summary>
        /// 容器背景：暗色 12% 黑 / 浅色 6% 黑,冻结后跨线程共享。
        /// </summary>
        private static SolidColorBrush BuildContainerBackground(bool isDark)
        {
            var brush = isDark
                ? new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 构建一行水平布局：[左弱文字标签 | 弹缩 | 右侧主文百分比]。
        /// </summary>
        private static Grid BuildHeadRow(out TextBlock label, out TextBlock percent, string labelText)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            label = new TextBlock
            {
                Text = labelText,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            percent = new TextBlock
            {
                Text = "—",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(percent, 1);
            grid.Children.Add(label);
            grid.Children.Add(percent);
            return grid;
        }

        /// <summary>圆角进度条：track 铺底色，fill 按 ratio 收窄；高 8 DIP、半径 4。</summary>
        private static (Grid grid, Rectangle track, Rectangle fill) BuildStyledProgressBar()
        {
            var grid = new Grid
            {
                Height = 8,
                Margin = new Thickness(0, 6, 24, 0)
            };
            var track = new Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var fill = new Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            grid.Children.Add(track);
            grid.Children.Add(fill);
            return (grid, track, fill);
        }

        /// <summary>
        /// <summary>进度条容器尺寸变化时按当前 ratio 重算 fill.Width。</summary>
        private void OnHourlyBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _hourlyFill.Width = Math.Max(0, _hourlyBarGrid.ActualWidth * _lastHourlyRatio);
        }

        private void OnWeeklyBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _weeklyFill.Width = Math.Max(0, _weeklyBarGrid.ActualWidth * _lastWeeklyRatio);
        }

        /// <summary>主题切换：重建 Brush 缓存、字号、字体、容器与进度条配色。</summary>
        public void ApplyTheme(PaperBodyTheme theme)
        {
            _theme = theme;
            Background = BuildContainerBackground(theme.IsDark);
            _textBrush = ToBrush(theme.TextColor, "#202020");
            _weakBrush = ToBrush(theme.WeakTextColor, "#707070");
            _accentBrush = ToBrush(theme.AccentColor, "#B07A31");
            _barTrackBrush = ToBrush(theme.IsDark ? "#28FFFFFF" : "#22000000", "#22000000");

            // 字体源：插件设置 miniViewFontFamily 非空时覆盖主题字体,留空跟随主题。
            var fontSource = !string.IsNullOrEmpty(_owner.MiniViewFontFamily)
                ? _owner.MiniViewFontFamily
                : theme.FontFamily;
            var font = ResolveFontFamily(fontSource);
            var scale = Math.Clamp(theme.FontScale, 0.85, 1.3);

            // 头部标签：弱文字、字号 15 × scale（中文字号再放大），加粗让"每五小时额度" / "周额度" 更突出
            _hourlyLabel.FontFamily = font;
            _hourlyLabel.FontSize = 15 * scale;
            _hourlyLabel.FontWeight = FontWeights.Bold;
            _hourlyLabel.Foreground = _weakBrush;
            _weeklyLabel.FontFamily = font;
            _weeklyLabel.FontSize = 15 * scale;
            _weeklyLabel.FontWeight = FontWeights.Bold;
            _weeklyLabel.Foreground = _weakBrush;

            // 百分比：主文字、字号 24 × scale；数字部分用斜体
            _hourlyPercent.FontFamily = font;
            _hourlyPercent.FontSize = 24 * scale;
            _hourlyPercent.FontStyle = FontStyles.Italic;
            _hourlyPercent.FontWeight = FontWeights.SemiBold;
            _hourlyPercent.Foreground = _textBrush;
            _weeklyPercent.FontFamily = font;
            _weeklyPercent.FontSize = 24 * scale;
            _weeklyPercent.FontStyle = FontStyles.Italic;
            _weeklyPercent.FontWeight = FontWeights.SemiBold;
            _weeklyPercent.Foreground = _textBrush;

            // 进度条 track 底色随主题;fill 风险色在 Update 里重写。
            _hourlyBarTrack.Fill = _barTrackBrush;
            _weeklyBarTrack.Fill = _barTrackBrush;

            // 5h / 周分割线颜色:复用进度条底色,弱视觉分组。
            _divider.Background = _barTrackBrush;

            // 倒计时：弱文字、字号 14 × scale（数字部分用斜体）；顶部 6 DIP margin 让它与进度条拉开间距
            _hourlyReset.FontFamily = font;
            _hourlyReset.FontSize = 14 * scale;
            _hourlyReset.FontStyle = FontStyles.Italic;
            _hourlyReset.Margin = new Thickness(0, 6, 0, 0);
            _hourlyReset.Foreground = _weakBrush;
            _weeklyReset.FontFamily = font;
            _weeklyReset.FontSize = 14 * scale;
            _weeklyReset.FontStyle = FontStyles.Italic;
            _weeklyReset.Margin = new Thickness(0, 6, 0, 0);
            _weeklyReset.Foreground = _weakBrush;

            // 底部 footer:弱文字、字号 11.5 × scale（时间戳装饰,但仍可读）；数字斜体
            _footer.FontFamily = font;
            _footer.FontSize = 11.5 * scale;
            _footer.FontStyle = FontStyles.Italic;
            _footer.Foreground = _weakBrush;

            // === DeepSeek 三列卡片字号设置 ===
            // Label(列标题):弱文字 11×scale Normal,让"今日消费金额"等副标轻盈。
            var dsLabelSize = 11 * scale;
            _dsCol1Label.FontFamily = font; _dsCol1Label.FontSize = dsLabelSize;
            _dsCol1Label.FontWeight = FontWeights.Normal; _dsCol1Label.Foreground = _weakBrush;
            _dsCol2Label.FontFamily = font; _dsCol2Label.FontSize = dsLabelSize;
            _dsCol2Label.FontWeight = FontWeights.Normal; _dsCol2Label.Foreground = _weakBrush;
            _dsCol3Label.FontFamily = font; _dsCol3Label.FontSize = dsLabelSize;
            _dsCol3Label.FontWeight = FontWeights.Normal; _dsCol3Label.Foreground = _weakBrush;

            // Value(主值):主文字 22×scale SemiBold,数字斜体加半粗
            var dsValueSize = 22 * scale;
            _dsCol1Value.FontFamily = font; _dsCol1Value.FontSize = dsValueSize;
            _dsCol1Value.FontWeight = FontWeights.SemiBold; _dsCol1Value.Foreground = _textBrush;
            _dsCol2Value.FontFamily = font; _dsCol2Value.FontSize = dsValueSize;
            _dsCol2Value.FontWeight = FontWeights.SemiBold; _dsCol2Value.Foreground = _textBrush;
            _dsCol3ValueNumber.FontFamily = font; _dsCol3ValueNumber.FontSize = dsValueSize;
            _dsCol3ValueNumber.FontWeight = FontWeights.SemiBold; _dsCol3ValueNumber.Foreground = _textBrush;
            _dsCol3ValueSuffix.FontFamily = font; _dsCol3ValueSuffix.FontSize = 11 * scale;
            _dsCol3ValueSuffix.FontWeight = FontWeights.Normal; _dsCol3ValueSuffix.Foreground = _weakBrush;

            // Foot(弱文字):极弱文字 10.5×scale
            var dsFootSize = 10.5 * scale;
            _dsCol1Foot.FontFamily = font; _dsCol1Foot.FontSize = dsFootSize;
            _dsCol1Foot.Foreground = _weakBrush;
            _dsCol2Foot.FontFamily = font; _dsCol2Foot.FontSize = dsFootSize;
            _dsCol2Foot.Foreground = _weakBrush;
            _dsCol3Foot1.FontFamily = font; _dsCol3Foot1.FontSize = dsFootSize;
            _dsCol3Foot1.Foreground = _weakBrush;
            _dsCol3Foot2.FontFamily = font; _dsCol3Foot2.FontSize = dsFootSize;
            _dsCol3Foot2.Foreground = _weakBrush;
            _dsCol3Foot3.FontFamily = font; _dsCol3Foot3.FontSize = dsFootSize;
            _dsCol3Foot3.Foreground = _weakBrush;

            // 列分隔线颜色:复用 _barTrackBrush
            _dsCol1Divider.Background = _barTrackBrush;
            _dsCol2Divider.Background = _barTrackBrush;
        }

        /// <summary>
        /// 刷新 MiniView 全部显示。按 snapshot.Provider 分发到 MiniMax 双模块或 DeepSeek 三列。
        /// MiniMax：Percent 已经是 0-100 的剩余比例,进度条 fill 固定为灰色,按 ratio 收窄宽度;
        /// 5h 倒计时用 "x 时 y 分",周倒计时用 "x 天 x 时 x 分"。
        /// DeepSeek：所有文本由调用方格式化好,直接显示;hasTokens=false 时 Token 后缀与三行 foot 隐藏。
        /// </summary>
        public void Update(MiniViewSnapshot snapshot, string statusText)
        {
            if (string.Equals(snapshot.Provider, PaperState.MiniMax, StringComparison.Ordinal))
            {
                // 整个 maxRootGrid 显示,dsRootGrid 隐藏,互斥且不互相影响 layout。
                _maxRootGrid.Visibility = Visibility.Visible;
                _dsRootGrid.Visibility = Visibility.Collapsed;

                if (snapshot.MaxData is { } max)
                {
                    _hourlyPercent.Text = FormatPercent(max.Percent);
                    var hourlyRatio = Math.Clamp(max.Percent / 100.0, 0, 1);
                    _lastHourlyRatio = hourlyRatio;
                    _hourlyFill.Fill = _grayBrush;
                    _hourlyFill.Width = Math.Max(0, _hourlyBarGrid.ActualWidth * hourlyRatio);
                    _hourlyReset.Text = string.IsNullOrEmpty(statusText)
                        ? FormatRemaining("距离下次重置还有", max.RemainingHours, includeDays: false)
                        : statusText;

                    _weeklyPercent.Text = FormatPercent(max.WeeklyPercent);
                    var weeklyRatio = Math.Clamp(max.WeeklyPercent / 100.0, 0, 1);
                    _lastWeeklyRatio = weeklyRatio;
                    _weeklyFill.Fill = _grayBrush;
                    _weeklyFill.Width = Math.Max(0, _weeklyBarGrid.ActualWidth * weeklyRatio);
                    _weeklyReset.Text = FormatRemaining("距离下次重置还有", max.WeeklyHours, includeDays: true);

                    _footer.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                }
            }
            else if (string.Equals(snapshot.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
            {
                _maxRootGrid.Visibility = Visibility.Collapsed;
                _dsRootGrid.Visibility = Visibility.Visible;

                if (snapshot.DeepSeekData is { } ds)
                {
                    _dsCol1Value.Text = string.IsNullOrEmpty(ds.TodayCostText) ? "—" : ds.TodayCostText;
                    _dsCol1Foot.Text = string.IsNullOrEmpty(ds.CostTodayFoot) ? "" : ds.CostTodayFoot;

                    _dsCol2Value.Text = string.IsNullOrEmpty(ds.Cost7dText) ? "—" : ds.Cost7dText;
                    _dsCol2Foot.Text = string.IsNullOrEmpty(ds.Cost7dFoot) ? "—" : ds.Cost7dFoot;

                    var hasTokens = ds.TodayTokensText != "—";
                    _dsCol3ValueNumber.Text = ds.TodayTokensText;
                    _dsCol3ValueSuffix.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                    _dsCol3Foot1.Text = hasTokens ? ds.TodayTokensWan : "";
                    _dsCol3Foot1.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                    _dsCol3Foot2.Text = hasTokens ? ds.TodayHitText : "";
                    _dsCol3Foot2.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                    _dsCol3Foot3.Text = ds.TodayCacheRate ?? "";
                    _dsCol3Foot3.Visibility = !string.IsNullOrEmpty(ds.TodayCacheRate) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private static string FormatPercent(double percent)
        {
            if (double.IsNaN(percent))
            {
                return "—";
            }
            var rounded = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
            return rounded.ToString(CultureInfo.CurrentCulture) + "%";
        }

        /// <summary>
        /// 把小时数格式化为剩余时长。
        /// 5h（includeDays=false）："x 时 y 分"，小时为 0 时简化为 "x 分"。
        /// 周（includeDays=true）："x 天 x 时 x 分"，三段都显示不省略。
        /// NaN / 0 / 负数视为未拉取。
        /// </summary>
        private static string FormatRemaining(string prefix, double hours, bool includeDays)
        {
            if (double.IsNaN(hours) || hours <= 0)
            {
                return "尚未拉取";
            }
            var totalHours = (int)Math.Floor(hours);
            var m = (int)Math.Round((hours - totalHours) * 60);
            if (m == 60)
            {
                m = 0;
                totalHours += 1;
            }
            if (!includeDays)
            {
                return totalHours == 0
                    ? prefix + m + "分"
                    : prefix + totalHours + "时" + m + "分";
            }
            var days = totalHours / 24;
            var h = totalHours % 24;
            return prefix + days + "天" + h + "时" + m + "分";
        }

        private FontFamily ResolveFontFamily(string source)
        {
            if (_cachedFontFamily != null && string.Equals(_cachedFontFamilySource, source, StringComparison.Ordinal))
            {
                return _cachedFontFamily;
            }
            _cachedFontFamily = new FontFamily(source);
            _cachedFontFamilySource = source;
            return _cachedFontFamily;
        }

        private static SolidColorBrush ToBrush(string value, string fallback)
        {
            SolidColorBrush brush;
            try
            {
                brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(value)!);
            }
            catch
            {
                try
                {
                    brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(fallback)!);
                }
                catch
                {
                    brush = new SolidColorBrush(Colors.Gray);
                }
            }
            // 冻结 brush：让 WPF 渲染系统走快路径，并允许跨线程共享。
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 1.8 边缘预览视图统一快照：按 Provider 路由到 MiniMax 双进度条或 DeepSeek 三列卡片。
        /// </summary>
        public readonly record struct MiniViewSnapshot(
            string Provider,
            MiniMaxQuota? MaxData,
            DeepSeekMetrics? DeepSeekData);

        /// <summary>
        /// MiniMax 双模块数据(每五小时 + 周额度)。
        /// </summary>
        public readonly record struct MiniMaxQuota(
            double Percent,
            double RemainingHours,
            double WeeklyPercent,
            double WeeklyHours);

        /// <summary>
        /// DeepSeek 三列卡片数据：所有文本已格式化,view 不再做除法。
        /// </summary>
        public readonly record struct DeepSeekMetrics(
            string TodayCostText,     // "¥0.08" 或 "—"
            string CostTodayFoot,     // "相较昨日 ↑12.0%" 或 ""
            string Cost7dText,        // "¥12.35" 或 "—"
            string Cost7dFoot,        // "日均 ¥1.76"
            string TodayTokensText,   // "50,336" 或 "—"
            string TodayTokensWan,    // "≈ 5.0万"
            string TodayHitText,      // "缓存命中: 25,088 Tokens"
            string? TodayCacheRate);  // "50.10%" 或 null(隐藏该行)
    }

}
