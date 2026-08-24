using System.Globalization;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using PaperTodo.Plugin.ApiBalanceMonitor.Session;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Payload;

/// <summary>
/// 组装推给 HTML 监视面板的 JSON payload（{ theme, data }）。
/// 通过 BalanceSession 的 internal getter 读取所需字段；依赖方向 Session → Payload。
/// </summary>
internal sealed class ViewPayloadBuilder
{
    private readonly BalanceSession _session;

    public ViewPayloadBuilder(BalanceSession session)
    {
        _session = session;
    }

    /// <summary>
    /// 组装推给 HTML 的 JSON：{ theme, data }。
    /// 字段顺序与值与原 BalanceSession.BuildViewPayload 1:1 一致，字典初始化顺序决定 JSON 顺序。
    /// web/monitor.html 与 web/minimax.html 通过 `window.__renderBalance(data)` 消费。
    /// </summary>
    public string Build()
    {
        var snapshot = _session.LatestSnapshot;
        var settings = _session.CurrentSettings;
        var state = _session.CurrentState;
        var theme = _session.CurrentTheme;
        var costDays = _session.CostDays;
        var usageDays = _session.UsageDays;
        var costTodayByModel = _session.CostTodayByModel;
        var minimaxModelRemains = _session.MiniMaxModelRemains;

        var status = snapshot.StatusText ?? "";
        var hasData = snapshot.HasRemaining && !double.IsNaN(snapshot.Remaining);
        var isMiniMax = _session.IsMiniMax;
        // 余额趋势：DeepSeek 有效；MiniMax 不算（趋势是按金额口径的），给空键值让前端识别。
        var trend = _session.CurrentTrend;

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

        var themePayload = new Dictionary<string, object?>
        {
            ["dark"] = theme.IsDark,
            ["text"] = Format.NormalizeHex(theme.TextColor, "#202020"),
            ["weak"] = Format.NormalizeHex(theme.WeakTextColor, "#707070"),
            ["accent"] = Format.NormalizeHex(theme.AccentColor, "#B07A31"),
            ["paper"] = Format.NormalizeHex(theme.PaperColor, "#FFF8E6")
        };

        var data = new Dictionary<string, object?>
        {
            ["provider"] = state.Provider,
            ["status"] = status,
            ["statusKind"] = statusKind,
            ["hasBalance"] = hasData,
            ["balance"] = hasData ? Format.FormatAmount(snapshot.Remaining) : "—",
            ["currency"] = isMiniMax ? "小时" : (ColorPalette.MapCurrencySymbolToCode(settings.CurrencySymbol) ?? settings.CurrencySymbol),
            ["currencySymbol"] = isMiniMax ? "" : settings.CurrencySymbol,
            ["credentialLevel"] = _session.CredentialLevel ?? "",
            ["updateTime"] = hasData
                ? "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "",
            ["costToday"] = MetricsAggregator.BuildCostTodayText(costDays, settings.CurrencySymbol),
            ["costTodayFoot"] = MetricsAggregator.BuildCostTodayFoot(costDays),
            ["costTodayModels"] = MetricsAggregator.BuildCostTodayByModels(costTodayByModel, settings.CurrencySymbol),
            ["cost7d"] = MetricsAggregator.BuildCost7dText(costDays, settings.CurrencySymbol),
            ["cost7dFoot"] = MetricsAggregator.BuildCost7dFoot(costDays, settings.CurrencySymbol),
            ["todayTokens"] = RiskClassifier.Finite(MetricsAggregator.BuildTodayTokens(usageDays)),
            ["todayHit"] = RiskClassifier.Finite(MetricsAggregator.BuildTodayHit(usageDays)),
            ["cacheRate"] = RiskClassifier.FiniteOrNull(MetricsAggregator.BuildTodayCacheRate(usageDays)),
            ["modelRemains"] = MetricsAggregator.BuildMiniMaxModelRemains(minimaxModelRemains),
            ["usage"] = MetricsAggregator.BuildUsageArray(usageDays),
            // 余额趋势：30 分钟滑动窗口分析结果。前端可按 trendLevel 决定是否渲染。
            // MiniMax / OpenCode 走"unknown"占位，前端识别后不显示。
            ["trendLevel"] = TrendAnalyzer.LevelKey(trend.Level),
            ["trendArrow"] = TrendAnalyzer.Arrow(trend.Level),
            ["trendRatePerHour"] = RiskClassifier.Finite(trend.RatePerHour),
            ["trendRelative"] = RiskClassifier.Finite(trend.RelativeRatePerHour),
            ["trendSamples"] = trend.SampleCount,
            ["trendDepletionHours"] = RiskClassifier.FiniteOrNull(trend.DepletionHours)
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["theme"] = themePayload,
            ["data"] = data
        });
    }
}