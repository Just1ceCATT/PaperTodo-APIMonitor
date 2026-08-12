using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    public Version Version => new(1, 0, 0);
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
/// </summary>
internal sealed record UsageDay(string Date, double Tokens);

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
    private PaperBodyTheme _theme;

    // 监视面板控件
    private ScrollViewer _viewRoot = null!;
    private TextBlock _titleText = null!;
    private Ellipse _statusDot = null!;
    private TextBlock _statusText = null!;
    private TextBlock _balanceText = null!;
    private TextBlock _currencyText = null!;
    private MonitorRiskRing _riskRing = null!;
    private TextBlock _riskStateText = null!;
    private TextBlock _thresholdText = null!;
    private TextBlock _updateTimeText = null!;
    private TextBlock _usageChartLabel = null!;
    private UsageChart _usageChart = null!;

    public BalanceSession(PaperBodyContext context)
    {
        _context = context;
        _theme = context.Body.Theme;
        _settings = ReadSettings(context.SettingsJson);

        BuildMonitorView(_theme);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PaperTodo.Plugin.ApiBalanceMonitor/1.0");

        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += async (_, _) => await PollAsync();

        ApplySettings(_settings);
        RefreshMonitorView();
        // 构造时不主动拉取，等 timer 首次触发，避免阻塞宿主启动。
    }

    public FrameworkElement View => _viewRoot;

    public void Commit() { /* 设置由宿主管理，正文无草稿 */ }
    public void RefreshFromModel() { /* 无外部数据源需要刷新 */ }
    public void CancelInteractions() { /* 无交互状态 */ }
    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
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
        ApplyMonitorTheme(theme);
        RefreshMonitorView();
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
            // 余额与用量并行拉取（v3.1 收集方式）；用量 Token 未配置时只拉余额。
            var now = DateTime.Now;
            var balanceTask = FetchBalanceAsync();
            var usageTask = string.IsNullOrWhiteSpace(_settings.UsageToken)
                ? Task.FromResult<UsageDay[]?>(null)
                : FetchUsageAsync(_settings.UsageToken, now.Year, now.Month);

            await Task.WhenAll(balanceTask, usageTask).ConfigureAwait(true);
            _usageDays = usageTask.Result;
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
                result.Add(new UsageDay(date, total));
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
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
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

        // 面板（含用量柱状图）每次拉取后都刷新：余额可能不变但用量/时间变了。
        RefreshMonitorView();
    }

    /// <summary>
    /// 胶囊文本：货币符号 + 余额 +（可选）百分比，v3.1 风格 "¥12.34 · 6%"。
    /// 文本由宿主 1.6 模板用宿主胶囊字体渲染；宿主支持胶囊宽度随内容自适应，
    /// 因此 " · " 分隔不会截断。
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

    // ---------------- 展开后的监视面板 ----------------

    // 风险色（v3.1 语义）
    private static readonly Color RiskGreen = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color RiskYellow = Color.FromRgb(0xFF, 0xC1, 0x07);
    private static readonly Color RiskOrange = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color RiskRed = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color RiskGray = Color.FromRgb(0x9E, 0x9E, 0x9E);

    private static readonly Brush GreenBrush = new SolidColorBrush(RiskGreen);
    private static readonly Brush RedBrush = new SolidColorBrush(RiskRed);
    private static readonly Brush GrayBrush = new SolidColorBrush(RiskGray);

    /// <summary>
    /// 构建监视面板（展开便签后的正文）。结构参考 v3.1 的 DeepSeek 监视器：
    /// 标题行 + 在线状态点、大字余额 + 币种、大号风险环 + 风险状态/阈值、更新时间。
    /// 宿主把正文 View 放进透明 Grid 且不自带滚动，这里用 ScrollViewer 自行容纳长内容。
    /// </summary>
    private void BuildMonitorView(PaperBodyTheme theme)
    {
        _titleText = new TextBlock
        {
            Text = "API 余额监测",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = GrayBrush,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusText = new TextBlock
        {
            Text = "等待数据…",
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusStack.Children.Add(_statusDot);
        statusStack.Children.Add(_statusText);

        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_titleText, 0);
        Grid.SetColumn(statusStack, 1);
        topRow.Children.Add(_titleText);
        topRow.Children.Add(statusStack);

        _balanceText = new TextBlock { Text = "—", FontWeight = FontWeights.Bold };
        _currencyText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 3)
        };
        var balanceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0)
        };
        balanceRow.Children.Add(_balanceText);
        balanceRow.Children.Add(_currencyText);

        _riskRing = new MonitorRiskRing { VerticalAlignment = VerticalAlignment.Center };
        _riskStateText = new TextBlock { Text = "等待数据…", FontWeight = FontWeights.SemiBold };
        _thresholdText = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        var riskPanel = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        riskPanel.Children.Add(_riskStateText);
        riskPanel.Children.Add(_thresholdText);
        var riskRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        riskRow.Children.Add(_riskRing);
        riskRow.Children.Add(riskPanel);

        _usageChartLabel = new TextBlock
        {
            Text = "用量趋势（近 7 天）",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4)
        };
        _usageChart = new UsageChart
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _updateTimeText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        stack.Children.Add(topRow);
        stack.Children.Add(new TextBlock { Text = "可用余额", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(balanceRow);
        stack.Children.Add(riskRow);
        stack.Children.Add(_usageChartLabel);
        stack.Children.Add(_usageChart);
        stack.Children.Add(_updateTimeText);

        _viewRoot = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };

        ApplyMonitorTheme(theme);
    }

    /// <summary>
    /// 应用主题：面板全部文字使用主题字体族，字号按 FontScale 缩放（v3.1 用 AppTypography.Scale 同理）。
    /// </summary>
    private void ApplyMonitorTheme(PaperBodyTheme theme)
    {
        var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
        var font = new FontFamily(theme.FontFamily);
        var text = ToBrush(theme.TextColor, "#202020");
        var weak = ToBrush(theme.WeakTextColor, "#707070");

        StyleText(_titleText, font, 20 * scale, FontWeights.SemiBold, text);
        StyleText(_statusText, font, 13 * scale, FontWeights.Normal, weak);
        StyleText(_balanceText, font, 32 * scale, FontWeights.Bold, text);
        StyleText(_currencyText, font, 15 * scale, FontWeights.Normal, weak);
        StyleText(_riskStateText, font, 15 * scale, FontWeights.SemiBold, text);
        StyleText(_thresholdText, font, 12 * scale, FontWeights.Normal, weak);
        StyleText(_usageChartLabel, font, 14 * scale, FontWeights.SemiBold, weak);
        StyleText(_updateTimeText, font, 12 * scale, FontWeights.Normal, weak);

        _riskRing.FontFamily = font;
        _riskRing.FontScale = scale;
        _riskRing.InvalidateVisual();

        _usageChart.FontFamily = font;
        _usageChart.FontScale = scale;
        _usageChart.WeakBrush = weak;
        _usageChart.InvalidateVisual();
    }

    private static void StyleText(
        TextBlock? block,
        FontFamily font,
        double size,
        FontWeight weight,
        Brush foreground)
    {
        if (block == null)
        {
            return;
        }
        block.FontFamily = font;
        block.FontSize = size;
        block.FontWeight = weight;
        block.Foreground = foreground;
    }

    /// <summary>
    /// 用最新快照刷新面板：状态点、余额、风险环、风险文字、阈值、更新时间。
    /// </summary>
    private void RefreshMonitorView()
    {
        if (_viewRoot == null)
        {
            return;
        }
        var status = _snapshot.StatusText ?? "";
        var hasData = _snapshot.HasRemaining && !double.IsNaN(_snapshot.Remaining);
        var ratio = ComputeRiskRatio(_snapshot.Remaining, _settings.BalanceThreshold);
        var riskColor = RiskColor(ratio);

        // 状态点：在线（绿）/ 请求错误（红）/ 未配置（灰）
        if (string.IsNullOrWhiteSpace(status))
        {
            _statusDot.Fill = GreenBrush;
            _statusText.Text = "在线";
            _statusText.Foreground = GreenBrush;
        }
        else if (status.StartsWith("错误：", StringComparison.Ordinal))
        {
            _statusDot.Fill = RedBrush;
            _statusText.Text = status;
            _statusText.Foreground = RedBrush;
        }
        else
        {
            _statusDot.Fill = GrayBrush;
            _statusText.Text = status;
            _statusText.Foreground = GrayBrush;
        }

        // 余额 + 币种
        if (hasData)
        {
            _balanceText.Text = FormatAmount(_snapshot.Remaining);
            _currencyText.Text =
                MapCurrencySymbolToCode(_settings.CurrencySymbol) ?? _settings.CurrencySymbol;
        }
        else
        {
            _balanceText.Text = "—";
            _currencyText.Text = _settings.CurrencySymbol;
        }

        // 风险环 + 状态文字 + 阈值
        _riskRing.Update(ratio, riskColor);
        _riskStateText.Text = RiskStateText(ratio);
        _riskStateText.Foreground = new SolidColorBrush(riskColor);
        _thresholdText.Text = _settings.BalanceThreshold > 0
            ? $"提醒阈值 {_settings.CurrencySymbol}{FormatAmount(_settings.BalanceThreshold)}"
            : "未设置提醒阈值，可在设置页配置";

        _updateTimeText.Text = hasData
            ? "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : "";

        UpdateUsageChart();
    }

    /// <summary>
    /// 用最近 7 天（含今天）的每日用量刷新柱状图。无数据时清空图表。
    /// </summary>
    private void UpdateUsageChart()
    {
        if (_usageChart == null)
        {
            return;
        }
        if (_usageDays == null || _usageDays.Length == 0)
        {
            _usageChart.Update(Array.Empty<double>());
            return;
        }

        var now = DateTime.Now;
        var start = now.AddDays(-6).Date;
        var values = new List<double>();
        var labels = new List<string>();
        for (var d = start; d <= now.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = Array.Find(_usageDays, u => u.Date == key);
            values.Add(day?.Tokens ?? 0);
            labels.Add(d.Day.ToString(CultureInfo.InvariantCulture));
        }
        _usageChart.Update(values.ToArray(), labels.ToArray());
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

    private static SolidColorBrush ToBrush(string? value, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
            }
        }
        catch
        {
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)!);
    }

    /// <summary>
    /// 监视面板的大号风险环：72px 环 + 中心风险百分比（参考 v3.1 DeepSeekPaymentRiskWidget）。
    /// </summary>
    private sealed class MonitorRiskRing : FrameworkElement
    {
        private const double Size = 72;
        private const double StrokeW = 5;
        private const double Radius = (Size - StrokeW) / 2;
        private const double Center = Size / 2;
        private static readonly Color GrayColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

        public FontFamily FontFamily { get; set; } = new FontFamily("Microsoft YaHei UI");
        public double FontScale { get; set; } = 1.0;

        private double _ratio;
        private Color _foreColor = GrayColor;

        public MonitorRiskRing()
        {
            Width = Size;
            Height = Size;
        }

        protected override Size MeasureOverride(Size constraint) => new Size(Size, Size);

        public void Update(double ratio, Color color)
        {
            _ratio = ratio;
            _foreColor = color;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 背景轨道
            var trackPen = new Pen(new SolidColorBrush(GrayColor) { Opacity = 0.3 }, StrokeW);
            dc.DrawEllipse(null, trackPen, new Point(Center, Center), Radius, Radius);

            if (_ratio > 0)
            {
                var deg = Math.Max(_ratio * 360, 5);
                if (deg >= 360)
                {
                    // 满圆
                    dc.DrawEllipse(
                        null,
                        new Pen(new SolidColorBrush(_foreColor), StrokeW),
                        new Point(Center, Center),
                        Radius,
                        Radius);
                }
                else
                {
                    // 弧：12 点方向起顺时针
                    var rad = deg * Math.PI / 180;
                    var sa = -Math.PI / 2;
                    var sx = Center + Radius * Math.Cos(sa);
                    var sy = Center + Radius * Math.Sin(sa);
                    var ex = Center + Radius * Math.Cos(sa + rad);
                    var ey = Center + Radius * Math.Sin(sa + rad);
                    var seg = new ArcSegment(
                        new Point(ex, ey),
                        new Size(Radius, Radius),
                        0,
                        deg > 180,
                        SweepDirection.Clockwise,
                        true);
                    var fig = new PathFigure(new Point(sx, sy), [seg], closed: false);
                    var geo = new PathGeometry([fig]);
                    var pen = new Pen(new SolidColorBrush(_foreColor), StrokeW)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    };
                    dc.DrawGeometry(null, pen, geo);
                }
            }

            // 中心百分比
            var pct = _ratio > 0 ? $"{(int)(_ratio * 100)}%" : "--";
            var ft = new FormattedText(
                pct,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    FontFamily,
                    FontStyles.Normal,
                    FontWeights.Bold,
                    FontStretches.Normal),
                14 * FontScale,
                new SolidColorBrush(_foreColor),
                96.0);
            dc.DrawText(ft, new Point(Center - ft.Width / 2, Center - ft.Height / 2));
        }
    }

    /// <summary>
    /// 简约趋势柱状图（v3.1 DeepSeekUsageChart 移植）：坐标轴 + 绿柱 + 悬停 ToolTip。
    /// 主题字体 / 弱色 / 字号缩放由 ApplyMonitorTheme 注入，柱子布局按当前宽度实时计算。
    /// </summary>
    private sealed class UsageChart : FrameworkElement
    {
        private const double BarWidth = 14;
        private const double BarGap = 20;
        private const double ChartArea = 60;
        private const double AxisWidth = 42;
        private const double AxisHeight = 18;
        private const double TotalHeight = ChartArea + AxisHeight + 4;

        private static readonly Color BarColor = Color.FromRgb(0x4C, 0xAF, 0x50);
        private static readonly Color HoverColor = Color.FromRgb(0x66, 0xBB, 0x6A);
        private static readonly Color AxisColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

        public FontFamily FontFamily { get; set; } = new FontFamily("Microsoft YaHei UI");
        public double FontScale { get; set; } = 1.0;
        public Brush WeakBrush { get; set; } = Brushes.Gray;

        private double[] _values = Array.Empty<double>();
        private string[] _labels = Array.Empty<string>();
        private double _maxVal = 1;
        private int _hoverIndex = -1;

        public UsageChart()
        {
            Height = TotalHeight;
            ToolTipService.SetInitialShowDelay(this, 0);
            ToolTipService.SetBetweenShowDelay(this, 0);
            ToolTipService.SetShowDuration(this, 60000);
        }

        public void Update(double[] dailyTokens, string[]? dateLabels = null)
        {
            _values = dailyTokens ?? Array.Empty<double>();
            _labels = dateLabels ?? Array.Empty<string>();
            _maxVal = _values.Length == 0 ? 1 : Math.Max(_values.Max(), 1);
            _hoverIndex = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            if (w <= 0 || _values.Length == 0)
            {
                return;
            }

            var bottom = ChartArea;
            var axisPen = new Pen(new SolidColorBrush(AxisColor) { Opacity = 0.25 }, 1);
            var axisFontSize = 11 * FontScale;
            var axisX = AxisWidth - 4;

            // Y 轴 / X 轴
            dc.DrawLine(axisPen, new Point(axisX, 0), new Point(axisX, bottom));
            dc.DrawLine(axisPen, new Point(axisX, bottom), new Point(w - 2, bottom));

            // Y 轴刻度：0 / 50% / 100%
            foreach (var tick in new[] { 0.0, _maxVal * 0.5, _maxVal })
            {
                var y = bottom - tick / _maxVal * ChartArea;
                dc.DrawLine(axisPen, new Point(axisX - 4, y), new Point(axisX, y));
                var ft = new FormattedText(
                    FormatTokens((long)tick),
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    axisFontSize,
                    WeakBrush,
                    96);
                dc.DrawText(ft, new Point(axisX - 8 - ft.Width, y - ft.Height / 2));
            }

            // 柱子 + X 轴日期标签
            var rects = ComputeBars();
            for (var i = 0; i < rects.Length; i++)
            {
                var rect = rects[i];
                var color = i == _hoverIndex ? HoverColor : BarColor;
                var opacity = i == _hoverIndex ? 0.9 : 0.65;
                var brush = new SolidColorBrush(color) { Opacity = opacity };
                var geo = new RectangleGeometry(rect, BarWidth / 2, BarWidth / 2);
                dc.DrawGeometry(brush, null, geo);

                if (i < _labels.Length)
                {
                    var ft = new FormattedText(
                        _labels[i],
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                        axisFontSize,
                        WeakBrush,
                        96);
                    dc.DrawText(ft, new Point(rect.X + BarWidth / 2 - ft.Width / 2, bottom + 4));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pos = e.GetPosition(this);
            var rects = ComputeBars();
            var found = -1;
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].Contains(pos))
                {
                    found = i;
                    break;
                }
            }
            if (found != _hoverIndex)
            {
                _hoverIndex = found;
                ToolTip = found >= 0 && found < _labels.Length
                    ? $"{_labels[found]}\n{FormatTokens((long)_values[found])} Token"
                    : "";
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex >= 0)
            {
                _hoverIndex = -1;
                ToolTip = "";
                InvalidateVisual();
            }
        }

        /// <summary>
        /// 按当前实际宽度计算柱子矩形（渲染与悬停共用，布局变化后自动校正）。
        /// </summary>
        private Rect[] ComputeBars()
        {
            if (_values.Length == 0 || ActualWidth <= 0)
            {
                return Array.Empty<Rect>();
            }
            var bottom = ChartArea;
            var areaWidth = Math.Max(ActualWidth - AxisWidth, 10);
            var totalBarArea = _values.Length * (BarWidth + BarGap) - BarGap;
            var startX = AxisWidth + (areaWidth - totalBarArea) / 2;
            var rects = new Rect[_values.Length];
            for (var i = 0; i < _values.Length; i++)
            {
                var h = Math.Max(_values[i] / _maxVal * ChartArea, 2);
                var x = startX + i * (BarWidth + BarGap);
                rects[i] = new Rect(x, bottom - h, BarWidth, h);
            }
            return rects;
        }

        private static string FormatTokens(long tokens)
        {
            if (tokens >= 1_000_000_000)
            {
                return $"{tokens / 1_000_000_000.0:F2}B";
            }
            if (tokens >= 1_000_000)
            {
                return $"{tokens / 1_000_000.0:F1}M";
            }
            if (tokens >= 1_000)
            {
                return $"{tokens / 1_000.0:F1}K";
            }
            return tokens.ToString();
        }
    }
}
