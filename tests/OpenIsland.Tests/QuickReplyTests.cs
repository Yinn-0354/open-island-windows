using OpenIsland.Core;

namespace OpenIsland.Tests;

public class QuickReplyTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \n")]
    public void Prepare_RejectsEmptyOrWhitespace(string? raw)
    {
        Assert.False(QuickReplyText.Prepare(raw).Ok);
    }

    [Fact]
    public void Prepare_TrimsOuterWhitespace_KeepsInner()
    {
        var r = QuickReplyText.Prepare("  hello\nworld  ");
        Assert.True(r.Ok);
        Assert.Equal("hello\nworld", r.Text);
    }

    [Fact]
    public void Prepare_AcceptsUnicode()
    {
        var r = QuickReplyText.Prepare("继续，把测试补全");
        Assert.True(r.Ok);
        Assert.Equal("继续，把测试补全", r.Text);
    }

    [Fact]
    public void Prepare_RejectsOverLength()
    {
        Assert.False(QuickReplyText.Prepare(new string('x', QuickReplyText.MaxLength + 1)).Ok);
    }
}

public class TerminalTargetingTests
{
    private static TerminalCandidate C(int pid, string? cwd, string? sid) => new(pid, cwd, sid);

    [Fact]
    public void ResolvePid_UniqueCwdMatch_ReturnsIt_IgnoringCaseAndTrailingSlash()
    {
        var cands = new[] { C(10, @"C:\proj\a", "s-a"), C(20, @"C:\proj\b\", "s-b") };
        Assert.Equal(20, TerminalTargeting.ResolvePid(@"c:\proj\b", null, cands));
    }

    [Fact]
    public void ResolvePid_AmbiguousCwd_TwoSessionsSameDir_ReturnsNull()
    {
        // Two agents in the SAME directory: can't tell which card the user meant — abort, don't guess.
        var cands = new[] { C(10, @"C:\proj\a", "s-1"), C(20, @"C:\proj\a", "s-2") };
        Assert.Null(TerminalTargeting.ResolvePid(@"C:\proj\a", null, cands));
    }

    [Fact]
    public void ResolvePid_MatchesBySessionId_WhenCwdMisses()
    {
        var cands = new[] { C(10, null, "s-a"), C(20, null, "s-b") };
        Assert.Equal(20, TerminalTargeting.ResolvePid(null, "s-b", cands));
    }

    [Fact]
    public void ResolvePid_SingleCandidate_FallsBackToIt_WhenNoPositiveMatch()
    {
        // On Windows the running claude's cwd is often unreadable and its id is synthetic, so
        // both positive matches miss. With exactly ONE claude alive there is no ambiguity — it's
        // the target. (This is what makes the feature work in the common single-session case.)
        var cands = new[] { C(99, null, "synthetic-id") };
        Assert.Equal(99, TerminalTargeting.ResolvePid(@"C:\proj\x", "real-uuid", cands));
    }

    [Fact]
    public void ResolvePid_MultipleCandidates_NoPositiveMatch_ReturnsNull()
    {
        // 2+ alive and nothing disambiguates → abort (never blind-inject into one of several).
        var cands = new[] { C(98, null, "x"), C(99, null, "y") };
        Assert.Null(TerminalTargeting.ResolvePid(@"C:\proj\x", "real-uuid", cands));
    }

    [Fact]
    public void ResolvePid_EmptyCandidates_ReturnsNull()
    {
        Assert.Null(TerminalTargeting.ResolvePid(@"C:\x", "s", Array.Empty<TerminalCandidate>()));
    }
}
