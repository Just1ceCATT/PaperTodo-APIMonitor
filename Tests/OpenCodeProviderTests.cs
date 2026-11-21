using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using Xunit;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Tests;

/// <summary>
/// OpenCode Go 三窗口用量解析测试。时间通过显式 DateTimeOffset 注入，确保倒计时断言可重复。
/// 覆盖：三窗口映射、percent == 0 丢弃占位重置时间、部分窗口畸形跳过、
/// 顶层缺少 usage、字符串 percent / 秒级时间戳兼容、rate-limited 按上游 percent 展示。
/// </summary>
public class OpenCodeProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 7, 0, 0, TimeSpan.Zero);

    /// <summary>三窗口正确映射：remaining = 100 - percent，resetsAt 转小时数。</summary>
    [Fact]
    public void ThreeWindows_MapToRemainingPercentAndResetHours()
    {
        const string json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 23, "resetsAt": "2026-11-21T08:00:00.000Z" },
            "weekly":  { "status": "ok", "percent": 41, "resetsAt": "2026-11-24T00:00:00.000Z" },
            "monthly": { "status": "ok", "percent": 12, "resetsAt": "2026-12-01T00:00:00.000Z" }
          }
        }
        """;

        var parsed = OpenCodeProvider.ParseUsage(json, Now);

        Assert.True(parsed.HasAny);
        Assert.NotNull(parsed.Rolling);
        Assert.NotNull(parsed.Weekly);
        Assert.NotNull(parsed.Monthly);
        var rolling = parsed.Rolling!.Value;
        var weekly = parsed.Weekly!.Value;
        var monthly = parsed.Monthly!.Value;
        Assert.Equal(23, rolling.UsedPercent, 3);
        Assert.Equal(77, rolling.RemainingPercent, 3);
        Assert.Equal(1.0, rolling.HoursToReset, 3);
        Assert.Equal(59, weekly.RemainingPercent, 3);
        Assert.Equal(65.0, weekly.HoursToReset, 3);
        Assert.Equal(88, monthly.RemainingPercent, 3);
    }

    /// <summary>percent == 0 时上游 resetsAt 是占位值，必须丢弃（小时数 0），剩余 100%。</summary>
    [Fact]
    public void ZeroPercent_DropsPlaceholderResetTime()
    {
        const string json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 0, "resetsAt": "2026-11-21T12:00:00.000Z" }
          }
        }
        """;

        var parsed = OpenCodeProvider.ParseUsage(json, Now);

        Assert.NotNull(parsed.Rolling);
        var rolling = parsed.Rolling!.Value;
        Assert.Equal(100, rolling.RemainingPercent, 3);
        Assert.Equal(0, rolling.HoursToReset, 3);
    }

    /// <summary>部分窗口畸形（percent 非数字）时只跳过该窗口，不影响其他窗口。</summary>
    [Fact]
    public void MalformedWindow_IsSkippedWithoutFailingOthers()
    {
        const string json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": "abc", "resetsAt": "2026-11-21T08:00:00.000Z" },
            "weekly":  { "status": "ok", "percent": 41, "resetsAt": "2026-11-24T00:00:00.000Z" }
          }
        }
        """;

        var parsed = OpenCodeProvider.ParseUsage(json, Now);

        Assert.True(parsed.HasAny);
        Assert.Null(parsed.Rolling);
        Assert.NotNull(parsed.Weekly);
        Assert.Null(parsed.Monthly);
        Assert.Equal(59, parsed.Weekly!.Value.RemainingPercent, 3);
    }

    /// <summary>顶层没有 usage（旧扁平响应形态）时三个窗口都为空。</summary>
    [Fact]
    public void MissingUsageContainer_ReturnsEmpty()
    {
        var parsed = OpenCodeProvider.ParseUsage("""{"foo":1}""", Now);

        Assert.False(parsed.HasAny);
        Assert.Null(parsed.Rolling);
        Assert.Null(parsed.Weekly);
        Assert.Null(parsed.Monthly);
    }

    /// <summary>percent 支持字符串数字，resetsAt 支持秒级 Unix 时间戳。</summary>
    [Fact]
    public void StringPercent_AndSecondTimestamp_AreSupported()
    {
        var resetSeconds = Now.ToUnixTimeSeconds() + 3600;
        var json = $$"""
        {
          "usage": {
            "rolling": { "status": "ok", "percent": "12", "resetsAt": {{resetSeconds}} }
          }
        }
        """;

        var parsed = OpenCodeProvider.ParseUsage(json, Now);

        Assert.NotNull(parsed.Rolling);
        var rolling = parsed.Rolling!.Value;
        Assert.Equal(12, rolling.UsedPercent, 3);
        Assert.Equal(88, rolling.RemainingPercent, 3);
        Assert.Equal(1.0, rolling.HoursToReset, 3);
    }

    /// <summary>rate-limited 时上游已把 percent 钉在 100，不特判，剩余 0%。</summary>
    [Fact]
    public void RateLimited_UsesPinnedPercent()
    {
        const string json = """
        {
          "usage": {
            "rolling": { "status": "rate-limited", "percent": 100, "resetsAt": "2026-11-21T08:00:00.000Z" }
          }
        }
        """;

        var parsed = OpenCodeProvider.ParseUsage(json, Now);

        Assert.NotNull(parsed.Rolling);
        var rolling = parsed.Rolling!.Value;
        Assert.Equal(100, rolling.UsedPercent, 3);
        Assert.Equal(0, rolling.RemainingPercent, 3);
    }
}
