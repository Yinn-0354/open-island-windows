using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Gemini CLI Hook Payload
/// </summary>
public record GeminiHookPayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("working_dir")]
    public string? WorkingDirectory { get; init; }

    public AgentEvent? ToAgentEvent()
    {
        var eventType = EventType?.ToLowerInvariant();
        var sessionId = SessionId ?? $"gemini_{Guid.NewGuid():N}"[..12];
        var timestamp = DateTime.UtcNow;

        return eventType switch
        {
            "session_start" => new SessionStarted
            {
                SessionId = sessionId,
                Title = $"Gemini - {System.IO.Path.GetFileName(WorkingDirectory) ?? "Session"}",
                Tool = AgentTool.GeminiCLI,
                Origin = SessionOrigin.Local,
                InitialPhase = SessionPhase.Running,
                Summary = Message ?? "Session started",
                Timestamp = timestamp,
                JumpTarget = new JumpTarget { WorkingDirectory = WorkingDirectory },
                GeminiMetadata = new GeminiMetadata
                {
                    Model = Model,
                    ProjectId = ProjectId
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
                    ToolName = "gemini",
                    Description = Message ?? "Permission requested",
                    Timestamp = timestamp
                },
                Timestamp = timestamp
            },

            "metadata_update" => new GeminiSessionMetadataUpdated
            {
                SessionId = sessionId,
                GeminiMetadata = new GeminiMetadata
                {
                    Model = Model,
                    ProjectId = ProjectId
                },
                Timestamp = timestamp
            },

            _ => null
        };
    }
}
