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
    /// 给定 hook 输入里的 permission_mode（+ 可选 tool_name）值，判断是否应让 hook 强制弹询问（输出 ask）。
    /// 仅 "default" 返回 true；其余已知模式（plan/acceptEdits/auto/dontAsk/bypassPermissions）返回 false。
    /// 缺失/空 → true：旧版 Claude Code 不带此字段，那时只可能是 default，保持原有行为（向后兼容）。
    /// 比较大小写不敏感，避免大小写差异把 bypass 误判成 default 而重新触发恶性 bug。
    ///
    /// 特例：toolName 为 "ExitPlanMode" 或 "AskUserQuestion" 时无条件返回 true，不看 permissionMode。
    /// 原因：ExitPlanMode 本身就是"请求用户批准计划"这个交互动作，不是一次可以被自动放行模式
    /// 跳过的普通工具调用——如果照常规逻辑在 plan/auto/bypassPermissions 等模式下被丢弃，就等于
    /// 计划完全没人审阅就自动通过，直接违背这个工具存在的意义。所以它必须永远强制走 ask，
    /// 让灵动岛镜像 + 用户确认这一步不可被任何权限模式绕过。
    /// AskUserQuestion 同理：Claude 协议（见 Agent SDK 文档 user-input）明确要求"即使允许规则匹配时
    /// 也会到达用户回调"——auto/bypass 模式下普通工具自动放行，但 AskUserQuestion 必须停下来等用户
    /// 回答。若照常规逻辑在非 default 模式丢弃，灵动岛就不会拉橙卡提醒，用户在 auto 模式下被提问时
    /// 完全不知道该看终端——正是这个 bug 的根因。两个工具都必须在任何模式下镜像到岛 + 弹终端询问。
    /// </summary>
    public static bool ShouldForceAsk(string? permissionMode, string? toolName = null)
    {
        var name = toolName?.Trim();
        if (string.Equals(name, "ExitPlanMode", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "AskUserQuestion", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(permissionMode)) return true;
        return string.Equals(permissionMode.Trim(), "default", System.StringComparison.OrdinalIgnoreCase);
    }
}
