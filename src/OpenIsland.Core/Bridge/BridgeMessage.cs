using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenIsland.Core.Bridge;

/// <summary>
/// 桥接消息基类
/// </summary>
public abstract record BridgeMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

// 客户端 → 服务器 的消息
public record RegisterClientMessage : BridgeMessage
{
    public override string Type => "registerClient";

    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("clientType")]
    public required string ClientType { get; init; } // "app", "hooks", "watch"
}

public record ProcessHookMessage : BridgeMessage
{
    public override string Type => "processHook";

    [JsonPropertyName("source")]
    public required string Source { get; init; } // "claude", "codex", "cursor", etc.

    [JsonPropertyName("eventData")]
    public required JsonElement EventData { get; init; }
}

public record ResolvePermissionMessage : BridgeMessage
{
    public override string Type => "resolvePermission";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    [JsonPropertyName("rules")]
    public List<PermissionRule>? Rules { get; init; }
}

public record AnswerQuestionMessage : BridgeMessage
{
    public override string Type => "answerQuestion";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("answer")]
    public required string Answer { get; init; }
}

public record RequestQuestionMessage : BridgeMessage
{
    public override string Type => "requestQuestion";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}

// 服务器 → 客户端 的消息
public record AcknowledgedMessage : BridgeMessage
{
    public override string Type => "acknowledged";

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public record HookDirectiveMessage : BridgeMessage
{
    public override string Type => "hookDirective";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("directive")]
    public required JsonElement Directive { get; init; }
}

public record PermissionRule
{
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; } // "allow", "deny", "ask"

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>
/// 消息编解码器
/// </summary>
public static class BridgeCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 编码消息为JSON行
    /// </summary>
    public static string Encode(BridgeMessage message)
    {
        // 关键：必须显式传入 runtime type，否则 System.Text.Json 只会序列化
        // BridgeMessage 基类上声明的属性，把所有派生字段（Source/EventData/
        // ClientId/...）静默丢掉——服务端解码到必填字段缺失，整个 hook 通道失效。
        return JsonSerializer.Serialize(message, message.GetType(), JsonOptions) + "\n";
    }

    /// <summary>
    /// 解码JSON行为消息
    /// </summary>
    public static BridgeMessage? Decode(string jsonLine)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonLine);
            var type = doc.RootElement.GetProperty("type").GetString();

            return type switch
            {
                "registerClient" => JsonSerializer.Deserialize<RegisterClientMessage>(jsonLine, JsonOptions),
                "processHook" => JsonSerializer.Deserialize<ProcessHookMessage>(jsonLine, JsonOptions),
                "resolvePermission" => JsonSerializer.Deserialize<ResolvePermissionMessage>(jsonLine, JsonOptions),
                "answerQuestion" => JsonSerializer.Deserialize<AnswerQuestionMessage>(jsonLine, JsonOptions),
                "requestQuestion" => JsonSerializer.Deserialize<RequestQuestionMessage>(jsonLine, JsonOptions),
                "acknowledged" => JsonSerializer.Deserialize<AcknowledgedMessage>(jsonLine, JsonOptions),
                "hookDirective" => JsonSerializer.Deserialize<HookDirectiveMessage>(jsonLine, JsonOptions),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
