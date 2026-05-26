using System.Text.Json.Nodes;

namespace OpenIsland.Core;

/// <summary>
/// 把模型档案落到 Claude Code 的配置上。
///
/// - 第三方 provider：写 ~/.claude/settings.json 顶层 `env` 块（ANTHROPIC_BASE_URL / token /
///   ANTHROPIC_MODEL / 角色模型），Claude 启动时读取 —— 对新 CLI 会话生效。切换前先清掉所有
///   "受管键"，避免上一个 provider 的残留键（如旧的 ANTHROPIC_API_KEY）和新值冲突。保留用户
///   自己的其它 env 键和其它 settings 字段。
/// - 官方 Claude：清掉受管键即可（空 env 等于回到 Claude 自带登录）。
/// - Claude 模型间切换：不走 env，用 /model 注入（见 BuildModelCommand）。
/// </summary>
public static class ClaudeModelEnv
{
    /// <summary>OpenIsland 负责写/清的 env 键集合（切换时整组覆盖，避免残留冲突）。</summary>
    public static readonly string[] ManagedEnvKeys =
    {
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL"
    };

    /// <summary>把第三方 provider 的 env 写进 settings DOM（返回同一个被修改的 root）。</summary>
    public static JsonObject ApplyThirdParty(JsonObject root, ModelProfile profile)
    {
        if (root["env"] is not JsonObject env)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        // 先整组清掉受管键，再写本档案的值 —— 保证切换干净，不留上一个 provider 的残留键。
        RemoveManaged(env);

        if (!string.IsNullOrEmpty(profile.BaseUrl))
            env["ANTHROPIC_BASE_URL"] = profile.BaseUrl;

        var keyName = string.IsNullOrEmpty(profile.ApiKeyEnvName)
            ? "ANTHROPIC_AUTH_TOKEN"
            : profile.ApiKeyEnvName;
        if (!string.IsNullOrEmpty(profile.ApiKey))
            env[keyName] = profile.ApiKey;

        if (!string.IsNullOrEmpty(profile.Model))
            env["ANTHROPIC_MODEL"] = profile.Model;
        if (!string.IsNullOrEmpty(profile.HaikuModel))
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = profile.HaikuModel;
        if (!string.IsNullOrEmpty(profile.SonnetModel))
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = profile.SonnetModel;
        if (!string.IsNullOrEmpty(profile.OpusModel))
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = profile.OpusModel;

        return root;
    }

    /// <summary>清掉所有受管 env 键（回到官方 Claude）；env 变空则移除 env 对象。</summary>
    public static JsonObject ClearManaged(JsonObject root)
    {
        if (root["env"] is JsonObject env)
        {
            RemoveManaged(env);
            if (env.Count == 0)
                root.Remove("env");
        }
        return root;
    }

    /// <summary>ClaudeModel 档案 → "/model &lt;slug&gt;" 命令；第三方或无 slug → null。</summary>
    public static string? BuildModelCommand(ModelProfile profile)
    {
        if (profile.Kind != ModelKind.ClaudeModel)
            return null;
        return string.IsNullOrWhiteSpace(profile.ClaudeModelSlug)
            ? null
            : $"/model {profile.ClaudeModelSlug.Trim()}";
    }

    private static void RemoveManaged(JsonObject env)
    {
        foreach (var key in ManagedEnvKeys)
            env.Remove(key);
    }
}
