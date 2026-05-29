using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Claude Code Hook事件类型
/// </summary>
public enum ClaudeHookEventName
{
    SessionStart,
    SessionEnd,
    UserPromptSubmit,
    PreToolUse,
    PostToolUse,
    PostToolUseFailure,
    PermissionRequest,
    PermissionDenied,
    Notification,
    Stop,
    StopFailure,
    SubagentStart,
    SubagentStop,
    PreCompact
}

/// <summary>
/// Claude Hook Payload
/// </summary>
public record ClaudeHookPayload
{
    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    /// <summary>会话当前权限模式：default / plan / acceptEdits / auto / dontAsk / bypassPermissions。
    /// 决定 PreToolUse hook 是否应强制弹询问（仅 default 才强制，见 ClaudeHookPolicy）。</summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public JsonElement? ToolInput { get; init; }

    /// <summary>Claude Code 给每次工具调用的唯一 id。并行 subagent 共享同一 session_id 时，
    /// 用它区分 / 去重同会话的多个并发权限请求（队列的稳定键）。</summary>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("tool_response")]
    public JsonElement? ToolResponse { get; init; }

    [JsonPropertyName("permission_suggestions")]
    public List<string>? PermissionSuggestions { get; init; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("terminal_app")]
    public string? TerminalApp { get; init; }

    [JsonPropertyName("terminal_session_id")]
    public string? TerminalSessionId { get; init; }

    [JsonPropertyName("warp_pane_uuid")]
    public string? WarpPaneUuid { get; init; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("active_subagents")]
    public int? ActiveSubagents { get; init; }

    [JsonPropertyName("active_tasks")]
    public int? ActiveTasks { get; init; }

    /// <summary>
    /// 转换为AgentEvent
    /// </summary>
    public AgentEvent? ToAgentEvent(string source)
    {
        // Normalize 兼容 Claude Code 实际写 PascalCase（"PreToolUse"）和 docs 里的
        // snake_case（"pre_tool_use"）：删 underscore + lowercase。
        var eventName = HookEventName?.Replace("_", "").ToLowerInvariant();
        var sessionId = SessionId ?? "unknown";
        var timestamp = DateTime.UtcNow;

        return eventName switch
        {
            "sessionstart" => new SessionStarted
            {
                SessionId = sessionId,
                Title = Message ?? (WorkingDirectory != null ? Path.GetFileName(WorkingDirectory) : null) ?? "Claude Session",
                Tool = MapSourceToTool(source),
                Origin = SessionOrigin.Local,
                InitialPhase = SessionPhase.Running,
                Summary = "Session started",
                Timestamp = timestamp,
                JumpTarget = CreateJumpTarget(),
                ClaudeMetadata = CreateMetadata()
            },

            "sessionend" => new SessionCompleted
            {
                SessionId = sessionId,
                Summary = Message ?? "Session ended",
                IsSessionEnd = true,
                Timestamp = timestamp
            },

            "userpromptsubmit" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = Prompt ?? "User input",
                Phase = SessionPhase.Running,
                Timestamp = timestamp,
                Title = Prompt // first user message becomes session title
            },

            "permissionrequest" => new PermissionRequested
            {
                SessionId = sessionId,
                Request = new PermissionRequest
                {
                    Id = ToolUseId ?? Guid.NewGuid().ToString("N")[..8],
                    ToolName = ToolName ?? "unknown",
                    Description = Message ?? $"{ToolName}: {ToolInput}",
                    ToolInput = ToolInput?.Deserialize<Dictionary<string, object>>(),
                    Suggestions = PermissionSuggestions,
                    SuggestedAlwaysAllow = BuildAllowRule(ToolName, ToolInput),
                    Timestamp = timestamp
                },
                Timestamp = timestamp
            },

            // PreToolUse 只在 default（普通）模式拉起"等待审批"橙卡；bypass/auto/acceptEdits/
            // dontAsk/plan 等非交互模式下 Claude 自动放行、无人工审批，也就没有可靠的解决事件回来，
            // 若仍拉起橙卡会永久卡住（感叹号下不去）。这些模式直接丢弃 PreToolUse（返回 null），
            // 会话的 Running/Idle 全交给 transcript watcher 驱动。与 hook 端的 ask 抑制同一策略。
            "pretooluse" when !ClaudeHookPolicy.ShouldForceAsk(PermissionMode) => null,

            "pretooluse" => new PermissionRequested
            {
                SessionId = sessionId,
                Request = new PermissionRequest
                {
                    Id = ToolUseId ?? Guid.NewGuid().ToString("N")[..8],
                    ToolName = ToolName ?? "unknown",
                    Description = $"{ToolName}: {SummarizeToolInput(ToolName, ToolInput)}",
                    ToolInput = ToolInput?.Deserialize<Dictionary<string, object>>(),
                    SuggestedAlwaysAllow = BuildAllowRule(ToolName, ToolInput),
                    Timestamp = timestamp
                },
                Timestamp = timestamp
            },

            "posttooluse" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = $"{ToolName} completed",
                Phase = SessionPhase.Running,
                Timestamp = timestamp
            },

