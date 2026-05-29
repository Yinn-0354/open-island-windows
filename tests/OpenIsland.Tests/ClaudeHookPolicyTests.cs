using OpenIsland.Core.Hooks;

namespace OpenIsland.Tests;

/// <summary>
/// 核心回归：PreToolUse hook 是否应强制弹询问，必须尊重会话的 permission_mode。
/// 官方语义：hook 显式返回 permissionDecision:"ask" 会覆盖权限模式、即使在 bypass/auto 下也强制弹窗。
/// 因此岛的 hook 只能在 "default"（普通）模式下输出 ask；其余模式必须 defer（不输出决定）。
/// </summary>
public class ClaudeHookPolicyTests
{
    [Fact]
    public void DefaultMode_ForcesAsk()
        => Assert.True(ClaudeHookPolicy.ShouldForceAsk("default"));

    // 缺失 / 空：旧版 Claude Code 不带该字段，此时只可能是 default 模式 —— 保持原有"镜像+ask"行为。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingMode_FallsBackToAsk(string? mode)
        => Assert.True(ClaudeHookPolicy.ShouldForceAsk(mode));

    // 用户选择的非交互模式：绝不能强制弹窗（这正是"开岛就在 bypass/auto 下弹询问"那个恶性 bug）。
    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    [InlineData("dontAsk")]
    public void NonDefaultModes_DoNotForceAsk(string mode)
        => Assert.False(ClaudeHookPolicy.ShouldForceAsk(mode));

    // 大小写鲁棒：取值理论上是固定大小写，但比较不应因大小写漏判而把 bypass 当成 default。
    [Theory]
    [InlineData("BypassPermissions")]
    [InlineData("AUTO")]
    [InlineData("AcceptEdits")]
    public void NonDefaultModes_AreCaseInsensitive(string mode)
        => Assert.False(ClaudeHookPolicy.ShouldForceAsk(mode));
}
