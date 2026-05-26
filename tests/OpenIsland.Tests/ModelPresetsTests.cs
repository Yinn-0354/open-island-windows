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
    public void Gemini_UsesApiKeyEnvName()
    {
        var gemini = ModelPresets.ThirdParty.Single(p => p.Id == "preset-gemini");
        Assert.Equal("ANTHROPIC_API_KEY", gemini.ApiKeyEnvName);
    }
}
