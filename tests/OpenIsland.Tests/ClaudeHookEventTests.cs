using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;

namespace OpenIsland.Tests;

/// <summary>
/// 回归：bypass/auto 等非交互模式下，PreToolUse 不应在岛上拉起"等待审批"（橙色感叹号）。
/// 这些模式 Claude 自动放行、不存在人工审批，也就没有可靠的"解决"事件回来 → 橙卡会永久卡住。
/// 因此非 default 模式的 PreToolUse 必须被丢弃（返回 null），会话活动交给 transcript watcher。
/// </summary>
public class ClaudeHookEventTests
{
    private static ClaudeHookPayload Pre(string? mode) => new()
    {
        HookEventName = "PreToolUse",
        SessionId = "sess-1",
        ToolName = "Bash",
        PermissionMode = mode
    };

    [Fact]
    public void PreToolUse_DefaultMode_RaisesPermissionRequest()
        => Assert.IsType<PermissionRequested>(Pre("default").ToAgentEvent("claude"));

    // 缺失字段（旧版 Claude Code，只可能是 default）→ 保持原有审批行为。
    [Fact]
    public void PreToolUse_MissingMode_RaisesPermissionRequest()
        => Assert.IsType<PermissionRequested>(Pre(null).ToAgentEvent("claude"));

    // 关键修复：非交互模式下不得产生权限请求（否则橙色感叹号卡死）。
    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("acceptEdits")]
    [InlineData("dontAsk")]
    [InlineData("plan")]
    public void PreToolUse_NonDefaultMode_ProducesNoPermissionRequest(string mode)
    {
        var ev = Pre(mode).ToAgentEvent("claude");
        Assert.False(ev is PermissionRequested, $"mode={mode} 不应拉起权限请求");
    }

    // 大小写鲁棒。
    [Theory]
    [InlineData("BypassPermissions")]
    [InlineData("AUTO")]
    public void PreToolUse_NonDefaultMode_CaseInsensitive(string mode)
        => Assert.False(Pre(mode).ToAgentEvent("claude") is PermissionRequested);
}
