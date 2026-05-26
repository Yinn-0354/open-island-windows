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
    public void ResolvePid_MatchesByCwd_IgnoringCaseAndTrailingSlash()
    {
        var cands = new[] { C(10, @"C:\proj\a", "s-a"), C(20, @"C:\proj\b\", "s-b") };
        Assert.Equal(20, TerminalTargeting.ResolvePid(@"c:\proj\b", null, cands));
    }

    [Fact]
    public void ResolvePid_MatchesBySessionId_WhenCwdMisses()
    {
        var cands = new[] { C(10, null, "s-a"), C(20, null, "s-b") };
        Assert.Equal(20, TerminalTargeting.ResolvePid(null, "s-b", cands));
    }

    [Fact]
    public void ResolvePid_NoPositiveMatch_ReturnsNull_EvenWithSingleCandidate()
    {
        // The dangerous "only one claude running -> it's the target" fallback must NOT
        // apply to injection: a stray prompt in the wrong session is harmful.
        var cands = new[] { C(99, null, "some-other-session") };
        Assert.Null(TerminalTargeting.ResolvePid(@"C:\proj\x", "my-session", cands));
    }

    [Fact]
    public void ResolvePid_EmptyCandidates_ReturnsNull()
    {
        Assert.Null(TerminalTargeting.ResolvePid(@"C:\x", "s", Array.Empty<TerminalCandidate>()));
    }
}
