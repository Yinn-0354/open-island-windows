using System.Collections.Immutable;

namespace OpenIsland.Core.Models;

/// <summary>
/// 会话状态管理 - 所有会话变更的唯一真相源
/// </summary>
public class SessionState
{
    private readonly ImmutableDictionary<string, AgentSession> _sessionsById;

    public SessionState(IEnumerable<AgentSession>? sessions = null)
    {
        _sessionsById = sessions?.ToImmutableDictionary(s => s.Id, s => s)
            ?? ImmutableDictionary<string, AgentSession>.Empty;
    }

    public IReadOnlyDictionary<string, AgentSession> SessionsById => _sessionsById;

    /// <summary>
    /// 按更新时间排序的会话列表
    /// </summary>
    public IEnumerable<AgentSession> Sessions => _sessionsById.Values
        .OrderByDescending(s => s.UpdatedAt)
        .ThenBy(s => s.Title);

    /// <summary>
    /// 需要关注的首个会话
    /// </summary>
    public AgentSession? ActiveActionableSession => Sessions.FirstOrDefault(s => s.NeedsAttention);

    /// <summary>
    /// 运行中的会话数量
    /// </summary>
    public int RunningCount => _sessionsById.Values.Count(s => s.IsRunning);

    /// <summary>
    /// 需要关注的会话数量
    /// </summary>
    public int AttentionCount => _sessionsById.Values.Count(s => s.NeedsAttention);

    /// <summary>
    /// 活跃会话数量（非完成状态）
    /// </summary>
    public int LiveSessionCount => _sessionsById.Values.Count(s => s.Phase != SessionPhase.Completed);

    /// <summary>
    /// 已完成会话数量
    /// </summary>
    public int CompletedCount => _sessionsById.Values.Count(s => s.Phase == SessionPhase.Completed);

    /// <summary>
    /// 应用事件到状态，返回新的状态
    /// </summary>
    public SessionState Apply(AgentEvent @event)
    {
        return @event switch
        {
            SessionStarted started => ApplySessionStarted(started),
            SessionActivityUpdated updated => ApplyActivityUpdated(updated),
            PermissionRequested requested => ApplyPermissionRequested(requested),
            QuestionAsked asked => ApplyQuestionAsked(asked),
            SessionCompleted completed => ApplySessionCompleted(completed),
            JumpTargetUpdated jumpUpdated => ApplyJumpTargetUpdated(jumpUpdated),
            SessionMetadataUpdated metadataUpdated => ApplyMetadataUpdated(metadataUpdated),
            ClaudeSessionMetadataUpdated claudeMetadata => ApplyClaudeMetadataUpdated(claudeMetadata),
            CursorSessionMetadataUpdated cursorMetadata => ApplyCursorMetadataUpdated(cursorMetadata),
            GeminiSessionMetadataUpdated geminiMetadata => ApplyGeminiMetadataUpdated(geminiMetadata),
            OpenCodeSessionMetadataUpdated openCodeMetadata => ApplyOpenCodeMetadataUpdated(openCodeMetadata),
            ActionableStateResolved resolved => ApplyActionableStateResolved(resolved),
            _ => this
        };
    }

    private SessionState ApplySessionStarted(SessionStarted started)
    {
        var session = new AgentSession
        {
            Id = started.SessionId,
            Title = started.Title,
            Tool = started.Tool,
            Phase = started.InitialPhase,
            Origin = started.Origin,
            Summary = started.Summary,
            JumpTarget = started.JumpTarget,
            IsRemote = started.IsRemote,
            IsHookManaged = true,
            UpdatedAt = started.Timestamp,
            ClaudeMetadata = started.ClaudeMetadata,
            CodexMetadata = started.CodexMetadata,
            CursorMetadata = started.CursorMetadata,
            GeminiMetadata = started.GeminiMetadata,
            OpenCodeMetadata = started.OpenCodeMetadata
        };

        return new SessionState(_sessionsById.SetItem(session.Id, session).Values);
    }

