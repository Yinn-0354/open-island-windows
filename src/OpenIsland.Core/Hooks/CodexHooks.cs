using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Codex Hook Payload
/// </summary>
public record CodexHookPayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("workspace")]
    public string? WorkspacePath { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("tokens_used")]
    public int? TokensUsed { get; init; }

    [JsonPropertyName("terminal")]
    public string? Terminal { get; init; }

    public AgentEvent? ToAgentEvent()
    {
        var eventType = Event?.ToLowerInvariant();
        var sessionId = SessionId ?? Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTime.UtcNow;

        return eventType switch
        {
            "session_start" => new SessionStarted
            {
                SessionId = sessionId,
                Title = $"Codex - {System.IO.Path.GetFileName(WorkspacePath) ?? "Session"}",
                Tool = AgentTool.Codex,
                Origin = SessionOrigin.Local,
                InitialPhase = SessionPhase.Running,
                Summary = Message ?? "Session started",
                Timestamp = timestamp,
                JumpTarget = new JumpTarget { WorkingDirectory = WorkingDirectory, TerminalApp = Terminal },
                CodexMetadata = new CodexMetadata
                {
                    WorkspacePath = WorkspacePath,
                    Model = Model,
                    TokensUsed = TokensUsed
                }
            },

            "session_end" => new SessionCompleted
            {
                SessionId = sessionId,
                Summary = Message ?? "Session ended",
                IsSessionEnd = true,
                Timestamp = timestamp
            },

            "prompt" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = Message ?? "Processing...",
                Phase = SessionPhase.Running,
                Timestamp = timestamp
            },

            "response" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = Message ?? "Response received",
                Phase = SessionPhase.Idle,
                Timestamp = timestamp
            },

            "permission_request" => new PermissionRequested
            {
                SessionId = sessionId,
                Request = new PermissionRequest
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    ToolName = Data?.GetProperty("tool").GetString() ?? "unknown",
                    Description = Message ?? "Permission requested",
                    Timestamp = timestamp
                },
                Timestamp = timestamp
            },

            "metadata_update" => new SessionMetadataUpdated
            {
                SessionId = sessionId,
                CodexMetadata = new CodexMetadata
                {
                    WorkspacePath = WorkspacePath,
                    Model = Model,
                    TokensUsed = TokensUsed
                },
                Timestamp = timestamp
            },

            _ => null
        };
    }
}
