using System.Globalization;
using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Payload;

/// <summary>
/// 纯静态格式化函数集合：数字 / 百分比 / 时长 / 缓存率 / hex 与 fallback hex。
/// 所有方法 pure，无外部依赖。FormatRemaining 与 FormatPercent 仍留在 BalanceMiniView，
/// 因为它们只被 MiniView 内部使用，不进入 WebView2 payload。
/// </summary>
internal static class Format
{
    private static string NaNPlaceholder = "—";

    /// <summary>
    /// v3.1 风格金额格式化：整数省小数位，否则保留 2 位小数；NaN/Infinity 输出 "—"。
    /// 不被 Payload Builder 复用（后者用 "0.00"），避免格式漂移。
    /// </summary>
    public static string FormatAmount(double amount)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount))
        {
            return NaNPlaceholder;
        }
        var asDecimal = (decimal)amount;
        return asDecimal % 1m == 0
            ? asDecimal.ToString("F0", CultureInfo.CurrentCulture)
            : asDecimal.ToString("F2", CultureInfo.CurrentCulture);
    }

    /// <summary>千分位逗号分隔（1000 → "1,000"），负数返回 "0"。</summary>
    public static string FormatThousands(double n)
    {
        if (!double.IsFinite(n) || n < 0)
        {
            return "0";
        }
        var rounded = (long)Math.Round(n);
        return rounded.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>整数 tokens 格式化：&lt;=0 显示 "—"，否则</summary>千分位。</summary>
    public static string FormatTokens(double n)
    {
        if (!double.IsFinite(n) || n <= 0)
        {
            return NaNPlaceholder;
        }
        return FormatThousands(n);
    }

    /// <summary>
    /// Tokens 换算为中文单位近似值(2 位小数):
    ///   - < 1亿: "≈X.XX万"(如 25,088 → "≈2.51万"; 5,000 → "≈0.50万")
    ///   - ≥ 1亿: "≈X.XX亿"(如 123,456,789 → "≈1.23亿")
    ///   - 无效/空/≤0: 空串(让调用方决定是否隐藏整段)
    /// </summary>
    public static string FormatEstimate(string tokensText)
    {
        if (string.IsNullOrEmpty(tokensText) || tokensText == NaNPlaceholder)
        {
            return "";
        }
        // 去掉千分位逗号后解析
        var cleaned = tokensText.Replace(",", "").Replace(" ", "").Trim();
        if (!double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var tokens) ||
            tokens <= 0 || !double.IsFinite(tokens))
        {
            return "";
        }
        const double yi = 100_000_000.0;  // 1 亿
        const double wan = 10_000.0;      // 1 万
        if (tokens >= yi)
        {
            return "≈" + (tokens / yi).ToString("F2", CultureInfo.CurrentCulture) + "亿";
        }
        return "≈" + (tokens / wan).ToString("F2", CultureInfo.CurrentCulture) + "万";
    }

    /// <summary>缓存命中率 0..1 → "50.10%"；null/NaN 返回 null。</summary>
    public static string? FormatCacheRate(double? rate)
    {
        if (!rate.HasValue || !double.IsFinite(rate.Value))
        {
            return null;
        }
        return (rate.Value * 100).ToString("0.00", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>确保 hex 字符串以 "#" 前缀，缺失时回退到 fallback。</summary>
    public static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return value.StartsWith("#") ? value : "#" + value;
    }

    /// <summary>
    /// hex 字符串 → 冻结的 SolidColorBrush。解析失败回退到 fallback，再失败则用 Colors.Gray。
    /// 冻结让 WPF 渲染走快路径并允许跨线程共享，供所有 view 复用。
    /// </summary>
    public static SolidColorBrush ToFrozenBrush(string value, string fallback)
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
        brush.Freeze();
        return brush;
    }
}