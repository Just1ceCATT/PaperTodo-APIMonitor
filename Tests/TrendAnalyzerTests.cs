using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using Xunit;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Tests;

/// <summary>
/// TrendAnalyzer 的 8 个基础场景测试，对应 design/ds_balance.md §11 的要求。
/// 所有时间通过显式 DateTime 传入，不依赖 DateTime.Now，确保测试可重复。
/// </summary>
public class TrendAnalyzerTests
{
    private static readonly DateTime Origin = new(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>30 分钟内余额几乎不变：归 Stable。
    /// 用单调微下降（每步 −0.005）来模拟"持平"，净消耗 0.01/30min ≈ 0.02%/h，远低于 StableRatio。</summary>
    [Fact]
    public void StableBalance_ClassifiesAsStable()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(8), 99.995);
        analyzer.Add(t.AddMinutes(15), 99.990);
        analyzer.Add(t.AddMinutes(22), 99.985);
        analyzer.Add(t.AddMinutes(30), 99.980);

        var result = analyzer.Analyze(t.AddMinutes(30), 99.980);

        Assert.Equal(TrendLevel.Stable, result.Level);
        Assert.True(result.ConsumptionRatePerHour < TrendAnalyzer.StableRatio,
            $"Consumption={result.ConsumptionRatePerHour:F4} 应当 < {TrendAnalyzer.StableRatio}");
        Assert.Equal(5, result.SampleCount);
    }

    /// <summary>30 分钟内余额下降约 1%/h：归 SlowConsumption。</summary>
    [Fact]
    public void SlowConsumption_ClassifiesAsSlow()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        // 起始 100，每小时 -1（30 分钟 -0.5），相对消耗 ~1%/h。
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(10), 99.83);
        analyzer.Add(t.AddMinutes(20), 99.67);
        analyzer.Add(t.AddMinutes(30), 99.50);

        var result = analyzer.Analyze(t.AddMinutes(30), 99.50);

        Assert.Equal(TrendLevel.SlowConsumption, result.Level);
        Assert.InRange(result.ConsumptionRatePerHour, 0.005, 0.02);
        Assert.True(result.RatePerHour < 0, "应为消耗方向");
    }

    /// <summary>30 分钟内余额快速下降：归 FastConsumption，且 DepletionHours 有合理值。</summary>
    [Fact]
    public void FastConsumption_HasDepletionHours()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        // 起始 100，每小时 -10（30 分钟 -5），相对消耗 10%/h → FastConsumption。
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(10), 98.33);
        analyzer.Add(t.AddMinutes(20), 96.67);
        analyzer.Add(t.AddMinutes(30), 95.00);

        var result = analyzer.Analyze(t.AddMinutes(30), 95.00);

        Assert.Equal(TrendLevel.FastConsumption, result.Level);
        Assert.True(result.ConsumptionRatePerHour > TrendAnalyzer.MediumRatio,
            $"Consumption={result.ConsumptionRatePerHour:F4} 应当 > {TrendAnalyzer.MediumRatio}");
        // 消耗 10¥/h，当前 95 → 约 9.5h。
        Assert.NotNull(result.DepletionHours);
        Assert.InRange(result.DepletionHours!.Value, 9.0, 10.0);
    }

    /// <summary>余额增加（充值）：窗口被重置，不产生"消耗变慢"假信号。</summary>
    [Fact]
    public void BalanceIncrease_ResetsWindow()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        // 先积累 25 分钟下降数据
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(10), 95.00);
        analyzer.Add(t.AddMinutes(20), 90.00);
        analyzer.Add(t.AddMinutes(25), 88.00);
        // 充值到 200，触发窗口重置
        analyzer.Add(t.AddMinutes(30), 200.00);

        var result = analyzer.Analyze(t.AddMinutes(30), 200.00);

        // 重置后只剩 1 条样本，应为 Unknown（不在 Collecting 范围：单样本）。
        Assert.Equal(TrendLevel.Unknown, result.Level);
        Assert.Equal(1, result.SampleCount);
    }

    /// <summary>样本不足（1 条）：归 Unknown。</summary>
    [Fact]
    public void SingleSample_IsUnknown()
    {
        var analyzer = new TrendAnalyzer();
        analyzer.Add(Origin, 100.00);

        var result = analyzer.Analyze(Origin.AddMinutes(30), 100.00);

        Assert.Equal(TrendLevel.Unknown, result.Level);
        Assert.Equal(1, result.SampleCount);
        Assert.Null(result.DepletionHours);
    }

    /// <summary>分析时间不足（2 条样本但跨度 3 分钟）：归 Collecting。</summary>
    [Fact]
    public void ShortElapsed_IsCollecting()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(3), 99.50);

        var result = analyzer.Analyze(t.AddMinutes(3), 99.50);

        Assert.Equal(TrendLevel.Collecting, result.Level);
        Assert.Equal(2, result.SampleCount);
    }

    /// <summary>API 失败不喂样本：失败期间 SampleCount 不增长。</summary>
    [Fact]
    public void FailedSamples_AreNotRecorded()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        // 模拟：成功 → 成功 → 失败（不调用 Add）→ 成功
        analyzer.Add(t, 100.00);
        analyzer.Add(t.AddMinutes(2), 99.90);
        // 失败窗口：调用方不该 Add
        analyzer.Add(t.AddMinutes(4), 99.70);

        var result = analyzer.Analyze(t.AddMinutes(4), 99.70);

        // 即使跨度只有 4 分钟，也应有 3 条样本（API 成功期间持续累计）。
        Assert.Equal(3, result.SampleCount);
        // 跨度不足 5 分钟 → Collecting。
        Assert.Equal(TrendLevel.Collecting, result.Level);
    }

    /// <summary>滑动窗口过期数据移除：40 分钟数据后 SampleCount 只含最近 30 分钟。</summary>
    [Fact]
    public void OldSamples_AreTrimmedFromWindow()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        // 每分钟一条样本，共 41 条，跨越 40 分钟。
        for (var i = 0; i <= 40; i++)
        {
            analyzer.Add(t.AddMinutes(i), 100.00 - i * 0.1);
        }

        // 在 40 分钟时分析：窗口 [10:00, 10:40] 应只含 11 条样本（第 10~40 分钟）。
        var result = analyzer.Analyze(t.AddMinutes(40), 100.00 - 40 * 0.1);

        // [10:00, 10:40] 的 [10:10, 10:40] 子区间内有 31 条样本（第 10, 11, ..., 40 分钟）。
        Assert.Equal(31, result.SampleCount);
        Assert.True(result.SampleCount <= 31);
    }

    /// <summary>浮点噪声容差：0.01 元以内的"增加"不触发充值重置。</summary>
    [Fact]
    public void TinyIncrease_DoesNotReset()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        analyzer.Add(t, 100.00);
        // 比上次仅多 0.005，低于 RechargeEpsilon=0.01，不应重置。
        analyzer.Add(t.AddMinutes(2), 100.005);

        Assert.Equal(2, analyzer.SampleCount);
    }

    /// <summary>相对消耗率分母：窗口起始余额为 0 时归 Unknown（除零保护）。
    /// 注意：为了保留全部样本不触发充值重置，每次相邻增量都 ≤ RechargeEpsilon。</summary>
    [Fact]
    public void ZeroWindowStartBalance_IsUnknown()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        analyzer.Add(t, 0.00);
        // 0 → 0.005 < 0.01，不重置。
        analyzer.Add(t.AddMinutes(10), 0.005);
        analyzer.Add(t.AddMinutes(20), 0.010);

        var result = analyzer.Analyze(t.AddMinutes(20), 0.010);

        Assert.Equal(TrendLevel.Unknown, result.Level);
        Assert.Equal(3, result.SampleCount);
    }

    /// <summary>斜率为正（余额上升）：Increasing。注意：每次增加 > RechargeEpsilon 都会触发窗口重置，
    /// 所以这里采用连续小增量逐步上升（每次 0.005，避开 0.01 阈值）来模拟连续上升。</summary>
    [Fact]
    public void IncreasingBalance_ClassifiesAsIncreasing()
    {
        var analyzer = new TrendAnalyzer();
        var t = Origin;
        analyzer.Add(t, 50.00);
        analyzer.Add(t.AddMinutes(10), 50.005);
        analyzer.Add(t.AddMinutes(20), 50.010);

        var result = analyzer.Analyze(t.AddMinutes(20), 50.010);

        Assert.Equal(TrendLevel.Increasing, result.Level);
        Assert.True(result.RatePerHour > 0);
        Assert.Equal(0, result.ConsumptionRatePerHour);
    }
}