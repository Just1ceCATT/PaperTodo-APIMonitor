using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// v3.1 风险档位阈值（ClassifyRisk 与 RiskColor / RingColorHex 共用）。
/// 阈值与色板绑定在同一个类，避免改阈值漏改色或反之。
/// </summary>
internal static class RiskClassifier
{
    public const double WarmingRatio = 0.5;
    public const double DangerRatio = 0.8;
    public const double OverrunRatio = 1.0;

    private const string HexOverrun = "#F44336";
    private const string HexDanger  = "#FF9800";
    private const string HexWarming = "#FFC107";
    private const string HexSafe    = "#4CAF50";
    private const string HexGray    = "#9E9E9E";

    public static readonly Color ColorOverrun = Color.FromRgb(0xF4, 0x43, 0x36);
    public static readonly Color ColorDanger  = Color.FromRgb(0xFF, 0x98, 0x00);
    public static readonly Color ColorWarming = Color.FromRgb(0xFF, 0xC1, 0x07);
    public static readonly Color ColorSafe    = Color.FromRgb(0x4C, 0xAF, 0x50);
    public static readonly Color ColorGray    = Color.FromRgb(0x9E, 0x9E, 0x9E);

    public enum State { Safe, Warming, Danger, Overrun }

    /// <summary>v3.1 风险档位：threshold/balance >=1 Overrun，>=0.8 Danger，>=0.5 Warming，否则 Safe。</summary>
    public static State Classify(double ratio)
    {
        if (ratio >= OverrunRatio) return State.Overrun;
        if (ratio >= DangerRatio)  return State.Danger;
        if (ratio >= WarmingRatio) return State.Warming;
        return State.Safe;
    }

    /// <summary>v3.1 颜色 hex（给 1.6 胶囊 ring）：Safe 绿 / Warming 黄 / Danger 橙 / Overrun 红。</summary>
    public static string RingColorHex(double ratio) => Classify(ratio) switch
    {
        State.Overrun     => HexOverrun,
        State.Danger      => HexDanger,
        State.Warming     => HexWarming,
        State.Safe        => HexSafe,
        _                 => HexGray
    };

    /// <summary>
    /// 宿主 ProgressRing 的 Value：Overrun 时传 1（满弧），其余钳到 [0,1]。
    /// </summary>
    public static double RingArcValue(double ratio)
    {
        if (ratio >= 1.0) return 1.0;
        return Math.Clamp(ratio, 0, 1);
    }

    /// <summary>v3.1 风险环颜色（含过渡渐变）：绿 → 黄 → 橙 → 红；未未配置阈值时灰。</summary>
    public static Color RiskColor(double ratio)
    {
        if (ratio <= 0)
        {
            return ColorGray;
        }
        if (ratio >= OverrunRatio)
        {
            return ColorOverrun;
        }
        if (ratio >= DangerRatio)
        {
            return Lerp(ColorWarming, ColorDanger, (ratio - DangerRatio) / 0.2);
        }
        if (ratio >= WarmingRatio)
        {
            return Lerp(ColorSafe, ColorWarming, (ratio - WarmingRatio) / 0.3);
        }
        return ColorSafe;
    }

    public static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    /// <summary>NaN/±Infinity 归一为 0，避免 JsonSerializer 序列化时抛异常。</summary>
    public static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>Finite 的可空版本：非有限值返回 null。</summary>
    public static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    /// <summary>
    /// 风险比例 v3.1 语义：threshold / balance。
    /// balance &lt;= 0 或 threshold &lt;= 0 → 视为未配置，返回 0。
    /// </summary>
    public static double ComputeRiskRatio(double balance, double threshold)
    {
        if (threshold <= 0 || balance <= 0)
        {
            return 0;
        }
        return threshold / balance;
    }
}

/// <summary>
/// 胶囊显示快照（不可变 record）：消除 BalanceSession._capsuleText/_capsuleRingColorHex/_capsuleRingArc
/// 共享字段的隐式时序契约。CreateCapsuleView 从 _latestCapsuleSnapshot 读取，无时序耦合。
/// 初值 CapsuleSnapshot.Empty 保证首屏胶囊始终有内容，不会空白。
/// </summary>
internal sealed record CapsuleSnapshot(string Text, string RingColorHex, double RingArc)
{
    public static readonly CapsuleSnapshot Empty = new("—", "#9E9E9E", 0);
}