namespace OpenIsland.Core.Models;

/// <summary>
/// 支持的AI代理工具类型
/// </summary>
public enum AgentTool
{
    ClaudeCode,
    Codex,
    CodexApp,
    Cursor,
    GeminiCLI,
    KimiCLI,
    OpenCode,
    Qoder,
    QwenCode,
    Factory,
    CodeBuddy,
    Droid
}

/// <summary>
/// 会话阶段
/// </summary>
public enum SessionPhase
{
    Running,
    WaitingForApproval,
    WaitingForAnswer,
    Completed,
    Idle
}

/// <summary>
/// 会话附着状态
/// </summary>
public enum SessionAttachmentState
{
    Attached,
    Stale,
    Detached
}

/// <summary>
/// 会话来源
/// </summary>
public enum SessionOrigin
{
    Local,
    Remote,
    Demo
}
