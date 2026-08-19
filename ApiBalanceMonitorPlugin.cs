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
/// 实现要点（不修改宿主）：
/// - 胶囊由插件自己渲染（IPaperCapsuleViewProvider，协议 1.7）。自定义视图
///   BalanceCapsuleView 完全 1:1 复刻宿主 1.6 模板的视觉：左 6 padding + 18 圆环 +
///   间距 5 + 填充文本，圆环绘制 1:1 移植宿主 CapsuleProgressRing。宿主只在
///   Theme / Surface / Width 这三个维度提供上下文。
/// - SetCapsulePresentation 仍被调用，但仅作为协议层通道传 ToolTip / PlainText
///   / PreferredWidth(=AutomaticWidth)，Components 仅作非空校验占位——customView
///   存在时宿主不会渲染 1.6 模板（见 PaperWindow.PluginCapsule.cs:BuildPluginCapsuleContent）。
/// - 设置项由宿主自带的"插件"设置页绘制（boolean / string / number / select 四类）。
/// - 鉴权信息（apiKey）会随设置写入 plugins/data/api.balance.monitor.json（明文），
///   因此在 plugin.json 的 description 中明确告知用户并建议使用只读子 key。
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
    bool ShowPercentage);

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

internal sealed class BalanceSession : IPaperBodySession, IPaperCapsuleViewProvider
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
    private BalanceSnapshot _snapshot = BalanceSnapshot.Empty("尚未拉取");
    private string _lastCapsuleSignature = "";
    private int _polling;
    private UsageDay[]? _usageDays;
    private CostDay[]? _costDays;
    // 今日各模型消费明细：model -> cost（元）。仅保留今日与昨日，便于卡片展示。
    private Dictionary<string, double>? _costTodayByModel;
    private double? _minimaxRemainingPercent;
    private List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours, long WeeklyStart, long WeeklyEnd)>? _minimaxModelRemains;
    private PaperBodyTheme _theme;

    // 1.7 胶囊自定义视图：宿主为每个 surface 至多请求一次并缓存，宽度变化时重建。
    // 在 UpdateSnapshot 里原地更新它们，避免 SetCapsulePresentation 触发重建抖动。
    private BalanceCapsuleView? _regularCapsuleView;
    private BalanceCapsuleView? _dockedCapsuleView;
    // CreateCapsuleView 在首次被宿主调用前就需要拿到最新状态，所以单独缓存一份快照。
    private string _capsuleText = "—";
    private string _capsuleRingColorHex = "#9E9E9E";
    private double _capsuleRingArc;
    // DeepSeek 高峰时段太阳图标：true 时在余额右侧显示太阳；其它供应商 / 非高峰隐藏。
    private bool _capsuleIsPeakHour;

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

        // 高峰时段哨兵：30 秒粒度足够覆盖 9:00 / 12:00 / 14:00 / 18:00 四个切换点
        // （30 秒 × 60 = 30 分钟，足以漂移到下个时段起点）。Priority=Background 避免与
        // 数据拉取争抢 UI 线程。
        _peakCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _peakCheckTimer.Tick += (_, _) => RefreshPeakHour();

        ApplySettings(_settings);
        // 哨兵 timer 始终运行（与 backgroundUpdates 一致），首次启动即同步当前状态。
        RefreshPeakHour();
        if (!_peakCheckTimer.IsEnabled)
        {
            _peakCheckTimer.Start();
        }
        // WebView2 在 View 首次布局后初始化（TryStartWebView），构造时不主动拉取，
        // 等 timer 首次触发，避免阻塞宿主启动。
    }

    public FrameworkElement View => _viewRoot;

    public void Commit() { /* 设置由宿主管理，正文无草稿 */ }
    public void RefreshFromModel() { /* 无外部数据源需要刷新 */ }
    public void CancelInteractions() { /* 无交互状态 */ }

    /// <summary>
    /// 高峰时段哨兵：每 30 秒检查 UTC+8 是否进入/离开高峰窗口。
    /// 状态变化时复用 UpdateSnapshot——它会把新 isPeakHour 写入 signature，
    /// 进而触发缓存视图 Update 与 SetCapsulePresentation（含动态 Components）。
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
    public void OnVisibilityChanged(bool visible)
    {
        // 折叠成胶囊后仍按 backgroundUpdates 继续轮询，无需特别处理。
    }
    public void OnPresentationChanged(bool expanded) { }
    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        // 1.7 自定义视图需要跟随主题切换重新设置字体/颜色；视图尚未创建时空操作。
        _regularCapsuleView?.ApplyTheme(theme);
        _dockedCapsuleView?.ApplyTheme(theme);
        PushView();
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);
    public void OnDpiChanged() { }

    public void OnSettingsChanged(string settingsJson)
    {
        // Provider 来自 per-paper state，不再从全局 settings 读取。
        ApplySettings(ReadSettings(settingsJson, _state.Provider));
    }

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
        var interval = TimeSpan.FromSeconds(
            Math.Max(15, Math.Min(3600, s.PollSeconds)));
        _timer.Interval = interval;
        // Provider 变化已迁到 SetPaperProvider；此处只处理 timer/重拉。
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
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
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
                double WeeklyPercent, double WeeklyHours, long WeeklyStart, long WeeklyEnd)>();
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
                var weeklyStart = TryReadNumber(m, "weekly_start_time");
                var weeklyEnd = TryReadNumber(m, "weekly_end_time");
                if (ms.HasValue)
                {
                    modelList.Add((
                        string.IsNullOrEmpty(name) ? "model" : name,
                        pct ?? 100,
                        ms.Value / 3600000.0,
                        weeklyPct ?? 100,
                        (weeklyMs ?? 0) / 3600000.0,
                        weeklyStart.HasValue ? (long)weeklyStart.Value : 0,
                        weeklyEnd.HasValue ? (long)weeklyEnd.Value : 0));
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
    /// 按主题字体精确测量文本宽度（DIP）。用 TextBlock.Measure() + DesiredSize.Width，
    /// 与 customView 中 TextBlock 实际 layout 完全同源，避免 FormattedText 与 TextBlock
    /// 之间的字体回退 / LineHeight / 亚像素舍入差异（差额 0.1 DIP 仍会被截断）。
    /// 测量失败回退线性估算（每个 ASCII 字符 7 DIP）。
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

        // DeepSeek 高峰时段（UTC+8 9-12 / 14-18）在余额右侧显示太阳图标；
        // 非高峰 / MiniMax / OpenCode 不显示。
        var isPeakHour = string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal)
            && IsPeakHourUtc8();

        var signature = text + "|" + riskRatio.ToString("F3", CultureInfo.InvariantCulture) + "|" + ringColor + "|" + isPeakHour + "|" + snapshot.StatusText;
        if (!string.Equals(signature, _lastCapsuleSignature, StringComparison.Ordinal))
        {
            // 胶囊只在内容真正变化时更新，避免无谓的宿主布局抖动。
            _lastCapsuleSignature = signature;

            // 1) 写共享字段：CreateCapsuleView 首次被宿主调用时会从这里取值。
            _capsuleText = text;
            _capsuleRingColorHex = ringColor;
            _capsuleRingArc = ringArc;
            _capsuleIsPeakHour = isPeakHour;

            // 2) 原地更新两个已缓存的 1.7 自定义视图（Regular / Docked）。
            //    宿主会优先使用 customView 渲染胶囊，这里保证视图跟随状态刷新。
            _regularCapsuleView?.Update(text, ringColor, ringArc, isPeakHour);
            _dockedCapsuleView?.Update(text, ringColor, ringArc, isPeakHour);

            // 3) 协议层通道：SetCapsulePresentation 必须调用，否则宿主判定
            //    `_pluginCapsulePresentation == null` 会清空胶囊槽、不请求 customView。
            //    PreferredWidth = 全部固定列宽(38) + textWidth + sunWidth + 0.1 余量。
            //    Grid 列布局 [6 pad][18 ring][5 gap][* text][5 gap][auto sun][4 right pad]，
            //    固定列总宽 = 6+18+5+5+4 = 38,Auto 列(sun)宽 = sunWidth (Visible=14, Collapsed=0)。
            //    customView 实际宽度 = 38 + textWidth + sunWidth,差额 0.1 极致贴边。
            //    textWidth 用 MeasureTextWidth(主题字体 TextBlock.Measure + DesiredSize.Width)
            //    与 customView 渲染完全同源，避免亚像素舍入差异导致省略。
            //    Components 保留 1 项最小 Text 占位（Length > 0 让 Normalize 不返回 null，
            //    customView != null 时宿主跳过 1.6 模板不渲染它们）。
            //    ToolTip 由宿主写到外壳 Border（1.7 视图 IsHitTestVisible=false 无法自己挂 ToolTip）；
            //    PlainText 用于跨队列拖动的纯文字回退。
            var textWidth = Math.Ceiling(MeasureTextWidth(text));
            var sunWidth = isPeakHour ? 14 : 0;
            var preferredWidth = 6 + 18 + 5 + textWidth + 5 + sunWidth + 4 + 0.1;
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

        // 面板（HTML）每次拉取后都推送：余额可能不变但用量/时间变了。
        PushView();
    }

    /// <summary>
    /// 协议 1.7 自定义胶囊视图入口：宿主为 Regular / Docked 两个 surface 各至多调一次，
    /// 把返回值缓存；宽度变化时再以新 Context 重新调用。
    /// 约束：每次必须返回 fresh unparented FrameworkElement，宿主会校验 Parent==null。
    /// </summary>
    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        var view = new BalanceCapsuleView(context);
        // 首次返回时立即把当前最新状态填入，避免宿主先展示一个空 view 再被 Update 刷新。
        view.Update(_capsuleText, _capsuleRingColorHex, _capsuleRingArc, _capsuleIsPeakHour);
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

    /// <summary>
    /// v3.1 颜色：Safe 绿 / Warming 黄 / Danger 橙 / Overrun 红。
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
    /// <summary>
    /// 把 NaN/±Infinity 归一为 0，避免 JsonSerializer 序列化时抛异常。
    /// </summary>
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>
    /// 可空版本的 Finite：非有限值返回 null。
    /// </summary>
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

    /// <summary>
    /// 按供应商选择面板 HTML 文件。DeepSeek / MiniMax / OpenCode Go 各自独立面板。
    /// </summary>
    private static string HtmlFileNameFor(string provider) => provider switch
    {
        "minimax" => "minimax.html",
        "opencode" => "opencode.html",
        _ => "monitor.html"
    };

    /// <summary>
    /// 供应商切换后重新导航 WebView2 到对应供应商的面板 HTML。
    /// </summary>
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
        // 页面 JS 就绪后发送 {type:"ready"}，宿主立即补发最新数据。
        // 页面能发出 ready 说明消息监听已挂载，无需再等 NavigationCompleted。
        // 另支持 {type:"switchProvider", provider:"..."} 切换当前 paper 的供应商。
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
            // 页面消息解析异常不影响面板主体，静默。
        }
    }

    private static bool IsValidProvider(string p) =>
        p == PaperState.DeepSeek || p == PaperState.MiniMax || p == PaperState.OpenCode;

    /// <summary>
    /// 切换当前 paper 的供应商：写 per-paper state + 重载面板 + 立即拉取。
    /// </summary>
    private void SetPaperProvider(string newProvider)
    {
        if (string.Equals(_state.Provider, newProvider, StringComparison.Ordinal))
        {
            return;
        }
        _state = new PaperState(newProvider);
        // 重新读取设置以应用新 provider 对应的 Key。
        _settings = ReadSettings(_context.SettingsJson, _state.Provider);
        try
        {
            _context.SaveStateJson(SerializeState(_state));
        }
        catch
        {
            // 状态写失败不致命；本会话内仍按新 provider 工作。
        }
        ReloadPanelForProvider();
        // 重置 snapshot 避免显示旧 provider 的残留数据。
        _minimaxModelRemains = null;
        _minimaxRemainingPercent = null;
        _ = PollAsync();
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

    /// <summary>
    /// 组装推给 HTML 的 JSON：{ theme, data }。data 含状态、余额、风险、用量、更新时间。
    /// </summary>
    private string BuildViewPayload()
    {
        var status = _snapshot.StatusText ?? "";
        var hasData = _snapshot.HasRemaining && !double.IsNaN(_snapshot.Remaining);
        // MiniMax：余额是时长额度，风险环用"已消耗比例"（100 − 剩余百分比）。
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
            ["paper"] = NormalizeHex(_theme.PaperColor, "#FFF8E6"),
            ["fontScale"] = Math.Clamp(_theme.FontScale, 0.85, 1.2)
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
            ["remainingPercent"] = _minimaxRemainingPercent.HasValue
                ? (double?)Math.Clamp(_minimaxRemainingPercent.Value, 0, 100)
                : null,
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
    /// 今日各模型消费明细数组（按金额降序），用于"今日消费金额"卡片下方的模型分布。
    /// </summary>
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

    /// <summary>
    /// MiniMax 各模型剩余额度数组（供 minimax.html 渲染）：model + 剩余百分比 + 剩余小时。
    /// </summary>
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
                ["weeklyHours"] = Math.Round(x.WeeklyHours, 1),
                ["weeklyStart"] = x.WeeklyStart,
                ["weeklyEnd"] = x.WeeklyEnd
            })
            .ToArray();
    }

    /// <summary>
    /// 今日用量明细；当天无数据返回 null。
    /// </summary>
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

    /// <summary>
    /// 近 7 天（含今天）消费总额；无数据返回 0。
    /// </summary>
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

    /// <summary>
    /// 近 7 天日均消费文案（"日均 ¥X.XX"）；数据源存在即返回（可能为 ¥0.00）。
    /// </summary>
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

    /// <summary>
    /// v3.1 风险环颜色（含过渡渐变）：绿 → 黄 → 橙 → 红；未配置阈值时灰。
    /// </summary>
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
        private readonly BalanceSunIcon _sun;

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

            // 列布局：[6 pad][18 ring][5 gap][* text][5 gap][auto sun][4 right pad]
            // sun 后 4 DIP padding 让图标不贴右边界；差额恒为 6（右 padding + sun 后间距）。
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

            _sun = new BalanceSunIcon
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(_sun, 5);
            Children.Add(_sun);

            ApplyTheme(context.Theme);
        }

        /// <summary>
        /// 刷新胶囊状态。仅设文本与圆环颜色 / 弧值，圆环底色由 ApplyTheme 设置；
        /// isPeakHour=true 时（DeepSeek UTC+8 高峰时段）在余额右侧显示太阳图标，其它时刻隐藏。
        /// </summary>
        public void Update(
            string text,
            string ringColorHex,
            double ringArc,
            bool isPeakHour)
        {
            _label.Text = text;
            _ring.Value = Math.Clamp(ringArc, 0, 1);
            _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
            _sun.Visibility = isPeakHour ? Visibility.Visible : Visibility.Collapsed;
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
    /// 太阳图标：用 WPF 几何图元绘制 SVG 风格的太阳（中心圆盘 + 8 条光芒）。
    /// 14×14 DIP，固定大小，不参与主题切换。
    /// </summary>
    private sealed class BalanceSunIcon : Canvas
    {
        private const double Size = 14;
        private const double Center = 7;
        private const double CoreRadius = 2.6;
        private const double RayInner = 4.6;
        private const double RayOuter = 7;
        private const double StrokeThickness = 1.4;

        public BalanceSunIcon()
        {
            Width = Size;
            Height = Size;
            IsHitTestVisible = false;
            ClipToBounds = false;

            // 太阳主色：amber，与插件风险色 Warming(#FFC107) 同源。
            var sunBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
            sunBrush.Freeze();

            // 中心圆盘
            var core = new Ellipse
            {
                Width = CoreRadius * 2,
                Height = CoreRadius * 2,
                Fill = sunBrush
            };
            SetLeft(core, Center - CoreRadius);
            SetTop(core, Center - CoreRadius);
            Children.Add(core);

            // 8 条光芒（每 45° 一条），圆角端点让图标柔和
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                var ray = new Line
                {
                    X1 = Center + cos * RayInner,
                    Y1 = Center + sin * RayInner,
                    X2 = Center + cos * RayOuter,
                    Y2 = Center + sin * RayOuter,
                    Stroke = sunBrush,
                    StrokeThickness = StrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                Children.Add(ray);
            }
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

}
