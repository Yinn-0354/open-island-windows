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
    public void Prepare_TrimsOuterWhitespace()
    {
        var r = QuickReplyText.Prepare("  hello world  ");
        Assert.True(r.Ok);
        Assert.Equal("hello world", r.Text);
    }

    // 关键修复：注入是「粘贴整段 + 末尾一个回车」。若回复里含内部换行，多数终端会把每个
    // 换行当成一次提交，导致 line1 被当 prompt 先跑掉、末尾回车又提交残段。所以 Prepare
    // 必须把内部换行（\n / \r\n / \r，含夹杂空白与连续空行）规整成单个空格 —— 作为一条 prompt 发送。
    [Theory]
    [InlineData("line1\nline2", "line1 line2")]
    [InlineData("line1\r\nline2", "line1 line2")]
    [InlineData("line1\rline2", "line1 line2")]
    [InlineData("a\n\n\nb", "a b")]
    [InlineData("a\n   \nb", "a b")]
    [InlineData("  first\nsecond  ", "first second")]
    public void Prepare_CollapsesInnerNewlinesToSingleSpace(string raw, string expected)
    {
        var r = QuickReplyText.Prepare(raw);
        Assert.True(r.Ok);
        Assert.Equal(expected, r.Text);
        Assert.DoesNotContain('\n', r.Text);
        Assert.DoesNotContain('\r', r.Text);
    }

    [Fact]
    public void Prepare_SingleLine_Unchanged()
    {
        var r = QuickReplyText.Prepare("just one line");
        Assert.True(r.Ok);
        Assert.Equal("just one line", r.Text);
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
