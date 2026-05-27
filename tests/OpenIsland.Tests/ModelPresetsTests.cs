using OpenIsland.Core;

namespace OpenIsland.Tests;

public class ModelPresetsTests
{
    [Fact]
    public void BuiltInClaude_ContainsOfficialAndShortcuts()
    {
        var ids = ModelPresets.BuiltInClaude.Select(p => p.Id).ToList();
        Assert.Contains(ModelProfile.OfficialClaudeId, ids);
        Assert.All(ModelPresets.BuiltInClaude, p => Assert.Equal(ModelKind.ClaudeModel, p.Kind));
        // official has no slug (Claude default); the others do
        var official = ModelPresets.BuiltInClaude.Single(p => p.Id == ModelProfile.OfficialClaudeId);
        Assert.True(string.IsNullOrEmpty(official.ClaudeModelSlug));
    }

    [Fact]
    public void ThirdPartyPresets_AllHaveBaseUrlAndUniqueId_AndAreThirdParty()
    {
        Assert.NotEmpty(ModelPresets.ThirdParty);
        Assert.All(ModelPresets.ThirdParty, p =>
        {
            Assert.Equal(ModelKind.ThirdParty, p.Kind);
            Assert.False(string.IsNullOrWhiteSpace(p.BaseUrl), $"{p.Name} missing base url");
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(string.IsNullOrEmpty(p.ApiKey), $"{p.Name} preset must ship without a key");
        });
        var ids = ModelPresets.ThirdParty.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Presets_DoNotIncludeGemini_WhichLacksAnthropicEndpoint()
    {
        // Gemini（generativelanguage）没有 Anthropic Messages API，直连 ANTHROPIC_BASE_URL 会失败，
        // 已从预设移除；此处防回归，避免有人误加回去。
        Assert.DoesNotContain(ModelPresets.ThirdParty, p => p.Id == "preset-gemini");
        Assert.DoesNotContain(ModelPresets.ThirdParty,
            p => p.BaseUrl != null && p.BaseUrl.Contains("generativelanguage"));
    }

    [Fact]
    public void ThirdPartyPresets_AllUseAuthTokenEnv()
    {
        // 移除 Gemini（曾是唯一用 ANTHROPIC_API_KEY 的预设）后，其余预设均走默认 ANTHROPIC_AUTH_TOKEN。
        Assert.All(ModelPresets.ThirdParty, p => Assert.Equal("ANTHROPIC_AUTH_TOKEN", p.ApiKeyEnvName));
    }
}
