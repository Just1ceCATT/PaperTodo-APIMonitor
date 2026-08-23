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
    // 30 分钟滑动窗口趋势分析器：只吃成功拉取的 DeepSeek 余额样本，纯内存不持久化，
    // 插件重启后重新积累（前 5 分钟显示"采集中"）。与阈值风险是两套独立指标。
    private readonly TrendAnalyzer _trend = new();

    // Internal getter:供 Payload/ViewPayloadBuilder 只读访问字段,不暴露给宿主。
    internal BalanceSnapshot LatestSnapshot => _snapshot;
    internal BalanceSettings CurrentSettings => _settings;
    internal PaperState CurrentState => _state;
    internal PaperBodyTheme CurrentTheme => _theme;
    internal UsageDay[]? UsageDays => _usageDays;
    internal CostDay[]? CostDays => _costDays;
    internal Dictionary<string, double>? CostTodayByModel => _costTodayByModel;
    internal List<(string Model, double Percent, double Hours, double WeeklyPercent, double WeeklyHours)>? MiniMaxModelRemains => _minimaxModelRemains;
    /// <summary>当前 30 分钟窗口的余额趋势。每次读取都基于当前时间重新裁剪窗口并回归。</summary>
    internal BalanceTrend CurrentTrend => _trend.Analyze(DateTime.Now, _snapshot.Remaining);

    // 1.7 胶囊自定义视图：宿主为每个 surface 至多请求一次并缓存，宽度变化时重建。
    // 在 UpdateSnapshot 里原地更新它们，避免 SetCapsulePresentation 触发重建抖动。
    // MiniMax 与 DeepSeek 各有独立的胶囊类（BalanceRingCapsuleView / BalanceDotCapsuleView），
    // 因此分别持有两份字段，互不干涉。provider 切换时清空，避免缓存视图类型不匹配。
    private BalanceRingCapsuleView? _regularRingCapsuleView;
    private BalanceRingCapsuleView? _dockedRingCapsuleView;
    private BalanceDotCapsuleView? _regularDotCapsuleView;
    private BalanceDotCapsuleView? _dockedDotCapsuleView;
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
        _regularRingCapsuleView = null;
        _dockedRingCapsuleView = null;
        _regularDotCapsuleView = null;
        _dockedDotCapsuleView = null;
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
        // 清空所有胶囊视图缓存：不同 provider 对应不同胶囊类，下一次 CreateCapsuleView
        // 会按新 provider 实例化正确类型。
        _regularRingCapsuleView = null;
        _dockedRingCapsuleView = null;
        _regularDotCapsuleView = null;
        _dockedDotCapsuleView = null;
        // 趋势窗口是按 provider 的余额口径积累的，跨 provider 复用会得到无意义的斜率。
        _trend.Reset();
        _ = PollAsync();
    }

    public void OnActivated() { }
    public void OnDeactivated() { }
    public void OnVisibilityChanged(bool visible) { }
    public void OnPresentationChanged(bool expanded) { }
    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        // 主题切换：同时更新两种胶囊缓存（provider 可能切换过），由胶囊内部 ApplyTheme
        // 各自处理 Brush 重建。
        _regularRingCapsuleView?.ApplyTheme(theme);
        _dockedRingCapsuleView?.ApplyTheme(theme);
        _regularDotCapsuleView?.ApplyTheme(theme);
        _dockedDotCapsuleView?.ApplyTheme(theme);
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
            RecordTrendSample(balanceTask.Result, now);
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
    /// 记录趋势样本。只有 DeepSeek 且本次余额确实拉取成功时才写入——
    /// 请求失败 / 超时 / 解析失败一律不喂，避免用虚假数据污染趋势窗口。
    /// </summary>
    private void RecordTrendSample(BalanceSnapshot snapshot, DateTime now)
    {
        if (!string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
        {
            return;
        }
        if (!snapshot.HasRemaining || !double.IsFinite(snapshot.Remaining))
        {
            return;
        }
        _trend.Add(now, snapshot.Remaining);
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

        // 趋势：每次刷新都重算（窗口是滑动窗口，时间会裁剪样本）；和阈值是两套独立指标。
        var trend = CurrentTrend;

        var text = BuildCapsuleText(snapshot, _settings, _state, riskRatio, _costDays);
        var toolTip = string.IsNullOrEmpty(snapshot.StatusText)
            ? text
            : $"{text}\n{snapshot.StatusText}";

        // 胶囊 signature：文本/风险比/颜色/状态文本 之外追加趋势等级，避免趋势变化时漏刷新胶囊。
        var signature = text + "|" + riskRatio.ToString("F3", CultureInfo.InvariantCulture) + "|" + ringColor + "|" + snapshot.StatusText + "|" + TrendAnalyzer.LevelKey(trend.Level) + "|" + trend.SampleCount.ToString(CultureInfo.InvariantCulture);
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
            //    按 provider 分发到不同的胶囊类 —— 两种胶囊互不继承，改动其中一个不会牵连另一个。
            if (isMiniMax)
            {
                _regularRingCapsuleView?.Update(text, ringColor, ringArc);
                _dockedRingCapsuleView?.Update(text, ringColor, ringArc);
            }
            else
            {
                _regularDotCapsuleView?.Update(text, ringColor, ringArc, ringColor, _lastIsPeakHour);
                _dockedDotCapsuleView?.Update(text, ringColor, ringArc, ringColor, _lastIsPeakHour);
            }

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
    /// 按当前 provider 分发到独立的胶囊类：MiniMax → BalanceRingCapsuleView，
    /// DeepSeek / OpenCode → BalanceDotCapsuleView。两种胶囊互不继承、各自封装指示器。
    /// </summary>
    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        if (IsMiniMax)
        {
            var ringView = new BalanceRingCapsuleView(context);
            // 首次返回时立即填入最新状态，避免宿主先展示空 view 再被 Update 刷新。
            ringView.Update(_latestCapsuleSnapshot.Text, _latestCapsuleSnapshot.RingColorHex, _latestCapsuleSnapshot.RingArc);
            if (context.Surface == PaperCapsuleSurfaceKind.Docked)
            {
                _dockedRingCapsuleView = ringView;
            }
            else
            {
                _regularRingCapsuleView = ringView;
            }
            return ringView;
        }

        var dotView = new BalanceDotCapsuleView(context);
        dotView.Update(
            _latestCapsuleSnapshot.Text,
            _latestCapsuleSnapshot.RingColorHex,
            _latestCapsuleSnapshot.RingArc,
            dotColorHex: _latestCapsuleSnapshot.RingColorHex,
            isPeakHour: _lastIsPeakHour);
        if (context.Surface == PaperCapsuleSurfaceKind.Docked)
        {
            _dockedDotCapsuleView = dotView;
        }
        else
        {
            _regularDotCapsuleView = dotView;
        }
        return dotView;
    }

    /// <summary>
    /// <summary>
/// 协议 1.8 自定义边缘预览视图：胶囊悬停时暴露 brief 卡片。OpenCode 返回 null 让宿主走 1.6/1.7 回退。
/// </summary>
    /// <summary>
/// 协议 1.8 自定义边缘预览视图：胶囊悬停时暴露 brief 卡片。OpenCode 返回 null 让宿主走 1.6/1.7 回退。
/// DeepSeek(3 行 + footer)需要更大空间,返回 322×232;MiniMax(2 行双模块)改为 280×190;
/// OpenCode 返回 null view 但宿主仍需尺寸用于布局占位,同样返回 280×190。
/// </summary>
public PaperMiniViewSize PreferredMiniViewSize
{
    get
    {
        if (string.Equals(_state.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
        {
            return new(322, 232);
        }
        // MiniMax / OpenCode / 默认
        return new(280, 190);
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
        // 显式锁定 MiniView 尺寸为 PreferredMiniViewSize。
        // BalanceMiniView 继承自 Border,默认 VerticalAlignment=Stretch,如果不锁定尺寸,
        // 宿主给的容器如果纵向 Space 偏大,会把整个 MiniView 拉伸,所有内容(图标/文字/进度条)
        // 按比例视觉放大,圆环变椭圆。
        // 锁定后即使父容器空间大于期望值,MiniView 也会按 PreferredMiniViewSize 显示,
        // 剩余空白归父容器处理。
        var size = PreferredMiniViewSize;
        view.Width = size.Width;
        view.Height = size.Height;
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
    /// 胶囊文本：MiniMax 走 "xx% · xx时xx分"；DeepSeek 走两段 "◉ ¥余额 · -¥今日消耗"，
    /// 缺数据的段整段省略（避免出现 "--" / 空分隔符）。百分比圆环由视图层切到呼吸圆点，
    /// 所以 ShowPercentage 不再影响 DeepSeek 文本。
    /// </summary>
    private static string BuildCapsuleText(
        BalanceSnapshot snapshot,
        BalanceSettings settings,
        PaperState state,
        double riskRatio,
        CostDay[]? costDays)
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
        // DeepSeek：两段拼接 "◉ ¥余额 · -¥今日消耗"。
        // 余额段：永远显示，NaN/失败时由 Format.FormatAmount 输出 "—"。
        // 圆点：呼吸圆点模式（趋势段不再出现在胶囊上，只在 MiniView 第 4 行）。
        AppendBalanceSegment(sb, settings, snapshot);
        // 今日消耗段：未配置 UsageToken 或无数据时整段省略（保留余额单段）。
        AppendTodayCostSegment(sb, settings, costDays);
        return sb.ToString();
    }

    /// <summary>余额段：货币符号 + 金额（NaN 由 FormatAmount 输出 "—"）。</summary>
    private static void AppendBalanceSegment(StringBuilder sb, BalanceSettings settings, BalanceSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(settings.CurrencySymbol))
        {
            sb.Append(settings.CurrencySymbol);
        }
        sb.Append(Format.FormatAmount(snapshot.Remaining));
    }

    /// <summary>
    /// 今日消耗段：未配置 UsageToken / 无 _costDays / 今日为 0 时省略。
    /// 始终前置 "-" 表示"减少了"（正号无意义），最终形如 "-¥1.20"。
    /// </summary>
    private static void AppendTodayCostSegment(StringBuilder sb, BalanceSettings settings, CostDay[]? costDays)
    {
        var today = MetricsAggregator.BuildCostTodayText(costDays, settings.CurrencySymbol);
        if (string.IsNullOrEmpty(today))
        {
            return;
        }
        // BuildCostTodayText 已包含 currencySymbol 前缀，这里只补负号与分隔符，
        // 避免双符号（如 "¥¥1.20"）。
        sb.Append(" · -");
        sb.Append(today);
    }

    /// <summary>
    /// 高峰时段判断：UTC+8 的 9:00-12:00 / 14:00-18:00（半开区间，不含 12:00 与 18:00 整点），
    /// 且仅限周一至周五——周末整天不算高峰期，胶囊圆点不呼吸。
    /// 不依赖用户本地时区，始终按北京时间计算——不同地区使用同一时段标准。
    /// </summary>
    private static bool IsPeakHourUtc8()
    {
        var nowUtc8 = DateTime.UtcNow.AddHours(8);
        // DayOfWeek：Sunday=0, Monday=1 ... Saturday=6。周末 = 周六(6) / 周日(0)。
        if (nowUtc8.DayOfWeek == DayOfWeek.Saturday || nowUtc8.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }
        var hour = nowUtc8.Hour;
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
