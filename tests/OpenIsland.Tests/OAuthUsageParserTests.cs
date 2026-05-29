using OpenIsland.Core;

namespace OpenIsland.Tests;

/// <summary>
/// 解析 /api/oauth/usage 的 5 小时窗口。覆盖文档形状与几种宽容退路。
/// </summary>
public class OAuthUsageParserTests
{
    [Fact]
    public void Utilization_And_ResetsAt_ParsedCorrectly()
    {
        var json = """
        {"five_hour":{"utilization":45,"resets_at":"2026-05-30T15:23:45Z"},
         "seven_day":{"utilization":30,"resets_at":"2026-06-06T00:00:00Z"}}
        """;
        Assert.True(OAuthUsageParser.TryParseFiveHour(json, out var used, out var reset));
        Assert.Equal(0.45, used, 3);
        Assert.NotNull(reset);
        Assert.Equal(new DateTime(2026, 5, 30, 15, 23, 45, DateTimeKind.Utc), reset!.Value);
    }

    [Fact]
    public void RemainingAndLimit_FallbackComputesUsed()
    {
        var json = """{"five_hour":{"remaining":250,"limit":1000}}""";
        Assert.True(OAuthUsageParser.TryParseFiveHour(json, out var used, out _));
        Assert.Equal(0.75, used, 3); // 1 - 250/1000
    }

    [Fact]
    public void UnixSecondsReset_Parsed()
    {
        var json = """{"five_hour":{"utilization":10,"reset":1780000000}}""";
        Assert.True(OAuthUsageParser.TryParseFiveHour(json, out _, out var reset));
        Assert.NotNull(reset);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780000000).UtcDateTime, reset!.Value);
    }

    [Fact]
    public void Utilization_ClampedTo_0_1()
    {
        Assert.True(OAuthUsageParser.TryParseFiveHour("""{"five_hour":{"utilization":140}}""", out var used, out _));
        Assert.Equal(1.0, used, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"seven_day":{"utilization":5}}""")] // 没有 five_hour
    [InlineData("""{"five_hour":{"foo":1}}""")]          // five_hour 无可用数字
    public void Malformed_ReturnsFalse(string? json)
        => Assert.False(OAuthUsageParser.TryParseFiveHour(json, out _, out _));
}
