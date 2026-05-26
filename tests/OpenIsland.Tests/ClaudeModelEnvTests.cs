using System.Text.Json.Nodes;
using OpenIsland.Core;

namespace OpenIsland.Tests;

public class ClaudeModelEnvTests
{
    private static JsonObject Parse(string j) => (JsonObject)JsonNode.Parse(j)!;

    private static ModelProfile ThirdParty(string baseUrl, string token, string model,
        string keyEnv = "ANTHROPIC_AUTH_TOKEN")
        => new()
        {
            Kind = ModelKind.ThirdParty, Name = "p",
            BaseUrl = baseUrl, ApiKey = token, Model = model, ApiKeyEnvName = keyEnv
        };

    [Fact]
    public void ApplyThirdParty_SetsEnvKeys_PreservesOtherSettingsAndEnv()
    {
        var root = Parse("""{ "model": "x", "env": { "FOO": "bar" } }""");

        ClaudeModelEnv.ApplyThirdParty(root, ThirdParty("https://api.deepseek.com/anthropic", "sk-1", "deepseek-chat"));

        var env = (JsonObject)root["env"]!;
        Assert.Equal("https://api.deepseek.com/anthropic", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("sk-1", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("deepseek-chat", (string?)env["ANTHROPIC_MODEL"]);
        Assert.Equal("bar", (string?)env["FOO"]);     // user's own env key preserved
        Assert.Equal("x", (string?)root["model"]);     // unrelated settings preserved
    }

    [Fact]
    public void ApplyThirdParty_SwitchingProvider_RemovesStaleManagedKeys()
    {
        // previous provider used ANTHROPIC_API_KEY; new one uses ANTHROPIC_AUTH_TOKEN.
        var root = Parse("""
        { "env": { "ANTHROPIC_BASE_URL": "https://old", "ANTHROPIC_API_KEY": "old", "ANTHROPIC_MODEL": "old-m" } }
        """);

        ClaudeModelEnv.ApplyThirdParty(root, ThirdParty("https://new", "new-tok", "new-m"));

        var env = (JsonObject)root["env"]!;
        Assert.Equal("https://new", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("new-tok", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("new-m", (string?)env["ANTHROPIC_MODEL"]);
        Assert.Null(env["ANTHROPIC_API_KEY"]); // stale key from old provider must be gone
    }

    [Fact]
    public void ApplyThirdParty_HonorsApiKeyEnvName()
    {
        var root = Parse("{}");
        ClaudeModelEnv.ApplyThirdParty(root, ThirdParty("https://x", "k", "m", keyEnv: "ANTHROPIC_API_KEY"));
        var env = (JsonObject)root["env"]!;
        Assert.Equal("k", (string?)env["ANTHROPIC_API_KEY"]);
        Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void ClearManaged_RemovesManagedKeys_PreservesUserEnv()
    {
        var root = Parse("""
        { "env": { "ANTHROPIC_BASE_URL": "https://x", "ANTHROPIC_MODEL": "m", "FOO": "bar" } }
        """);

        ClaudeModelEnv.ClearManaged(root);

        var env = (JsonObject)root["env"]!;
        Assert.Null(env["ANTHROPIC_BASE_URL"]);
        Assert.Null(env["ANTHROPIC_MODEL"]);
        Assert.Equal("bar", (string?)env["FOO"]);
    }

    [Fact]
    public void ClearManaged_RemovesEnvObjectWhenItBecomesEmpty()
    {
        var root = Parse("""{ "env": { "ANTHROPIC_MODEL": "m" } }""");
        ClaudeModelEnv.ClearManaged(root);
        Assert.Null(root["env"]);
    }

    [Fact]
    public void BuildModelCommand_ClaudeModel_ReturnsSlashModel()
    {
        var p = new ModelProfile { Kind = ModelKind.ClaudeModel, Name = "Opus", ClaudeModelSlug = "opus" };
        Assert.Equal("/model opus", ClaudeModelEnv.BuildModelCommand(p));
    }

    [Fact]
    public void BuildModelCommand_ThirdParty_ReturnsNull()
    {
        Assert.Null(ClaudeModelEnv.BuildModelCommand(ThirdParty("u", "t", "m")));
    }

    [Fact]
    public void SetOfficialModel_WithId_SetsOnlyModel_ClearsThirdPartyKeys()
    {
        var root = Parse("""
        { "env": { "ANTHROPIC_BASE_URL": "https://x", "ANTHROPIC_AUTH_TOKEN": "t", "ANTHROPIC_MODEL": "old", "FOO": "bar" } }
        """);
        ClaudeModelEnv.SetOfficialModel(root, "claude-opus-4-7");
        var env = (JsonObject)root["env"]!;
        Assert.Null(env["ANTHROPIC_BASE_URL"]);
        Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("claude-opus-4-7", (string?)env["ANTHROPIC_MODEL"]);
        Assert.Equal("bar", (string?)env["FOO"]); // user's own env key preserved
    }

    [Fact]
    public void SetOfficialModel_NullId_ClearsManagedAndEmptyEnv()
    {
        var root = Parse("""{ "env": { "ANTHROPIC_MODEL": "x" } }""");
        ClaudeModelEnv.SetOfficialModel(root, null);
        Assert.Null(root["env"]);
    }
}
