namespace OpenIsland.Core.Models;

/// <summary>
/// Claude会话元数据
/// </summary>
public record ClaudeMetadata
{
    public string? TranscriptPath { get; init; }
    public string? CurrentTool { get; init; }
    public string? Model { get; init; }
    public int ActiveSubagents { get; init; }
    public int ActiveTasks { get; init; }
    /// <summary>
    /// "cli" / "claude-desktop"。Jump 行为分流：cli → 开终端跑 claude --resume，
    /// claude-desktop → 激活桌面端窗口。
    /// </summary>
    public string? Entrypoint { get; init; }

    // Token用量信息
    public uint InputTokens { get; init; }
    public uint OutputTokens { get; init; }
    public uint CacheReadTokens { get; init; }
    public uint CacheCreationTokens { get; init; }
    public uint TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    // 费用信息
    public decimal TotalCost { get; init; }
}

/// <summary>
/// Codex会话元数据
/// </summary>
public record CodexMetadata
{
    public string? WorkspacePath { get; init; }
    public string? Model { get; init; }
    public int? TokensUsed { get; init; }
}

/// <summary>
/// Cursor会话元数据
/// </summary>
public record CursorMetadata
{
    public string? WorkspacePath { get; init; }
    public string? Model { get; init; }
}

/// <summary>
/// Gemini会话元数据
/// </summary>
public record GeminiMetadata
{
    public string? Model { get; init; }
    public string? ProjectId { get; init; }
}

/// <summary>
/// OpenCode会话元数据
/// </summary>
public record OpenCodeMetadata
{
    public string? WorkspacePath { get; init; }
    public string? Model { get; init; }
}
