namespace OpenIsland.Core.Models;

/// <summary>
/// 权限请求
/// </summary>
public record PermissionRequest
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string Description { get; init; }
    public Dictionary<string, object>? ToolInput { get; init; }
    public List<string>? Suggestions { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 建议的"一直允许"规则（hook 端按 tool_name + tool_input 推断）：
    /// - WebFetch + url=https://linux.do/... → AllowRule { ToolName="WebFetch", Pattern="domain:linux.do" }
    /// - Bash / Read / Edit / 其它 tool → AllowRule { ToolName="Bash", Pattern=null }（一律允许此 tool）
    /// UI 上的"一直允许"按钮以这个为模板生成显示文本（"一直允许 linux.do" / "一直允许 Bash"），
    /// 用户点击后会把这条规则写入 ~/.claude/settings.json 的 permissions.allow 列表。
    /// </summary>
    public AllowRule? SuggestedAlwaysAllow { get; init; }
}

/// <summary>
/// 一条"一直允许"规则。Pattern 为空表示对该 tool 的所有调用都放行；
/// 否则按 Pattern 类型做匹配（如 "domain:linux.do" 限定 WebFetch 域名）。
/// </summary>
public record AllowRule
{
    public required string ToolName { get; init; }
    public string? Pattern { get; init; }

    /// <summary>序列化成 ~/.claude/settings.json permissions.allow 里的字符串</summary>
    public string ToSettingString() =>
        string.IsNullOrEmpty(Pattern) ? ToolName : $"{ToolName}({Pattern})";

    /// <summary>
    /// UI 按钮上显示的文本。镜像 Claude 终端权限提示的"2. Yes, and don't ask again for X this session"。
    /// 用户要求灵动岛的选项原原本本对应 Claude 终端弹的 1/2/3 三选项。
    /// </summary>
    public string ToButtonLabel()
    {
        if (string.IsNullOrEmpty(Pattern)) return $"2. Yes, don't ask again for {ToolName}";
        // domain:linux.do → "2. Yes, don't ask again for linux.do"
        if (Pattern.StartsWith("domain:", StringComparison.Ordinal))
            return $"2. Yes, don't ask again for {Pattern.AsSpan(7).ToString()}";
        return $"2. Yes, don't ask again for {ToolName}";
    }
}

/// <summary>
/// 问题提示（交互式问题）
/// </summary>
public record QuestionPrompt
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// AskUserQuestion 工具的单个候选答案 —— 从 tool_input 的
/// questions[0].options[] 解析。Number 是 1-based 序号（对应 Claude Code
/// 终端的编号列表 / Claude Desktop 选项行右侧的数字徽章 1/2/3…）。
/// 灵动岛把每个选项渲染成一个可点按钮，点击后按 entrypoint 分流：
/// CLI 注入 "{Number}\r" 到终端；Claude Desktop 用 UIA 点对应选项行。
/// </summary>
public record QuestionOption
{
    public required int Number { get; init; }
    public required string Label { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// 跳转目标（用于一键跳回终端）
/// </summary>
public record JumpTarget
{
    public string? TerminalApp { get; init; }
    public string? TerminalSessionId { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? ProcessId { get; init; }
    public string? WindowTitle { get; init; }
}