            "stop" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = "Ready for input",
                Phase = SessionPhase.Idle,
                Timestamp = timestamp
            },

            _ => null
        };
    }

    private JumpTarget? CreateJumpTarget()
    {
        if (string.IsNullOrEmpty(TerminalApp) && string.IsNullOrEmpty(WorkingDirectory))
            return null;

        return new JumpTarget
        {
            TerminalApp = TerminalApp,
            TerminalSessionId = TerminalSessionId,
            WorkingDirectory = WorkingDirectory,
            WindowTitle = Message
        };
    }

    private ClaudeMetadata? CreateMetadata()
    {
        if (string.IsNullOrEmpty(Model) && string.IsNullOrEmpty(TranscriptPath))
            return null;

        return new ClaudeMetadata
        {
            TranscriptPath = TranscriptPath,
            Model = Model,
            ActiveSubagents = ActiveSubagents ?? 0,
            ActiveTasks = ActiveTasks ?? 0
        };
    }

    /// <summary>
    /// 根据 tool_name + tool_input 推断"一直允许"建议规则。
    /// WebFetch 按 url 域名拆分（"WebFetch(domain:linux.do)"），其它 tool 一律 tool 级（"Bash" 等）。
    /// </summary>
    private static AllowRule? BuildAllowRule(string? toolName, JsonElement? toolInput)
    {
        if (string.IsNullOrEmpty(toolName)) return null;

        if (string.Equals(toolName, "WebFetch", StringComparison.OrdinalIgnoreCase) && toolInput is JsonElement ti)
        {
            if (ti.ValueKind == JsonValueKind.Object && ti.TryGetProperty("url", out var urlElem))
            {
                var url = urlElem.GetString();
                if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return new AllowRule { ToolName = "WebFetch", Pattern = $"domain:{uri.Host}" };
            }
        }

        return new AllowRule { ToolName = toolName };
    }

    /// <summary>
    /// 把 tool_input 缩成一行人话用作权限弹窗的描述。
    /// </summary>
    private static string SummarizeToolInput(string? toolName, JsonElement? toolInput)
    {
        if (toolInput is not JsonElement ti || ti.ValueKind != JsonValueKind.Object) return string.Empty;
        if (string.Equals(toolName, "WebFetch", StringComparison.OrdinalIgnoreCase) && ti.TryGetProperty("url", out var u))
            return u.GetString() ?? "";
        if (string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase) && ti.TryGetProperty("command", out var c))
            return c.GetString() ?? "";
        if (ti.TryGetProperty("file_path", out var fp))
            return fp.GetString() ?? "";
        if (ti.TryGetProperty("path", out var p))
            return p.GetString() ?? "";
        return ti.GetRawText().Length > 100 ? ti.GetRawText()[..100] + "…" : ti.GetRawText();
    }

    private static AgentTool MapSourceToTool(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "claude" => AgentTool.ClaudeCode,
            "qoder" => AgentTool.Qoder,
            "qwen" => AgentTool.QwenCode,
            "factory" => AgentTool.Factory,
            "codebuddy" => AgentTool.CodeBuddy,
            "droid" => AgentTool.Droid,
            "kimi" => AgentTool.KimiCLI,
            _ => AgentTool.ClaudeCode
        };
    }
}

/// <summary>
/// Claude Hook响应指令
/// </summary>
public record ClaudeHookDirective
{
    [JsonPropertyName("pre_tool_use")]
    public ClaudePreToolUseDirective? PreToolUse { get; init; }

    [JsonPropertyName("permission_request")]
    public ClaudePermissionRequestDecision? PermissionRequest { get; init; }
}

public record ClaudePreToolUseDirective
{
    [JsonPropertyName("reject")]
    public bool? Reject { get; init; }

    [JsonPropertyName("tool_input")]
    public JsonElement? ToolInput { get; init; }
}

public record ClaudePermissionRequestDecision
{
    [JsonPropertyName("approve")]
    public bool? Approve { get; init; }

    [JsonPropertyName("update")]
    public ClaudePermissionUpdate? Update { get; init; }
}

public record ClaudePermissionUpdate
{
    [JsonPropertyName("add_rules")]
    public List<PermissionRuleDef>? AddRules { get; init; }

    [JsonPropertyName("replace_rules")]
    public List<PermissionRuleDef>? ReplaceRules { get; init; }

    [JsonPropertyName("remove_rules")]
    public List<PermissionRuleDef>? RemoveRules { get; init; }

    [JsonPropertyName("set_mode")]
    public string? SetMode { get; init; } // "acceptEdits", "plan", "auto"

    [JsonPropertyName("add_directories")]
    public List<string>? AddDirectories { get; init; }

    [JsonPropertyName("remove_directories")]
    public List<string>? RemoveDirectories { get; init; }
}

public record PermissionRuleDef
{
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }
}