    private SessionState ApplyActivityUpdated(SessionActivityUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        // 等待审批 / 回答时默认锁 phase ——避免 watcher tick 把橙卡刷掉。
        // 但加一个解锁旁路：如果 watcher 上报的 *transcript 末条消息时间* 晚于本次 permission/
        // question 的时间戳（说明用户已经在终端答完了，tool 跑过了，transcript 有新增），
        // 直接采纳 watcher 的 Phase。这样 PostToolUse hook 万一没传到，岛也能跟着 transcript
        // 自动下线橙卡。
        var newPhase = session.Phase;
        bool clearPermission = false;
        bool clearQuestion = false;
        if (session.Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer)
        {
            var anchor = session.PermissionRequest?.Timestamp
                         ?? session.QuestionPrompt?.Timestamp;
            if (anchor.HasValue
                && updated.LastTranscriptTimestamp.HasValue
                && updated.LastTranscriptTimestamp.Value > anchor.Value.AddSeconds(1))
            {
                newPhase = updated.Phase;
                clearPermission = session.Phase == SessionPhase.WaitingForApproval;
                clearQuestion = session.Phase == SessionPhase.WaitingForAnswer;
            }
        }
        else
        {
            newPhase = updated.Phase;
        }

        var newSession = session.With(
            summary: updated.Summary,
            phase: newPhase
        );
        if (clearPermission) newSession = newSession with { PendingPermissions = ImmutableList<PermissionRequest>.Empty };
        if (clearQuestion) newSession = newSession with { QuestionPrompt = null };

        // Senders only attach Title when they have a meaningful one (custom title or first user
        // message from transcript scan, prompt from user_prompt_submit hook), so trust them.
        if (!string.IsNullOrWhiteSpace(updated.Title) && updated.Title != session.Title)
        {
            newSession = newSession with { Title = updated.Title };
        }

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyPermissionRequested(PermissionRequested requested)
    {
        if (!_sessionsById.TryGetValue(requested.SessionId, out var session))
            return this;

        // 入队（按 Id/tool_use_id 去重，避免同一请求被重复 hook 投递堆叠）；不覆盖已有的 ——
        // 支持并行 subagent 共享同一 session_id 时的多个并发权限请求逐个排队。
        if (session.PendingPermissions.Any(p => p.Id == requested.Request.Id))
            return this;

        var newSession = session with
        {
            Phase = SessionPhase.WaitingForApproval,
            PendingPermissions = session.PendingPermissions.Add(requested.Request),
            UpdatedAt = DateTime.UtcNow
        };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyQuestionAsked(QuestionAsked asked)
    {
        if (!_sessionsById.TryGetValue(asked.SessionId, out var session))
            return this;

        var newSession = session.With(
            phase: SessionPhase.WaitingForAnswer,
            questionPrompt: asked.Prompt
        );

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplySessionCompleted(SessionCompleted completed)
    {
        if (!_sessionsById.TryGetValue(completed.SessionId, out var session))
            return this;

        var newSession = session.With(
            phase: SessionPhase.Completed,
            summary: completed.Summary
        ) with { IsSessionEnded = completed.IsSessionEnd };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyJumpTargetUpdated(JumpTargetUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session.With(jumpTarget: updated.JumpTarget);
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyMetadataUpdated(SessionMetadataUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session with { CodexMetadata = updated.CodexMetadata };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyClaudeMetadataUpdated(ClaudeSessionMetadataUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session with { ClaudeMetadata = updated.ClaudeMetadata };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyCursorMetadataUpdated(CursorSessionMetadataUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session with { CursorMetadata = updated.CursorMetadata };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyGeminiMetadataUpdated(GeminiSessionMetadataUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session with { GeminiMetadata = updated.GeminiMetadata };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyOpenCodeMetadataUpdated(OpenCodeSessionMetadataUpdated updated)
    {
        if (!_sessionsById.TryGetValue(updated.SessionId, out var session))
            return this;

        var newSession = session with { OpenCodeMetadata = updated.OpenCodeMetadata };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    private SessionState ApplyActionableStateResolved(ActionableStateResolved resolved)
    {
        if (!_sessionsById.TryGetValue(resolved.SessionId, out var session))
            return this;

        var newSession = session.With(
            phase: SessionPhase.Running,
            summary: resolved.Summary
        ) with
        {
            PendingPermissions = ImmutableList<PermissionRequest>.Empty,
            QuestionPrompt = null
        };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    /// <summary>
    /// 解决一个权限请求：移除队头（= 终端当前正在问的那个，逐个对应）。
    /// 队列还有就保持 WaitingForApproval 显示下一个；全部答完才转 Running ——
    /// 支持并行 subagent 共享同一 session_id 时的多个并发请求逐个解决。
    /// </summary>
    public SessionState ResolvePermission(string sessionId, bool approved, string? summary = null)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return this;
        if (session.PendingPermissions.Count == 0)
            return this;

        var remaining = session.PendingPermissions.RemoveAt(0);
        var newSession = session with
        {
            Phase = remaining.Count > 0 ? SessionPhase.WaitingForApproval : SessionPhase.Running,
            Summary = summary ?? session.Summary,
            PendingPermissions = remaining,
            UpdatedAt = DateTime.UtcNow
        };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    /// <summary>
    /// 回答问题
    /// </summary>
    public SessionState AnswerQuestion(string sessionId, string? summary = null)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return this;

        var newSession = session with
        {
            Phase = SessionPhase.Running,
            Summary = summary ?? session.Summary,
            QuestionPrompt = null,
            UpdatedAt = DateTime.UtcNow
        };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    /// <summary>
    /// 标记单个会话活跃
    /// </summary>
    public SessionState MarkSessionAlive(string sessionId)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return this;

        var newSession = session with { IsProcessAlive = true };
        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }

    /// <summary>
    /// 移除不可见会话
    /// </summary>
    public SessionState RemoveInvisibleSessions()
    {
        var visibleSessions = _sessionsById.Values.Where(s => s.IsVisibleInIsland);
        return new SessionState(visibleSessions);
    }

    /// <summary>
    /// 手动关闭会话
    /// </summary>
    public SessionState DismissSession(string sessionId)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return this;

        var newSession = session with
        {
            Phase = SessionPhase.Completed,
            AttachmentState = SessionAttachmentState.Detached,
            IsSessionEnded = true
        };

        return new SessionState(_sessionsById.SetItem(newSession.Id, newSession).Values);
    }
}
