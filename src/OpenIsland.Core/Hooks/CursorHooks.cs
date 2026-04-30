using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Cursor Hook Payload
/// </summary>
public record CursorHookPayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("workspace")]
    public string? WorkspacePath { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    public AgentEvent? ToAgentEvent()
    {
        var eventType = Type?.ToLowerInvariant();
        var sessionId = SessionId ?? $"cursor_{Guid.NewGuid():N}"[..12];
        var timestamp = DateTime.UtcNow;

        return eventType switch
        {
            "session_start" => new SessionStarted
            {
                SessionId = sessionId,
                Title = $"Cursor - {System.IO.Path.GetFileName(WorkspacePath) ?? "Composer"}",
                Tool = AgentTool.Cursor,
                Origin = SessionOrigin.Local,
                InitialPhase = SessionPhase.Running,
                Summary = Message ?? "Composer session started",
                Timestamp = timestamp,
                CursorMetadata = new CursorMetadata
                {
                    WorkspacePath = WorkspacePath,
                    Model = Model
                }
            },

            "session_end" => new SessionCompleted
            {
                SessionId = sessionId,
                Summary = Message ?? "Session ended",
                IsSessionEnd = true,
                Timestamp = timestamp
            },

            "activity" => new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = Message ?? "Processing...",
                Phase = SessionPhase.Running,
                Timestamp = timestamp
            },

            "metadata_update" => new CursorSessionMetadataUpdated
            {
                SessionId = sessionId,
                CursorMetadata = new CursorMetadata
                {
                    WorkspacePath = WorkspacePath,
                    Model = Model
                },
                Timestamp = timestamp
            },

            _ => null
        };
    }
}
