namespace OpenIsland.Core;

/// <summary>模型档案的种类。</summary>
public enum ModelKind
{
    /// <summary>官方 Claude 模型（Opus/Sonnet/Haiku…）——通过注入 /model 实时切换，CLI 与 Desktop 都生效。</summary>
    ClaudeModel,

    /// <summary>第三方 provider（DeepSeek/Kimi/GLM…）——通过写 ~/.claude/settings.json 的 env 块，仅对新 CLI 会话生效。</summary>
    ThirdParty
}

/// <summary>
/// 一个可切换的"模型档案"。默认内置一个官方 Claude 档案；用户可在控制中心添加自己的第三方 provider。
/// </summary>
public record ModelProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ModelKind Kind { get; init; }

    /// <summary>ClaudeModel 用：/model 的参数（如 "opus"/"sonnet"/"haiku"）。空表示用 Claude 默认。</summary>
    public string? ClaudeModelSlug { get; init; }

    // ── ThirdParty 用：写进 settings.json env 的字段 ──
    public string? BaseUrl { get; init; }                       // ANTHROPIC_BASE_URL
    public string ApiKeyEnvName { get; init; } = "ANTHROPIC_AUTH_TOKEN"; // 或 ANTHROPIC_API_KEY
    public string? ApiKey { get; init; }                        // 上述 env 名对应的 token
    public string? Model { get; init; }                         // ANTHROPIC_MODEL
    public string? HaikuModel { get; init; }                    // ANTHROPIC_DEFAULT_HAIKU_MODEL
    public string? SonnetModel { get; init; }                   // ANTHROPIC_DEFAULT_SONNET_MODEL
    public string? OpusModel { get; init; }                     // ANTHROPIC_DEFAULT_OPUS_MODEL

    /// <summary>内置不可删除的官方 Claude 档案的固定 Id。</summary>
    public const string OfficialClaudeId = "claude-official";
}
