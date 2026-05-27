namespace OpenIsland.Core;

/// <summary>
/// 内置模型预设（参考 cc-switch 的 providerPresets）。
/// - BuiltInClaude：Claude 官方 + Opus/Sonnet/Haiku 快捷档，始终可用，切换走 /model（实时）。
/// - ThirdParty：第三方 provider 模板，已预填地址/默认模型，用户只需在控制中心填 API key 即可，
///   不用再手输地址。只收录 *Anthropic API 兼容*（base_url 直连即可、无需格式转换代理）的端点。
/// </summary>
public static class ModelPresets
{
    public static IReadOnlyList<ModelProfile> BuiltInClaude { get; } = new[]
    {
        new ModelProfile { Id = ModelProfile.OfficialClaudeId, Name = "Claude（官方）", Kind = ModelKind.ClaudeModel },
        new ModelProfile { Id = "claude-opus",   Name = "Claude Opus",   Kind = ModelKind.ClaudeModel, ClaudeModelSlug = "opus",   Model = "claude-opus-4-7" },
        new ModelProfile { Id = "claude-sonnet", Name = "Claude Sonnet", Kind = ModelKind.ClaudeModel, ClaudeModelSlug = "sonnet", Model = "claude-sonnet-4-6" },
        new ModelProfile { Id = "claude-haiku",  Name = "Claude Haiku",  Kind = ModelKind.ClaudeModel, ClaudeModelSlug = "haiku",  Model = "claude-haiku-4-5-20251001" },
    };

    /// <summary>第三方 provider 模板（ApiKey 留空，用户在控制中心填）。</summary>
    public static IReadOnlyList<ModelProfile> ThirdParty { get; } = new[]
    {
        Preset("deepseek",    "DeepSeek",            "https://api.deepseek.com/anthropic",            "deepseek-v4-pro", haiku: "deepseek-v4-flash"),
        // 注意：不收录 Gemini —— Google generativelanguage 只提供 OpenAI / 原生 Gemini 兼容层，
        // 没有 Anthropic Messages API（/v1/messages），直连 ANTHROPIC_BASE_URL 会失败，需翻译代理。
        Preset("glm",         "智谱 GLM",            "https://open.bigmodel.cn/api/anthropic",        "glm-5"),
        Preset("glm-zai",     "智谱 GLM (z.ai)",     "https://api.z.ai/api/anthropic",                "glm-5"),
        Preset("kimi",        "Kimi (Moonshot)",     "https://api.moonshot.cn/anthropic",             "kimi-k2.6"),
        Preset("minimax",     "MiniMax",             "https://api.minimaxi.com/anthropic",            "MiniMax-M2.7"),
        Preset("qwen",        "通义千问 (Coding)",   "https://coding.dashscope.aliyuncs.com/apps/anthropic", null),
        Preset("openrouter",  "OpenRouter",          "https://openrouter.ai/api",                     "anthropic/claude-sonnet-4.6", haiku: "anthropic/claude-haiku-4.5", sonnet: "anthropic/claude-sonnet-4.6", opus: "anthropic/claude-opus-4.7"),
        Preset("siliconflow", "硅基流动 SiliconFlow", "https://api.siliconflow.cn",                   "Pro/MiniMaxAI/MiniMax-M2.7"),
        Preset("novita",      "Novita AI",           "https://api.novita.ai/anthropic",               "zai-org/glm-5"),
        Preset("modelscope",  "魔搭 ModelScope",     "https://api-inference.modelscope.cn",           "ZhipuAI/GLM-5"),
        Preset("xiaomi-mimo", "小米 MiMo",           "https://api.xiaomimimo.com/anthropic",          "mimo-v2.5-pro"),
    };

    private static ModelProfile Preset(string id, string name, string baseUrl, string? model,
        string? haiku = null, string? sonnet = null, string? opus = null,
        string keyEnv = "ANTHROPIC_AUTH_TOKEN")
        => new()
        {
            Id = "preset-" + id,
            Name = name,
            Kind = ModelKind.ThirdParty,
            BaseUrl = baseUrl,
            ApiKeyEnvName = keyEnv,
            Model = model,
            HaikuModel = haiku,
            SonnetModel = sonnet,
            OpusModel = opus,
        };
}
