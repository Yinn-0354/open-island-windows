using OpenIsland.Core.Models;

namespace OpenIsland.Tests;

/// <summary>
/// 并发权限请求队列：并行 subagent 共享同一 session_id 时，多个权限请求必须各自入队、
/// 逐个显示与解决，而不是互相覆盖（曾经的 bug：只显示一个、接受后其余消失）。
/// </summary>
public class SessionStatePermissionQueueTests
{
    private static SessionState OneRunningSession(string sid = "sess-1")
        => new(new[]
        {
            new AgentSession { Id = sid, Title = "t", Tool = AgentTool.ClaudeCode, Phase = SessionPhase.Running }
        });

    private static PermissionRequested Req(string sid, string id, string tool = "Bash")
        => new()
        {
            SessionId = sid,
            Request = new PermissionRequest { Id = id, ToolName = tool, Description = $"{tool} {id}" }
        };

    [Fact]
    public void ConcurrentRequests_SameSession_AllQueued_HeadIsEarliest()
    {
        var s = OneRunningSession()
            .Apply(Req("sess-1", "a"))
            .Apply(Req("sess-1", "b"))
            .Apply(Req("sess-1", "c"));

        var sess = s.SessionsById["sess-1"];
        Assert.Equal(SessionPhase.WaitingForApproval, sess.Phase);
        Assert.Equal(3, sess.PendingPermissions.Count);          // 三个都在，没被覆盖
        Assert.Equal("a", sess.PermissionRequest!.Id);            // 队头 = 最早那个
    }

    [Fact]
    public void Resolve_RemovesHead_KeepsRestWaiting()
    {
        var s = OneRunningSession()
            .Apply(Req("sess-1", "a")).Apply(Req("sess-1", "b")).Apply(Req("sess-1", "c"))
            .ResolvePermission("sess-1", approved: true);

        var sess = s.SessionsById["sess-1"];
        Assert.Equal(2, sess.PendingPermissions.Count);
        Assert.Equal("b", sess.PermissionRequest!.Id);            // 接受后显示下一个
        Assert.Equal(SessionPhase.WaitingForApproval, sess.Phase); // 还有 → 继续等待批准
    }

    [Fact]
    public void Resolve_AllOneByOne_EndsRunningWithEmptyQueue()
    {
        var s = OneRunningSession()
            .Apply(Req("sess-1", "a")).Apply(Req("sess-1", "b")).Apply(Req("sess-1", "c"))
            .ResolvePermission("sess-1", true)
            .ResolvePermission("sess-1", true)
            .ResolvePermission("sess-1", true);

        var sess = s.SessionsById["sess-1"];
        Assert.Empty(sess.PendingPermissions);
        Assert.Null(sess.PermissionRequest);
        Assert.Equal(SessionPhase.Running, sess.Phase);           // 全部答完才转 Running
    }

    [Fact]
    public void DuplicateRequest_SameId_NotQueuedTwice()
    {
        var s = OneRunningSession()
            .Apply(Req("sess-1", "dup")).Apply(Req("sess-1", "dup"));

        Assert.Single(s.SessionsById["sess-1"].PendingPermissions); // 同 tool_use_id 去重
    }

    [Fact]
    public void AttentionCount_CountsSessionOnce_NotPerPendingItem()
    {
        var s = OneRunningSession()
            .Apply(Req("sess-1", "a")).Apply(Req("sess-1", "b"));

        Assert.Equal(1, s.AttentionCount); // 一个会话(含2个pending)算1次关注
    }

    [Fact]
    public void SingleRequest_BackwardCompatible()
    {
        var s = OneRunningSession().Apply(Req("sess-1", "only"));
        var sess = s.SessionsById["sess-1"];
        Assert.Equal(SessionPhase.WaitingForApproval, sess.Phase);
        Assert.Equal("only", sess.PermissionRequest!.Id);
        Assert.Single(sess.PendingPermissions);
    }
}
