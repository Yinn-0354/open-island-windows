using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;

namespace OpenIsland.Tests;

/// <summary>
/// 多问题 AskUserQuestion 场景（模型 C / P5-simple / W2 / R2）回归：
///
/// - TotalQuestions 从 tool_input.questions 数组长度解析（ClaudeHooks.ParseTotalQuestions）
/// - watcher 推进检测：多问题场景不清 PendingPermissions（W2，避免中途推进误清剩下问题卡片）
/// - PostToolUse CompletedAskUserQuestion=true：清 PendingPermissions（R2 兜底，覆盖用户终端答场景）
/// - 单问题场景保持原有行为（watcher 推进 = 答完 = 清卡）
///
/// Claude 协议层无"答完一问"中间信号暴露（hook 文档确认），灵动岛靠 PermissionRequest.TotalQuestions
/// 区分单/多问题分支，靠 SessionActivityUpdated.CompletedAskUserQuestion 做 PostToolUse 兜底清卡。
/// </summary>
public class MultiQuestionAskUserQuestionTests
{
    private static SessionState OneRunningSession(string sid = "sess-1")
        => new(new[]
        {
            new AgentSession { Id = sid, Title = "t", Tool = AgentTool.ClaudeCode, Phase = SessionPhase.Running }
        });

    /// <summary>构造一个 AskUserQuestion 的 PermissionRequested，TotalQuestions = questions 数组长度。
    /// 调用方传 questions JSON 字符串数组，每个元素是单条 question 的 JSON（不含外层 questions 包装）。</summary>
    private static PermissionRequested AskUserQuestionReq(string sid, string id, int questionCount)
    {
        var questions = new System.Collections.Generic.List<object>();
        for (int i = 0; i < questionCount; i++)
        {
            questions.Add(new
            {
                question = $"Q{i + 1}?",
                header = $"H{i + 1}",
                options = new[]
                {
                    new { label = $"opt{i + 1}.1", description = "" },
                    new { label = $"opt{i + 1}.2", description = "" }
                },
                multiSelect = false
            });
        }

        var toolInput = new System.Collections.Generic.Dictionary<string, object>
        {
            ["questions"] = questions
        };

        return new PermissionRequested
        {
            SessionId = sid,
            Request = new PermissionRequest
            {
                Id = id,
                ToolName = "AskUserQuestion",
                Description = "AskUserQuestion",
                ToolInput = toolInput,
                TotalQuestions = questionCount
            }
        };
    }

    private static SessionActivityUpdated ActivityWithTranscriptAdvance(string sid, DateTime advanceTime, bool completedAskUserQuestion = false)
        => new()
        {
            SessionId = sid,
            Summary = "scan tick",
            Phase = SessionPhase.Running,
            LastTranscriptTimestamp = advanceTime
        };

    // ── TotalQuestions 解析（ClaudeHooks.ParseTotalQuestions 间接经 ClaudeHookPayload.ToAgentEvent）──

    [Fact]
    public void ParseTotalQuestions_SingleQuestion_Returns1()
    {
        var payload = new ClaudeHookPayload
        {
            HookEventName = "PreToolUse",
            SessionId = "s",
            ToolName = "AskUserQuestion",
            PermissionMode = "default",
            ToolInput = JsonElementFromQuestions(1)
        };
        var ev = payload.ToAgentEvent("claude") as PermissionRequested;
        Assert.NotNull(ev);
        Assert.Equal(1, ev!.Request.TotalQuestions);
    }

    [Fact]
    public void ParseTotalQuestions_FourQuestions_Returns4()
    {
        var payload = new ClaudeHookPayload
        {
            HookEventName = "PreToolUse",
            SessionId = "s",
            ToolName = "AskUserQuestion",
            PermissionMode = "auto",
            ToolInput = JsonElementFromQuestions(4)
        };
        var ev = payload.ToAgentEvent("claude") as PermissionRequested;
        Assert.NotNull(ev);
        Assert.Equal(4, ev!.Request.TotalQuestions);
    }

    [Fact]
    public void ParseTotalQuestions_NonAskUserQuestion_Returns1()
    {
        var payload = new ClaudeHookPayload
        {
            HookEventName = "PreToolUse",
            SessionId = "s",
            ToolName = "Bash",
            PermissionMode = "default",
            ToolInput = JsonElementFromObject(new { command = "ls" })
        };
        var ev = payload.ToAgentEvent("claude") as PermissionRequested;
        Assert.NotNull(ev);
        Assert.Equal(1, ev!.Request.TotalQuestions);
    }

