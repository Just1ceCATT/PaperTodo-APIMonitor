using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// 余额趋势等级。阈值语义见 <see cref="TrendAnalyzer"/> 的常量，UI 侧只读枚举不复算。
/// </summary>
internal enum TrendLevel
{
    /// <summary>样本不足，无法分析。</summary>
    Unknown,
    /// <summary>样本跨度不足最小分析时长，正在采集。</summary>
    Collecting,
    /// <summary>余额上升（充值 / 赠额恢复）。</summary>
    Increasing,
    /// <summary>基本持平。</summary>
    Stable,
    /// <summary>缓慢消耗。</summary>
    SlowConsumption,
    /// <summary>中速消耗。</summary>
    MediumConsumption,
    /// <summary>快速消耗。</summary>
    FastConsumption
}

/// <summary>
/// 一次趋势分析的结果。全部为已过有限性检查的值，可直接序列化 / 显示。
/// </summary>
internal readonly record struct BalanceTrend(
    TrendLevel Level,
    double RatePerHour,             // ¥/hour，负数表示消耗
    double RelativeRatePerHour,     // 相对窗口起始余额的比例，负数表示消耗
    double ConsumptionRatePerHour,  // max(0, -RelativeRatePerHour)，UI / 分级用
    double? DepletionHours,         // 预计耗尽小时数，无法估算时 null
    double WindowStartBalance,      // 窗口第一条样本的余额，0 表示无数据
    int SampleCount)
{
    /// <summary>无数据占位：样本不足时的统一返回值。</summary>
    public static BalanceTrend None(int sampleCount) =>
        new(TrendLevel.Unknown, 0, 0, 0, null, 0, sampleCount);
}

/// <summary>
/// 30 分钟滑动窗口余额趋势分析器。
/// 只吃成功拉取的余额样本（失败/超时/解析失败不得喂入），对窗口内样本做最小二乘线性回归，
/// 得到 ¥/hour 消耗速度，再除以窗口起始余额得到相对消耗率并映射为趋势等级。
///
/// 本文件刻意不引用 System.Windows.*：纯逻辑，可被独立测试工程链接编译。
/// 趋势阈值集中在这里，UI 代码不得再出现裸阈值。
/// </summary>
internal sealed class TrendAnalyzer
{
    /// <summary>滑动窗口长度（分钟）：窗口永远是 [now - 30min, now]。</summary>
    public const int WindowMinutes = 30;
    /// <summary>最小样本数：少于 2 条无法拟合斜率。</summary>
    public const int MinSamples = 2;
    /// <summary>最小分析跨度（分钟）：不足这个跨度只报 Collecting，避免抖动放大成"快速消耗"。</summary>
    public const int MinElapsedMinutes = 5;

    /// <summary>Stable 上界：相对消耗率 &lt;= 0.5%/hour 视为持平。</summary>
    public const double StableRatio = 0.005;
    /// <summary>SlowConsumption 上界：&lt;= 2%/hour。</summary>
    public const double SlowRatio = 0.02;
    /// <summary>MediumConsumption 上界：&lt;= 5%/hour，超出即 FastConsumption。</summary>
    public const double MediumRatio = 0.05;

    /// <summary>
    /// 余额增加判定容差（货币单位）。DeepSeek 余额本身是字符串两位小数，
    /// 用 0.01 兜住浮点噪声，避免把解析误差当成充值事件反复清窗口。
    /// </summary>
    public const double RechargeEpsilon = 0.01;

    // 队首最旧、队尾最新。样本量最多 30min/轮询间隔（默认 60s ≈ 30 条），List 足够。
    private readonly List<BalanceSample> _samples = new();

    /// <summary>当前窗口内的样本数（已裁剪过期数据的口径需先调用 Analyze / Add）。</summary>
    public int SampleCount => _samples.Count;

    /// <summary>
    /// 记录一条成功样本。调用方必须先确认余额有效——本方法不做失败态判断。
    /// 检测到余额明显增加（充值）时清空窗口，趋势窗口不得跨越充值事件。
    /// </summary>
    public void Add(DateTime now, double balance)
    {
        if (!double.IsFinite(balance))
        {
            return;
        }
        if (_samples.Count > 0 &&
            balance > _samples[^1].Balance + RechargeEpsilon)
        {
            // 余额增加：旧数据继续参与回归会把"充值"读成"消耗变慢"，直接从这条重新积累。
            _samples.Clear();
        }
        _samples.Add(new BalanceSample(now, balance));
        Trim(now);
    }

    /// <summary>切换供应商 / 重置会话时清空窗口。</summary>
    public void Reset() => _samples.Clear();

