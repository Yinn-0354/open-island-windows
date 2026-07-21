namespace OpenIsland.Core.Models;

/// <summary>
/// 代理事件类型
/// </summary>
public abstract record AgentEvent
{
    public abstract string EventType { get; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public record SessionStarted : AgentEvent
{
    public override string EventType => "sessionStarted";
    public required string SessionId { get; init; }
    public string Title { get; init; } = "";
    public AgentTool Tool { get; init; }
    public SessionOrigin Origin { get; init; }
    public SessionPhase InitialPhase { get; init; } = SessionPhase.Running;
    public string Summary { get; init; } = "";
    public JumpTarget? JumpTarget { get; init; }
    public bool IsRemote { get; init; }

    public ClaudeMetadata? ClaudeMetadata { get; init; }
    public CodexMetadata? CodexMetadata { get; init; }
    public CursorMetadata? CursorMetadata { get; init; }
    public GeminiMetadata? GeminiMetadata { get; init; }
    public OpenCodeMetadata? OpenCodeMetadata { get; init; }
}

public record SessionActivityUpdated : AgentEvent
{
    public override string EventType => "activityUpdated";
    public required string SessionId { get; init; }
    public string Summary { get; init; } = "";
    public SessionPhase Phase { get; init; }
    public string? Title { get; init; }
    /// <summary>
    /// Watcher 看到的 transcript 末条消息的实际写入时间（不是 watcher emit 此事件的时间）。
    /// 用来判断 transcript 是否在 PermissionRequest 之后真有推进 —— 推进了说明用户已经
    /// 在终端答完了 tool/question，可以解锁岛上的 WaitingForApproval/WaitingForAnswer。
    /// </summary>
    public DateTime? LastTranscriptTimestamp { get; init; }

    /// <summary>
    /// 是否是 AskUserQuestion 工具完成事件（PostToolUse + tool_name=="AskUserQuestion"）。
    /// R2 兜底：用户在终端手动答完所有问题时（没点灵动岛按钮），AnswerQuestionAsync 不会被调，
    /// 灵动岛无法主动 ResolvePermission 清卡；这时 Claude 会发 PostToolUse AskUserQuestion，
    /// 在 SessionActivityUpdated 上带这个标志，SessionState 看到就清 PendingPermissions。
    /// 多问题场景下 W2 已经关掉了 watcher 推进检测的清卡路径，这里补上权威清卡：
    /// "AskUserQuestion 工具完成 = 用户已答完所有问题"是 Claude 亲自发的信号，最可靠。
    /// </summary>
    public bool CompletedAskUserQuestion { get; init; }
}

public record PermissionRequested : AgentEvent
{
    public override string EventType => "permissionRequested";
    public required string SessionId { get; init; }
    public required PermissionRequest Request { get; init; }
}

public record QuestionAsked : AgentEvent
{
    public override string EventType => "questionAsked";
    public required string SessionId { get; init; }
    public required QuestionPrompt Prompt { get; init; }
}

public record SessionCompleted : AgentEvent
{
    public override string EventType => "sessionCompleted";
    public required string SessionId { get; init; }
    public string Summary { get; init; } = "";
    public bool IsInterrupt { get; init; }
    public bool IsSessionEnd { get; init; }
}

public record JumpTargetUpdated : AgentEvent
{
    public override string EventType => "jumpTargetUpdated";
    public required string SessionId { get; init; }
    public required JumpTarget JumpTarget { get; init; }
}

public record SessionMetadataUpdated : AgentEvent
{
    public override string EventType => "sessionMetadataUpdated";
    public required string SessionId { get; init; }
    public CodexMetadata? CodexMetadata { get; init; }
}

public record ClaudeSessionMetadataUpdated : AgentEvent
{
    public override string EventType => "claudeSessionMetadataUpdated";
    public required string SessionId { get; init; }
    public ClaudeMetadata? ClaudeMetadata { get; init; }
}

public record CursorSessionMetadataUpdated : AgentEvent
{
    public override string EventType => "cursorSessionMetadataUpdated";
    public required string SessionId { get; init; }
    public CursorMetadata? CursorMetadata { get; init; }
}

public record GeminiSessionMetadataUpdated : AgentEvent
{
    public override string EventType => "geminiSessionMetadataUpdated";
    public required string SessionId { get; init; }
    public GeminiMetadata? GeminiMetadata { get; init; }
}

public record OpenCodeSessionMetadataUpdated : AgentEvent
{
    public override string EventType => "openCodeSessionMetadataUpdated";
    public required string SessionId { get; init; }
    public OpenCodeMetadata? OpenCodeMetadata { get; init; }
}

public record ActionableStateResolved : AgentEvent
{
    public override string EventType => "actionableStateResolved";
    public required string SessionId { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// 带延迟调度的事件
/// </summary>
public record ScheduledAgentEvent
{
    public required TimeSpan Delay { get; init; }
    public required AgentEvent Event { get; init; }
}
