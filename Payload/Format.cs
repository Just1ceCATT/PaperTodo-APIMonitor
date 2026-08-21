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

    /// <summary>万/亿简写（"12345" → "1.2万"），负数/NaN 返回 "—"。</summary>
    public static string FormatWanYi(double n)
    {
        if (!double.IsFinite(n) || n < 0)
        {
            return NaNPlaceholder;
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

    /// <summary>缓存命中率 0..1 → "50.10%"；null/NaN 返回 null。</summary>
    public static string? FormatCacheRate(double? rate)
    {
        if (!rate.HasValue || !double.IsFinite(rate.Value))
        {
            return null;
        }
        return (rate.Value * 100).ToString("0.00", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>Color → "#RRGGBB"。</summary>
    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>确保 hex 字符串以 "#" 前缀，缺失时回退到 fallback。</summary>
    public static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return value.StartsWith("#") ? value : "#" + value;
    }
}