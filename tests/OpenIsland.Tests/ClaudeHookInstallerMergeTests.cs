using System.Text.Json.Nodes;
using OpenIsland.Core.Hooks.HookInstallers;

namespace OpenIsland.Tests;

public class ClaudeHookInstallerMergeTests
{
    private const string OurCommand = "C:/app/open-island-hooks.exe --source claude";

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void MergeHookInstall_PreservesUnrelatedTopLevelKeysAndOtherEvents()
    {
        var root = Parse("""
        {
          "model": "claude-x",
          "hooks": {
            "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "user-own.exe" } ] } ]
          }
        }
        """);

        ClaudeHookInstaller.MergeHookInstall(root, new[] { "PreToolUse", "Stop" }, OurCommand);

        Assert.Equal("claude-x", root["model"]!.GetValue<string>());          // unrelated key kept
        Assert.NotNull(root["hooks"]!["UserPromptSubmit"]);                    // user's event kept
        Assert.NotNull(root["hooks"]!["PreToolUse"]);                         // our events added
        Assert.NotNull(root["hooks"]!["Stop"]);
        var cmd = root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Contains("open-island-hooks", cmd);
    }

    [Fact]
    public void MergeHookInstall_KeepsUserEntryOnManagedEvent_AndIsIdempotent()
    {
        var root = Parse("""
        {
          "hooks": {
            "PreToolUse": [ { "hooks": [ { "type": "command", "command": "user-pre.exe" } ] } ]
          }
        }
        """);

        ClaudeHookInstaller.MergeHookInstall(root, new[] { "PreToolUse" }, OurCommand);
        ClaudeHookInstaller.MergeHookInstall(root, new[] { "PreToolUse" }, OurCommand); // re-install

        var arr = (JsonArray)root["hooks"]!["PreToolUse"]!;
        Assert.Equal(2, arr.Count); // user's entry + exactly one of ours (no duplicate)
    }

    [Fact]
    public void RemoveHookInstall_RemovesOnlyOpenIslandEntries_KeepsUserHooks()
    {
        var root = Parse("""
        {
          "hooks": {
            "PreToolUse": [
              { "hooks": [ { "type": "command", "command": "user-pre.exe" } ] },
              { "hooks": [ { "type": "command", "command": "C:/app/open-island-hooks.exe --source claude" } ] }
            ],
            "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "user-ups.exe" } ] } ]
          }
        }
        """);

        ClaudeHookInstaller.RemoveHookInstall(root);

        var arr = (JsonArray)root["hooks"]!["PreToolUse"]!;
        Assert.Single(arr);
        Assert.Contains("user-pre.exe", arr[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.NotNull(root["hooks"]!["UserPromptSubmit"]); // unrelated event preserved
    }

    [Fact]
    public void RemoveHookInstall_DropsEmptiedEventAndHooksObjectWhenOnlyOurs()
    {
        var root = Parse("""
        {
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "open-island-hooks.exe --source claude" } ] } ]
          }
        }
        """);

        ClaudeHookInstaller.RemoveHookInstall(root);

        Assert.Null(root["hooks"]); // hooks object removed because nothing else remained
    }
}
