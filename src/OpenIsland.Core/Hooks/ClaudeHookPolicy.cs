namespace OpenIsland.Core.Hooks;

/// <summary>
/// PreToolUse hook 的权限策略判定（纯函数，便于单测）。
///
/// 背景：Claude Code 的 PreToolUse hook 在所有权限模式下都会运行，且 hook 若显式输出
/// permissionDecision:"ask"，会 *覆盖* 当前权限模式、即使用户处于 bypassPermissions / auto /
/// acceptEdits 也照样强制弹询问（官方语义，见 code.claude.com/docs/en/hooks）。
///
/// 灵动岛只想做"镜像显示 + 终端交互"，绝不能在用户已选非交互模式时替他把询问弹回来。
/// 因此：只有 "default"（普通）模式才输出 ask；其余模式一律 defer（不输出决定，交还给模式自身）。
/// </summary>
public static class ClaudeHookPolicy
{
    /// <summary>
    /// 给定 hook 输入里的 permission_mode 值，判断是否应让 hook 强制弹询问（输出 ask）。
    /// 仅 "default" 返回 true；其余已知模式（plan/acceptEdits/auto/dontAsk/bypassPermissions）返回 false。
    /// 缺失/空 → true：旧版 Claude Code 不带此字段，那时只可能是 default，保持原有行为（向后兼容）。
    /// 比较大小写不敏感，避免大小写差异把 bypass 误判成 default 而重新触发恶性 bug。
    /// </summary>
    public static bool ShouldForceAsk(string? permissionMode)
    {
        if (string.IsNullOrWhiteSpace(permissionMode)) return true;
        return string.Equals(permissionMode.Trim(), "default", System.StringComparison.OrdinalIgnoreCase);
    }
}
