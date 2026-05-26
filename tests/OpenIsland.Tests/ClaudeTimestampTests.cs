using OpenIsland.Core;

namespace OpenIsland.Tests;

public class ClaudeTimestampTests
{
    [Fact]
    public void TryParseUtc_ZSuffix_ReturnsUtcKindWithSameInstant()
    {
        Assert.True(ClaudeTimestamp.TryParseUtc("2026-05-26T12:00:00.000Z", out var utc));
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void TryParseUtc_WithOffset_ConvertsToUtc()
    {
        // 2026-05-26 21:00 at +09:00 is 2026-05-26 12:00 UTC.
        Assert.True(ClaudeTimestamp.TryParseUtc("2026-05-26T21:00:00+09:00", out var utc));
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void TryParseUtc_NoTimezone_TreatedAsUtc()
    {
        Assert.True(ClaudeTimestamp.TryParseUtc("2026-05-26T12:00:00", out var utc));
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc), utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    public void TryParseUtc_Invalid_ReturnsFalse(string? value)
    {
        Assert.False(ClaudeTimestamp.TryParseUtc(value, out _));
    }
}