    /// <summary>
    /// 分析当前窗口。now 与 currentBalance 由调用方传入（便于测试，不依赖 DateTime.Now）。
    /// </summary>
    public BalanceTrend Analyze(DateTime now, double currentBalance)
    {
        Trim(now);
        if (_samples.Count < MinSamples)
        {
            return BalanceTrend.None(_samples.Count);
        }

        var first = _samples[0];
        var last = _samples[^1];
        if ((last.Timestamp - first.Timestamp).TotalMinutes < MinElapsedMinutes)
        {
            return new BalanceTrend(
                TrendLevel.Collecting, 0, 0, 0, null, first.Balance, _samples.Count);
        }
        if (first.Balance <= 0)
        {
            // 窗口起始余额为 0 时相对消耗率无意义（除零），不做分级。
            return BalanceTrend.None(_samples.Count);
        }

        var slope = ComputeSlopePerHour(first.Timestamp);
        if (!double.IsFinite(slope))
        {
            return BalanceTrend.None(_samples.Count);
        }

        var relative = slope / first.Balance;
        if (!double.IsFinite(relative))
        {
            return BalanceTrend.None(_samples.Count);
        }
        var consumption = Math.Max(0, -relative);
        var level = Classify(slope, consumption);

        // 预计耗尽时间：仅作辅助信息，消耗速度或余额非正时不给值。
        double? depletion = null;
        var consumptionPerHour = -slope;
        if (consumptionPerHour > 0 && currentBalance > 0)
        {
            var hours = currentBalance / consumptionPerHour;
            if (double.IsFinite(hours))
            {
                depletion = hours;
            }
        }

        return new BalanceTrend(
            level, slope, relative, consumption, depletion, first.Balance, _samples.Count);
    }

    /// <summary>
    /// 最小二乘斜率：x 取相对窗口首条样本的小时数，y 取余额，
    /// slope = Σ((x-x̄)(y-ȳ)) / Σ((x-x̄)²)。分母为 0（时间戳全相同）时返回 NaN。
    /// </summary>
    private double ComputeSlopePerHour(DateTime origin)
    {
        double sumX = 0, sumY = 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            sumX += (_samples[i].Timestamp - origin).TotalHours;
            sumY += _samples[i].Balance;
        }
        var meanX = sumX / _samples.Count;
        var meanY = sumY / _samples.Count;

        double numerator = 0, denominator = 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var dx = (_samples[i].Timestamp - origin).TotalHours - meanX;
            numerator += dx * (_samples[i].Balance - meanY);
            denominator += dx * dx;
        }
        return denominator == 0 ? double.NaN : numerator / denominator;
    }

    /// <summary>斜率非负即 Increasing；否则按相对消耗率落 Stable / Slow / Medium / Fast。</summary>
    private static TrendLevel Classify(double slope, double consumption)
    {
        if (slope >= 0)
        {
            return TrendLevel.Increasing;
        }
        if (consumption <= StableRatio) return TrendLevel.Stable;
        if (consumption <= SlowRatio)   return TrendLevel.SlowConsumption;
        if (consumption <= MediumRatio) return TrendLevel.MediumConsumption;
        return TrendLevel.FastConsumption;
    }

    /// <summary>移除窗口外（早于 now - WindowMinutes）的样本。</summary>
    private void Trim(DateTime now)
    {
        var cutoff = now.AddMinutes(-WindowMinutes);
        var drop = 0;
        while (drop < _samples.Count && _samples[drop].Timestamp < cutoff)
        {
            drop++;
        }
        if (drop > 0)
        {
            _samples.RemoveRange(0, drop);
        }
    }

    /// <summary>趋势箭头：与等级一一对应，UI 不得自行拼箭头。</summary>
    public static string Arrow(TrendLevel level) => level switch
    {
        TrendLevel.Increasing        => "↑",
        TrendLevel.Stable            => "───",
        TrendLevel.SlowConsumption   => "↘",
        TrendLevel.MediumConsumption => "↘↘",
        TrendLevel.FastConsumption   => "↘↘↘",
        _                            => ""
    };

    /// <summary>趋势等级中文名，给 MiniView / WebView2 payload 显示。</summary>
    public static string LevelText(TrendLevel level) => level switch
    {
        TrendLevel.Collecting        => "采集中",
        TrendLevel.Increasing        => "余额增加",
        TrendLevel.Stable            => "基本持平",
        TrendLevel.SlowConsumption   => "缓慢消耗",
        TrendLevel.MediumConsumption => "中速消耗",
        TrendLevel.FastConsumption   => "快速消耗",
        _                            => ""
    };

    /// <summary>趋势等级的 payload 键名（小写串），给 web/*.html 用。</summary>
    public static string LevelKey(TrendLevel level) => level switch
    {
        TrendLevel.Collecting        => "collecting",
        TrendLevel.Increasing        => "increasing",
        TrendLevel.Stable            => "stable",
        TrendLevel.SlowConsumption   => "slow",
        TrendLevel.MediumConsumption => "medium",
        TrendLevel.FastConsumption   => "fast",
        _                            => "unknown"
    };
}
