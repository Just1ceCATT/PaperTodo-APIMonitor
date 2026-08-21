using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperTodo.Plugin;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Payload;
using PaperTodo.Plugin.ApiBalanceMonitor.Rendering;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using PaperTodo.Plugin.ApiBalanceMonitor.WebPanel;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Session;

/// <summary>
/// 插件会话编排器:持有 HttpClient / 两个 DispatcherTimer / WebViewPanelHost / 三个 provider /
/// ViewPayloadBuilder / 视图缓存;接收宿主生命周期回调,转发给子系统。
/// 业务逻辑全部委托给 Services/Payload/Rendering/WebPanel 子模块。
/// </summary>
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
    // 派生数据构造器:组装推给 HTML 的 JSON payload。Payload/ViewPayloadBuilder 持有 Session 引用,
    // 通过 internal getter 读取所需字段(避免 Session 暴露过多 mutable 字段)。
    private readonly ViewPayloadBuilder _payloadBuilder;
    // 三个 provider:DeepSeek 拉余额,MiniMax 拉余额 + model_remains,Usage 拉平台用量与消费。
    // 共用 _http(超时 15s + UA),生命周期与 Session 一致。
    private readonly DeepSeekProvider _deepseek;
    private readonly MiniMaxProvider _minimax;
    private readonly UsageProvider _usage;
    private UsageDay[]? _usageDays;
    private CostDay[]? _costDays;
    // 今日各模型消费明细：model -> cost（元）。仅保留今日与昨日，便于卡片展示。
    private Dictionary<string, double>? _costTodayByModel;
    private double? _minimaxRemainingPercent;
    private List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? _minimaxModelRemains;
    private PaperBodyTheme _theme;

    // Internal getter:供 Payload/ViewPayloadBuilder 只读访问字段,不暴露给宿主。
    internal BalanceSnapshot LatestSnapshot => _snapshot;
    internal BalanceSettings CurrentSettings => _settings;
    internal PaperState CurrentState => _state;
    internal PaperBodyTheme CurrentTheme => _theme;
    internal UsageDay[]? UsageDays => _usageDays;
    internal CostDay[]? CostDays => _costDays;
    internal Dictionary<string, double>? CostTodayByModel => _costTodayByModel;
    internal List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? MiniMaxModelRemains => _minimaxModelRemains;

    // 1.7 胶囊自定义视图：宿主为每个 surface 至多请求一次并缓存，宽度变化时重建。
    // 在 UpdateSnapshot 里原地更新它们，避免 SetCapsulePresentation 触发重建抖动。
    private BalanceCapsuleView? _regularCapsuleView;
    private BalanceCapsuleView? _dockedCapsuleView;
    // 1.8 边缘预览视图：仅 MiniMax 场景保留；非 MiniMax 时 CreateMiniView 返回 null
    // 让宿主切到 1.6/1.7 放大胶囊回退（DescribePluginCapsuleFallback）。
    private BalanceMiniView? _miniView;
    // CreateCapsuleView 在首次被宿主调用前就需要拿到最新状态，所以单独缓存一份不可变快照。
    // 初值 CapsuleSnapshot.Empty 保证首屏胶囊始终有内容（"—" / 灰 / 0 弧）。
    private CapsuleSnapshot _latestCapsuleSnapshot = CapsuleSnapshot.Empty;

    // WebView2 监视面板
    private bool _disposed;
    // WebView2 监视面板封装(替代原 _viewRoot / _webView / _environmentGate / _environmentTask /
    // _webViewInitializationStarted / _webViewReady / _documentReady / _pendingPayload / WebView2 CTS)。
    private readonly WebPanel.WebViewPanelHost _panel;

    public BalanceSession(PaperBodyContext context)
    {
        _context = context;
        _theme = context.Body.Theme;
        _state = ReadState(context.StateJson);
        _settings = ReadSettings(context.SettingsJson, _state.Provider);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PaperTodo.Plugin.ApiBalanceMonitor/1.0");

        _payloadBuilder = new ViewPayloadBuilder(this);
        _deepseek = new DeepSeekProvider(_http);
        _minimax = new MiniMaxProvider(_http);
        _usage = new UsageProvider(_http);
        _panel = new WebPanel.WebViewPanelHost();
        _panel.SetActiveProvider(_state.Provider);
        _panel.WebMessageReceived += OnWebMessageReceived;

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

    public FrameworkElement View => _panel.ViewRoot;

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
        _timer.Stop();
        _peakCheckTimer.Stop();
        _http.Dispose();
        // 清空 1.7 视图缓存：宿主在下次 body session 重建时会请求新的 view，
        // 旧引用指向的元素已经被宿主丢弃，保留只会徒增引用计数。
        _regularCapsuleView = null;
        _dockedCapsuleView = null;
        _miniView = null;
        // WebView2 状态机封装在自己的 Dispose 里(取消 CTS + 释放 _webView)。
        _panel.Dispose();
    }

    /// <summary>WebView2 消息回调:ready → 推送当前 payload;switchProvider → 切换供应商。</summary>
    private void OnWebMessageReceived(string json)
    {
        try
        {
            if (json.IndexOf("\"ready\"", StringComparison.Ordinal) >= 0)
            {
                _panel.PostSnapshot(_payloadBuilder.Build());
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
        _panel.SetActiveProvider(newProvider);
        _panel.ReloadForProvider(newProvider);
        // 重置 snapshot 避免显示旧 provider 的残留数据。
        _minimaxModelRemains = null;
        _minimaxRemainingPercent = null;
        _ = PollAsync();
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
        _panel.PostSnapshot(_payloadBuilder.Build());
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

    // ---------------- 设置应用 ----------------

    // 静态包装：保持原 BalanceSession 内部调用方式不变,逻辑全部迁到 Services.SettingsReader。
    private static BalanceSettings ReadSettings(string? json, string provider) =>
        SettingsReader.ReadSettings(json, provider);
    private static PaperState ReadState(string? json) => SettingsReader.ReadState(json);
    private static string SerializeState(PaperState state) => SettingsReader.SerializeState(state);
    private static bool IsValidProvider(string p) => SettingsReader.IsValidProvider(p);

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
                : _usage.FetchUsageForRecentMonthsAsync(_settings.UsageToken, now);
            var costTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<(CostDay[]? Days, Dictionary<string, double>? TodayByModel)>((null, null))
                : _usage.FetchCostForRecentMonthsAsync(_settings.UsageToken, now);

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
    /// 拉取当前 provider 的余额。MiniMax → MiniMaxProvider;OpenCode → "尚未适配";默认 DeepSeek。
    /// 失败/网络异常走 BalanceSnapshot.Error。
    /// </summary>
    private async Task<BalanceSnapshot> FetchBalanceAsync()
    {
        if (string.Equals(_state.Provider, PaperState.MiniMax, StringComparison.Ordinal))
        {
            var snap = await _minimax.FetchBalanceAsync(_settings.ApiKey).ConfigureAwait(false);
            // 把 model_remains / remaining_percent 同步给 Session 缓存,供 ViewPayload / RiskRatio 使用。
            _minimaxModelRemains = _minimax.ModelRemains;
            _minimaxRemainingPercent = _minimax.RemainingPercent;
            return snap;
        }
        if (string.Equals(_state.Provider, PaperState.OpenCode, StringComparison.Ordinal))
        {
            return BalanceSnapshot.Error("尚未适配该供应商");
        }
        // DeepSeek（默认）
        return await _deepseek.FetchBalanceAsync(_settings.ApiKey, _settings.CurrencySymbol).ConfigureAwait(false);
    }

    /// <summary>
/// MiniMax Coding Plan 用量接口：GET /v1/api/openplatform/coding_plan/remains。
/// 返回各模型的剩余时长（remains_time）与剩余百分比。
/// 逻辑已迁到 Services/MiniMaxProvider,保留此注释段作为归档位置。
/// </summary>


    /// <summary>
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
        var ringColor = RiskClassifier.RingColorHex(riskRatio);
        var ringArc = isMiniMax
            ? Math.Clamp(_minimaxRemainingPercent ?? 100, 0, 100) / 100.0
            : RiskClassifier.RingArcValue(riskRatio);

        var text = BuildCapsuleText(snapshot, _settings, _state, riskRatio);
        var toolTip = string.IsNullOrEmpty(snapshot.StatusText)
            ? text
            : $"{text}\n{snapshot.StatusText}";

        // 胶囊 signature 不再包含 isPeakHour（太阳图标已移除）。
        var signature = text + "|" + riskRatio.ToString("F3", CultureInfo.InvariantCulture) + "|" + ringColor + "|" + snapshot.StatusText;
        var capsule = new CapsuleSnapshot(text, ringColor, ringArc);
        if (!string.Equals(signature, _lastCapsuleSignature, StringComparison.Ordinal))
        {
            // 胶囊只在内容真正变化时更新，避免无谓的宿主布局抖动。
            _lastCapsuleSignature = signature;
            // 不可变胶囊快照：消除 _capsuleText/_capsuleRingColorHex/_capsuleRingArc 共享字段的
            // 隐式时序契约,CreateCapsuleView 直接从这里读取。
            _latestCapsuleSnapshot = capsule;

            // 1) 原地更新两个已缓存的 1.7 自定义视图（Regular / Docked）。
            //    宿主会优先使用 customView 渲染胶囊，这里保证视图跟随状态刷新。
            _regularCapsuleView?.Update(text, ringColor, ringArc);
            _dockedCapsuleView?.Update(text, ringColor, ringArc);

            // 2) 协议层通道：SetCapsulePresentation 必须调用，否则宿主判定
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
        _panel.PostSnapshot(_payloadBuilder.Build());
    }

    /// <summary>
    /// 协议 1.7 自定义胶囊视图：宿主为 Regular / Docked 各调一次并缓存；宽度变化时重新调用。
    /// 必须返回 fresh unparented FrameworkElement（宿主校验 Parent==null）。
    /// </summary>
    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        var view = new BalanceCapsuleView(context);
        // 首次返回时立即填入最新状态，避免宿主先展示空 view 再被 Update 刷新。
        // 不可变 CapsuleSnapshot:无隐式时序契约,_latestCapsuleSnapshot 初值 = CapsuleSnapshot.Empty
        // 保证首屏胶囊始终有内容,不会空白。
        view.Update(_latestCapsuleSnapshot.Text, _latestCapsuleSnapshot.RingColorHex, _latestCapsuleSnapshot.RingArc);
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
    /// <summary>
/// 协议 1.8 自定义边缘预览视图：胶囊悬停时暴露 brief 卡片。OpenCode 返回 null 让宿主走 1.6/1.7 回退。
/// DeepSeek(3 行 + footer)需要更大空间,返回 322×207(15% 放大);MiniMax(2 行双模块)保持 280×180;
/// OpenCode 返回 null view 但宿主仍需尺寸用于布局占位,同样返回 280×180。
/// </summary>
public PaperMiniViewSize PreferredMiniViewSize
{
    get
    {
        if (string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
        {
            return new(322, 207);
        }
        // MiniMax / OpenCode / 默认
        return new(280, 180);
    }
}

    public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
    {
        if (string.Equals(_state.Provider, PaperState.OpenCode, StringComparison.Ordinal))
        {
            return null;
        }
        var view = new BalanceMiniView(_miniViewFontFamily, context);
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
            var todayCost = MetricsAggregator.BuildCostTodayText(_costDays, _settings.CurrencySymbol);
            var change = MetricsAggregator.BuildCostChange(_costDays);
            var cost7d = MetricsAggregator.BuildCost7dText(_costDays, _settings.CurrencySymbol);
            var cost7dFoot = MetricsAggregator.BuildCost7dFoot(_costDays, _settings.CurrencySymbol);
            var todayTokens = MetricsAggregator.BuildTodayTokens(_usageDays);
            var todayHit = MetricsAggregator.BuildTodayHit(_usageDays);
            var cacheRate = MetricsAggregator.BuildTodayCacheRate(_usageDays);
            var sparkline = MetricsAggregator.BuildCostSparkline(_costDays, 7);

            var ds = new BalanceMiniView.DeepSeekMetrics(
                TodayCostText: string.IsNullOrEmpty(todayCost) ? "" : todayCost,
                ChangeDirection: change.Direction,
                ChangePercent: change.Percent,
                Cost7dText: string.IsNullOrEmpty(cost7d) ? "" : cost7d,
                Cost7dFoot: cost7dFoot,
                TodayTokensText: Format.FormatTokens(todayTokens),
                TodayHitText: Format.FormatThousands(todayHit),
                CacheRateText: Format.FormatCacheRate(cacheRate),
                Sparkline: sparkline,
                IsPeakHour: _lastIsPeakHour);
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
        // 无数据时 Format.FormatAmount(NaN) 输出 "—"。
        sb.Append(Format.FormatAmount(snapshot.Remaining));
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
    /// 当前供应商是否为 MiniMax（额度按时长计费）。
    /// internal:供 Payload/ViewPayloadBuilder 读取。
    /// </summary>
    internal bool IsMiniMax =>
        string.Equals(_state.Provider, PaperState.MiniMax, StringComparison.Ordinal);

    /// <summary>
    /// 当前供应商的风险比例：MiniMax 用"已消耗比例"（100 − 剩余百分比），
    /// DeepSeek 用阈值/余额；统一过滤 NaN/Infinity。
    /// internal:供 Payload/ViewPayloadBuilder 读取。
    /// </summary>
    internal double ComputeRiskRatioForCurrent()
    {
        if (IsMiniMax && _minimaxRemainingPercent.HasValue)
        {
            return RiskClassifier.Finite((100 - _minimaxRemainingPercent.Value) / 100.0);
        }
        return RiskClassifier.Finite(RiskClassifier.ComputeRiskRatio(_snapshot.Remaining, _settings.BalanceThreshold));
    }

}
