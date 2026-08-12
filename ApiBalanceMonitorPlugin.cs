using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ApiBalanceMonitor;

/// <summary>
/// 余额监测插件：拉取 DeepSeek /user/balance 接口，
/// 在胶囊中显示「绿/黄/红圆环 + 货币 + 余额 + 可选百分比」。
///
/// 实现要点（不修改宿主）：
/// - 1.7 协议胶囊（PaperCapsulePresentation）即可渲染圆环形态
///   （ProgressRing + Text），外壳底色由暗色主题自然提供。
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

internal sealed class BalanceSession : IPaperBodySession, IPaperCapsuleViewProvider
{
    private readonly PaperBodyContext _context;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private BalanceSettings _settings;
    private BalanceSnapshot _snapshot = BalanceSnapshot.Empty("尚未拉取");
    private string _lastCapsuleSignature = "";
    private BalanceCapsuleView? _regularCapsuleView;
    private BalanceCapsuleView? _dockedCapsuleView;
    private int _polling;

    public BalanceSession(PaperBodyContext context)
    {
        _context = context;
        _settings = ReadSettings(context.SettingsJson);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PaperTodo.Plugin.ApiBalanceMonitor/1.0");

        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += async (_, _) => await PollAsync();

        ApplySettings(_settings);
        // 构造时不主动拉取，等 timer 首次触发，避免阻塞宿主启动。
    }