    // ── W2:多问题场景 watcher 推进不清 PendingPermissions ──

    [Fact]
    public void WatcherAdvance_MultiQuestion_DoesNotClearPendingPermissions()
    {
        var s = OneRunningSession()
            .Apply(AskUserQuestionReq("sess-1", "auq", questionCount: 3));
        var permTime = s.SessionsById["sess-1"].PermissionRequest!.Timestamp;

        // transcript 推进 > permission timestamp + 1 秒（Watcher 的清卡触发条件）
        var advance = ActivityWithTranscriptAdvance("sess-1", permTime.AddSeconds(5));

        s = s.Apply(advance);

        var sess = s.SessionsById["sess-1"];
        // W2:多问题场景不该清 PendingPermissions（推进只代表答完当前一问，不是整个请求答完）
        Assert.NotNull(sess.PermissionRequest);
        Assert.Single(sess.PendingPermissions);
        // 但 phase 仍允许更新（推进后 Claude 切下一问）
        Assert.Equal(SessionPhase.Running, sess.Phase);
    }

    [Fact]
    public void WatcherAdvance_SingleQuestion_ClearsPendingPermissions()
    {
        var s = OneRunningSession()
            .Apply(AskUserQuestionReq("sess-1", "auq", questionCount: 1));
        var permTime = s.SessionsById["sess-1"].PermissionRequest!.Timestamp;

        var advance = ActivityWithTranscriptAdvance("sess-1", permTime.AddSeconds(5));

        s = s.Apply(advance);

        var sess = s.SessionsById["sess-1"];
        // 单问题场景保持原有行为：watcher 推进 = 答完 = 清卡
        Assert.Null(sess.PermissionRequest);
        Assert.Empty(sess.PendingPermissions);
    }

    // ── R2:PostToolUse CompletedAskUserQuestion=true 清卡（兜底用户终端答场景）──

    [Fact]
    public void CompletedAskUserQuestion_ClearsPendingPermissions_MultiQuestion()
    {
        var s = OneRunningSession()
            .Apply(AskUserQuestionReq("sess-1", "auq", questionCount: 3));

        // PostToolUse AskUserQuestion: CompletedAskUserQuestion=true
        var postTool = new SessionActivityUpdated
        {
            SessionId = "sess-1",
            Summary = "AskUserQuestion completed",
            Phase = SessionPhase.Running,
            CompletedAskUserQuestion = true
        };

        s = s.Apply(postTool);

        var sess = s.SessionsById["sess-1"];
        Assert.Null(sess.PermissionRequest);
        Assert.Empty(sess.PendingPermissions);
    }

    [Fact]
    public void CompletedAskUserQuestion_ClearsPendingPermissions_SingleQuestion()
    {
        var s = OneRunningSession()
            .Apply(AskUserQuestionReq("sess-1", "auq", questionCount: 1));

        var postTool = new SessionActivityUpdated
        {
            SessionId = "sess-1",
            Summary = "AskUserQuestion completed",
            Phase = SessionPhase.Running,
            CompletedAskUserQuestion = true
        };

        s = s.Apply(postTool);

        var sess = s.SessionsById["sess-1"];
        Assert.Null(sess.PermissionRequest);
        Assert.Empty(sess.PendingPermissions);
    }

    [Fact]
    public void NonAskUserQuestionPostToolUse_DoesNotSetCompletedAskUserQuestion()
    {
        // 非 AskUserQuestion 的 PostToolUse（如 Bash）不该触发 R2 清卡。
        // ClaudeHooks.cs 的 posttooluse case 只在 tool_name=="AskUserQuestion" 时设 true。
        var payload = new ClaudeHookPayload
        {
            HookEventName = "PostToolUse",
            SessionId = "s",
            ToolName = "Bash"
        };
        var ev = payload.ToAgentEvent("claude") as SessionActivityUpdated;
        Assert.NotNull(ev);
        Assert.False(ev!.CompletedAskUserQuestion);
    }

    // ── 辅助:构造 JsonElement ──

    private static System.Text.Json.JsonElement JsonElementFromQuestions(int count)
    {
        var questions = new System.Collections.Generic.List<object>();
        for (int i = 0; i < count; i++)
        {
            questions.Add(new
            {
                question = $"Q{i + 1}?",
                header = $"H{i + 1}",
                options = new[] { new { label = "a", description = "" } },
                multiSelect = false
            });
        }
        return JsonElementFromObject(new { questions });
    }

    private static System.Text.Json.JsonElement JsonElementFromObject(object obj)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // JsonDocument 的 RootElement 是 struct，复制出来要用 Deserialize
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
    }
}
