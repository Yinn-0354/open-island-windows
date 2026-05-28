using System.Collections.Immutable;

namespace OpenIsland.Core.Models;

/// <summary>
/// AI代理会话 - 核心模型
/// </summary>
public record AgentSession
{
    // 核心属性
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public AgentTool Tool { get; init; }
    public SessionPhase Phase { get; init; } = SessionPhase.Running;
    public SessionAttachmentState AttachmentState { get; init; } = SessionAttachmentState.Attached;
    public string Summary { get; init; } = "";
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public SessionOrigin Origin { get; init; } = SessionOrigin.Local;

    // 状态标志
    public bool IsRemote { get; init; }
    public bool IsHookManaged { get; init; }
    public bool IsCodexAppSession { get; init; }
    public bool IsSessionEnded { get; init; }
    public bool IsProcessAlive { get; init; } = true;

    // 可选元数据
    /// <summary>该会话所有待处理的权限请求（FIFO 队列）。支持并行 subagent 共享同一
    /// session_id 时的多个并发请求 —— 入队而非互相覆盖。</summary>
    public ImmutableList<PermissionRequest> PendingPermissions { get; init; } = ImmutableList<PermissionRequest>.Empty;

    /// <summary>队头：最早的待处理权限请求，无则 null。UI 与旧代码读这个即可
    /// （= 当前该处理的那个，对应终端里正在弹的提示）。</summary>
    public PermissionRequest? PermissionRequest => PendingPermissions.Count > 0 ? PendingPermissions[0] : null;
    public QuestionPrompt? QuestionPrompt { get; init; }
    public JumpTarget? JumpTarget { get; init; }

    // 工具特定元数据
    public ClaudeMetadata? ClaudeMetadata { get; init; }
    public CodexMetadata? CodexMetadata { get; init; }
    public CursorMetadata? CursorMetadata { get; init; }
    public GeminiMetadata? GeminiMetadata { get; init; }
    public OpenCodeMetadata? OpenCodeMetadata { get; init; }

    /// <summary>
    /// 是否需要用户关注（有权限请求或问题待回答）
    /// </summary>
    public bool NeedsAttention => Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer
        || PermissionRequest != null
        || QuestionPrompt != null;

    /// <summary>
    /// 是否仍在运行中
    /// </summary>
    public bool IsRunning => Phase != SessionPhase.Completed;

    /// <summary>
    /// 是否在Island中可见
    /// </summary>
    public bool IsVisibleInIsland => Phase != SessionPhase.Completed || AttachmentState == SessionAttachmentState.Attached;

    /// <summary>
    /// 创建会话的副本并更新指定字段。所有可空参数都使用 "?? 旧值" 的 patch 语义 ——
    /// 不传 = 保留旧值。这避免 ApplyActivityUpdated 这种"只想动 phase/summary"的调用
    /// 把 PermissionRequest/QuestionPrompt 顺带清空（曾经导致权限提示文字闪一下就消失，
    /// 因为 watcher 的 SessionActivityUpdated 一过来就用 With(summary, phase) 调用，
    /// 这两个字段被默认 null 写回去了）。
    /// 想显式清空 PermissionRequest/QuestionPrompt 的地方应该用 record `with {}` 直接赋值。
    /// </summary>
    public AgentSession With(
        string? title = null,
        SessionPhase? phase = null,
        string? summary = null,
        QuestionPrompt? questionPrompt = null,
        JumpTarget? jumpTarget = null,
        SessionAttachmentState? attachmentState = null,
        bool? isProcessAlive = null)
    {
        return this with
        {
            Title = title ?? Title,
            Phase = phase ?? Phase,
            Summary = summary ?? Summary,
            QuestionPrompt = questionPrompt ?? QuestionPrompt,
            JumpTarget = jumpTarget ?? JumpTarget,
            AttachmentState = attachmentState ?? AttachmentState,
            IsProcessAlive = isProcessAlive ?? IsProcessAlive,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
