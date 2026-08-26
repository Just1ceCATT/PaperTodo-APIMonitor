using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// v3.1 风险档位阈值（Classify 与 RingColorHex 共用）。
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

    /// <summary>NaN/±Infinity 归一为 0，避免 JsonSerializer 序列化时抛异常。</summary>
    public static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>Finite 的可空版本：非有限值返回 null。</summary>
    public static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;
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