    public FrameworkElement View { get; } = new TextBlock
    {
        // 该插件不提供完整 PaperBody 正文，最小可视元素即可；
        // 用户主要与胶囊与设置面板交互。
        Text = "API 余额监测：胶囊显示当前余额，设置页可调整 API Key / 刷新间隔 / 提醒阈值。",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16),
        Opacity = 0.65
    };

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
        _regularCapsuleView?.ApplyTheme(theme);
        _dockedCapsuleView?.ApplyTheme(theme);
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);
    public void OnDpiChanged() { }

    public void OnSettingsChanged(string settingsJson)
    {
        ApplySettings(ReadSettings(settingsJson));
    }

    // ---------------- 1.7 协议胶囊视图 ----------------

    /// <summary>
    /// Protocol 1.7：宿主为每个 live capsule surface 至多调用一次并缓存视图。
    /// 视图拿到精确的内容段宽度（不含 1.6 模板视觉内边距），由插件自绘圆环与文本，
    /// 文本不再受宿主 CharacterEllipsis 截断——胶囊窗口宽度会随内容自适应（最大 320）。
    /// </summary>
    public FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context)
    {
        var view = new BalanceCapsuleView(context);
        var ratio = ComputeRiskRatio(_snapshot.Remaining, _settings.BalanceThreshold);
        view.Update(
            BuildCapsuleText(_snapshot, _settings, ratio),
            ratio,
            RingColor(ratio));
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

    // ---------------- 设置解析 ----------------

    private static BalanceSettings ReadSettings(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            return new BalanceSettings(
                ReadString(root, "apiKey", ""),
                ReadInt(root, "pollSeconds", 60),
                ReadString(root, "currencySymbol", "¥"),
                ReadDouble(root, "balanceThreshold", 20.0),
                ReadBool(root, "showPercentage", true));
        }
        catch
        {
            return new BalanceSettings(
                "", 60, "¥", 20.0, true);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekBalanceUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            using var response = await _http.SendAsync(request).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            UpdateSnapshot(ParseResponse(body));
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
        if (string.Equals(signature, _lastCapsuleSignature, StringComparison.Ordinal))
        {
            return;
        }
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

        // 1.7 自定义视图（若宿主已创建并缓存）同步当前文本与风险环。
        _regularCapsuleView?.Update(text, riskRatio, ringColor);
        _dockedCapsuleView?.Update(text, riskRatio, ringColor);
    }

    /// <summary>
    /// 胶囊文本：货币符号 + 余额 +（可选）百分比，v3.1 风格。
    /// 1.7 视图下胶囊宽度随内容自适应，文本不再被宿主 CharacterEllipsis 截断，
    /// 因此保留一个普通空格分隔即可。
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
            sb.Append(' ');
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

    // ---------------- 1.7 胶囊自定义视图 ----------------

    /// <summary>
    /// 1.7 协议胶囊视图：横向 [风险圆环][文本]，整体占满宿主给的精确内容宽度。
    /// 文本由插件自绘、字号跟随主题，不再经过宿主 1.6 模板的 CharacterEllipsis 截断；
    /// 若内容超宽（极端情况）仍以省略号兜底，同时胶囊窗口宽度会随内容自适应变宽。
    /// </summary>
    private sealed class BalanceCapsuleView : Grid
    {
        private readonly RiskRingElement _ring;
        private readonly TextBlock _label;
        private readonly PaperCapsuleSurfaceKind _surface;

        public BalanceCapsuleView(PaperCapsuleViewContext context)
        {
            _surface = context.Surface;
            Background = Brushes.Transparent;
            ClipToBounds = true;

            _ring = new RiskRingElement
            {
                Margin = new Thickness(6, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _label = new TextBlock
            {
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold
            };

            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_ring, 0);
            Grid.SetColumn(_label, 1);
            Children.Add(_ring);
            Children.Add(_label);

            ApplyTheme(context.Theme);
        }

        public void Update(string text, double ratio, string ringColor)
        {
            _label.Text = text;
            _ring.SetState(ratio, ringColor);
        }

        public void ApplyTheme(PaperBodyTheme theme)
        {
            var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
            _label.FontFamily = new FontFamily(theme.FontFamily);
            _label.FontSize =
                (_surface == PaperCapsuleSurfaceKind.Docked ? 11.5 : 12) * scale;
            _label.Foreground = ToBrush(theme.TextColor, "#202020");
        }

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
    }

    /// <summary>
    /// 风险圆环（v3.1 实现移植）：浅色轨道 + 彩色前景弧，自 12 点起顺时针，
    /// 未配置阈值显示灰环，Overrun 显示满圆，最小可见弧 10°。
    /// </summary>
    private sealed class RiskRingElement : FrameworkElement
    {
        private const double RingSize = 16;
        private const double StrokeThicknessValue = 2.5;
        private const double Radius = (RingSize - StrokeThicknessValue) / 2;
        private const double MinArcDegrees = 10;
        private const double Center = RingSize / 2;

        private static readonly Color GrayColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

        private double _ratio;
        private Color _foreColor = GrayColor;

        public RiskRingElement()
        {
            Width = RingSize;
            Height = RingSize;
        }

        protected override Size MeasureOverride(Size constraint) =>
            new Size(RingSize, RingSize);

        public void SetState(double ratio, string color)
        {
            _ratio = ratio;
            _foreColor = ParseColor(color, "#9E9E9E");
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 背景灰色轨道。
            var trackPen = new Pen(
                new SolidColorBrush(GrayColor) { Opacity = 0.35 },
                StrokeThicknessValue);
            dc.DrawEllipse(null, trackPen, new Point(Center, Center), Radius, Radius);

            // 未配置阈值：只画灰环，不画前景弧。
            if (_ratio <= 0)
            {
                return;
            }

            if (_ratio >= 1.0)
            {
                // Overrun / 满圆。
                var fullPen = new Pen(new SolidColorBrush(_foreColor), StrokeThicknessValue);
                dc.DrawEllipse(null, fullPen, new Point(Center, Center), Radius, Radius);
                return;
            }

            // 弧形：12 点钟方向起，顺时针。
            var degrees = Math.Max(_ratio * 360, MinArcDegrees);
            var radians = degrees * Math.PI / 180;
            var startAngle = -Math.PI / 2;
            var start = new Point(
                Center + Radius * Math.Cos(startAngle),
                Center + Radius * Math.Sin(startAngle));
            var end = new Point(
                Center + Radius * Math.Cos(startAngle + radians),
                Center + Radius * Math.Sin(startAngle + radians));
            var isLargeArc = degrees > 180;

            var segment = new ArcSegment(
                end,
                new Size(Radius, Radius),
                0,
                isLargeArc,
                SweepDirection.Clockwise,
                true);
            var figure = new PathFigure(start, [segment], closed: false);
            var geometry = new PathGeometry([figure]);
            var pen = new Pen(new SolidColorBrush(_foreColor), StrokeThicknessValue)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, pen, geometry);
        }

        private static Color ParseColor(string? value, string fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return (Color)ColorConverter.ConvertFromString(value)!;
                }
            }
            catch
            {
            }
            return (Color)ColorConverter.ConvertFromString(fallback)!;
        }
    }
}
