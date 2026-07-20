using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;

namespace OpenIsland.Tests;

/// <summary>
/// 回归：bypass/auto 等非交互模式下，普通工具（Bash/Write/Edit 等）的 PreToolUse 不应在岛上
/// 拉起"等待审批"（橙色感叹号）。这些模式 Claude 自动放行、不存在人工审批，也就没有可靠的
/// "解决"事件回来 → 橙卡会永久卡住。因此非 default 模式的普通工具 PreToolUse 必须被丢弃
/// （返回 null），会话活动交给 transcript watcher。
///
/// 例外：AskUserQuestion / ExitPlanMode 在任何权限模式下都必须拉橙卡 —— 见 ClaudeHookPolicy
/// 的特例逻辑。Claude 协议要求即使 auto/bypass 也必须到达用户回调，灵动岛镜像符合语义。
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

    // 洞 1 修复回归：AskUserQuestion 在任何权限模式下都必须拉橙卡。
    // Claude 协议（Agent SDK 文档 user-input）明确要求"即使允许规则匹配时也会到达用户回调"——
    // auto/bypass 模式下普通工具自动放行，但 AskUserQuestion 必须停下来等用户回答。
    // 任何模式拉起橙卡符合协议语义；退出路径靠 PostToolUse + transcript watcher 推进检测保证
    // （AskUserQuestion 一定有"用户回答完 → Claude 继续"的后续，不会像普通工具那样永久卡住）。
    [Theory]
    [InlineData("default")]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("acceptEdits")]
    [InlineData("dontAsk")]
    [InlineData("plan")]
    [InlineData(null)]  // 缺失字段（旧版 Claude Code 兼容）
    public void PreToolUse_AskUserQuestion_AlwaysRaisesPermissionRequest(string? mode)
    {
        var payload = new ClaudeHookPayload
        {
            HookEventName = "PreToolUse",
            SessionId = "sess-1",
            ToolName = "AskUserQuestion",
            PermissionMode = mode
        };
        Assert.IsType<PermissionRequested>(payload.ToAgentEvent("claude"));
    }
}
