using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using OpenIsland.Core.Bridge;
using OpenIsland.Core.Hooks;

namespace OpenIsland.Hooks;

[SupportedOSPlatform("windows")]

/// <summary>
/// Open Island Hooks CLI - 代理钩子入口点
/// 从stdin读取hook事件，转发到Open Island主应用
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Claude / Codex / Cursor / Gemini 这些 agent 都用 UTF-8 写 JSON 给 hook stdin。
        // 但中文/日文 Windows 的 Console.InputEncoding 默认是系统 ANSI 代码页（cp936/cp932），
        // StreamReader 直接拿这个会把 UTF-8 字节误解成乱码（"总结" → "鎬荤粨"）。
        // 强制三个流都按 UTF-8，避免双向 mojibake（input prompt 字段乱码 + output directive
        // 反向再乱一次）。
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var rootCommand = new RootCommand("Open Island Hooks - Bridge AI agents to Open Island dashboard");

        // --source 参数
        var sourceOption = new Option<string>(
            name: "--source",
            description: "Hook source (claude, codex, cursor, gemini, etc.)"
        )
        {
            IsRequired = true
        };
        sourceOption.AddAlias("-s");
        rootCommand.AddOption(sourceOption);

        // --timeout 参数（交互式hook需要更长时间）
        var timeoutOption = new Option<int>(
            name: "--timeout",
            description: "Connection timeout in seconds",
            getDefaultValue: () => 45
        )
        {
            IsRequired = false
        };
        timeoutOption.AddAlias("-t");
        rootCommand.AddOption(timeoutOption);

        rootCommand.SetHandler(async (string source, int timeout) =>
        {
            var exitCode = await RunAsync(source, timeout);
            Environment.ExitCode = exitCode;
        }, sourceOption, timeoutOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunAsync(string source, int timeoutSeconds)
    {
        try
        {
            // 从stdin读取hook payload —— 显式 UTF-8（Main 已设 Console.InputEncoding，但
            // 这里 new StreamReader 时再传一遍以防 OpenStandardInput 拿到的 stream 没继承）
            string jsonInput;
            using (var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8))
            {
                jsonInput = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(jsonInput))
            {
                // 空输入可能是hook测试，静默退出
                return 0;
            }

            // 解析JSON
            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(jsonInput);
            }
            catch (JsonException)
            {
                // 无效JSON，但fail open
                await Console.Error.WriteLineAsync("Warning: Invalid JSON input");
                return 0;
            }

            // 检测是否为交互式hook（需要长时间等待用户响应）
            bool isInteractive = IsInteractiveHook(source, payload);

            // Fast-path：交互式 hook 启动时先读 ~/.claude/settings.json 的 permissions.allow
            // 列表，命中规则直接 exit 0 输出 approve 不连 bridge 不弹 UI。这是"一直允许"的运行时
            // 兑现 —— 用户在 OpenIsland 上点过"一直允许 linux.do"后，下次同 tool/同域名瞬间放行。
            if (isInteractive && IsAlwaysAllowedByLocalSettings(source, payload))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    decision = "approve",
                    reason = "Pre-approved via Open Island always-allow"
                }));
                return 0;
            }
            // 显示模式：交互 hook 不再阻塞 Claude 等岛上点击 —— 立刻把事件投递给岛之后退出
            // 并输出 permissionDecision:"ask" 让 Claude 用它自己的终端 1/2/3 prompt 跟用户对话。
            // 这是 B 模式的实际形态：协议上 hook 一旦 exit 就没法再影响 Claude，所以岛只做"详情
            // 镜像 + 跳转终端"，实际权限交互走终端。
            // 非交互 hook（PostToolUse 等）按 timeout 参数走 fire-and-forget。
            var effectiveTimeout = TimeSpan.FromSeconds(timeoutSeconds);

            // 任务完成 / 权限请求 / 问题：直接由 hook 子进程在本地播提示音，不依赖 bridge ——
            // 这是用户钦定的"任务完成"提示音路径（之前主进程 BeepService 那条已撤）。
            // 即使主应用未运行，Stop 事件仍能让用户听到。
            if (IsStopEvent(source, payload) || isInteractive)
            {
                PlayBeep();
            }

            // 连接到bridge —— 短超时（500ms）：UI 没在跑则立即放弃，避免阻塞 Claude
            await using var client = new BridgeCommandClient(effectiveTimeout);
            using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var connected = await client.ConnectAsync(connectCts.Token);

            if (!connected)
            {
                // Open Island未运行，fail open
                await Console.Error.WriteLineAsync("Warning: Open Island not running, hook event dropped");
                return 0;
            }

            using var cts = new CancellationTokenSource(effectiveTimeout);

            // 所有事件都 fire-and-forget：发完即走，不等岛回 directive。
            // 交互 hook（PreToolUse 等）多输出 permissionDecision:"ask" 让 Claude
            // 自己用终端 1/2/3 prompt 跟用户对话；岛上只做镜像显示。
            var ack = await client.SendHookEventAsync(source, payload, cts.Token);
            if (ack == null || !ack.Success)
            {
                await Console.Error.WriteLineAsync($"Warning: Failed to forward hook: {ack?.Error ?? "unknown error"}");
            }

            if (isInteractive)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    hookSpecificOutput = new
                    {
                        hookEventName = "PreToolUse",
                        permissionDecision = "ask",
                        permissionDecisionReason = "Mirrored to Open Island; respond in terminal"
                    }
                }));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            // 超时，fail open
            await Console.Error.WriteLineAsync("Warning: Hook forwarding timed out");
            return 0;
        }
        catch (Exception ex)
        {
            // 任何错误都fail open，确保不影响代理运行
            await Console.Error.WriteLineAsync($"Warning: Hook error: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 检测是否为需要用户交互的hook
    /// </summary>
    /// <summary>
    /// 把 hook event 名归一化以应对 Claude Code 实际写 PascalCase（"PreToolUse"）
    /// 而我们代码到处假设 snake_case（"pre_tool_use"）的不一致。归一形式：删 underscore + lowercase。
    ///   "PreToolUse" / "pre_tool_use" / "pretooluse" → "pretooluse"
    /// </summary>
    private static string NormalizeEventName(string? raw)
        => string.IsNullOrEmpty(raw) ? "" : raw.Replace("_", "").ToLowerInvariant();

    private static bool IsInteractiveHook(string source, JsonElement payload)
    {
        // 检查Claude permissionRequest / pre_tool_use（PreToolUse 现在是权限审批入口）
        if (source is "claude" or "qoder" or "qwen" or "factory" or "codebuddy" or "kimi")
        {
            if (payload.TryGetProperty("hook_event_name", out var eventName))
            {
                var eventStr = NormalizeEventName(eventName.GetString());
                if (eventStr == "permissionrequest" || eventStr == "question" || eventStr == "pretooluse")
                {
                    return true;
                }
            }
        }

        // 检查Codex/Cursor/Gemini的交互式事件
        if (payload.TryGetProperty("event", out var codexEvent))
        {
            var eventStr = codexEvent.GetString()?.ToLowerInvariant();
            if (eventStr == "permission_request" || eventStr == "question")
            {
                return true;
            }
        }

        if (payload.TryGetProperty("type", out var cursorEvent))
        {
            var eventStr = cursorEvent.GetString()?.ToLowerInvariant();
            if (eventStr == "question")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 读 ~/.claude/settings.json 的 permissions.allow 列表，按 tool_name + tool_input 判断是否命中。
    /// 支持的规则形态：
    ///   "Bash"                          → 任意 Bash 调用
    ///   "WebFetch(domain:linux.do)"     → 仅 linux.do 域
    ///   其它带括号 pattern 的暂按 tool 名前缀宽松匹配
    /// 任何错误（settings.json 不存在、解析失败）都返回 false → 走正常 UI 弹窗流程。
    /// </summary>
    private static bool IsAlwaysAllowedByLocalSettings(string source, JsonElement payload)
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var settingsPath = Path.Combine(userProfile, ".claude", "settings.json");
            if (!File.Exists(settingsPath)) return false;

            var text = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("permissions", out var perms)) return false;
            if (!perms.TryGetProperty("allow", out var allowArr) || allowArr.ValueKind != JsonValueKind.Array) return false;

            var toolName = TryGetString(payload, "tool_name");
            if (string.IsNullOrEmpty(toolName)) return false;
            var toolInput = payload.TryGetProperty("tool_input", out var ti) ? ti : default;

            foreach (var ruleElem in allowArr.EnumerateArray())
            {
                var rule = ruleElem.GetString();
                if (string.IsNullOrEmpty(rule)) continue;
                if (MatchesRule(rule, toolName, toolInput)) return true;
            }
        }
        catch { /* 任何失败都退到 UI 弹窗路径 */ }
        return false;
    }

    private static bool MatchesRule(string rule, string toolName, JsonElement toolInput)
    {
        // 形态: "ToolName" 或 "ToolName(pattern)"
        var parenIdx = rule.IndexOf('(');
        if (parenIdx < 0)
        {
            return string.Equals(rule, toolName, StringComparison.OrdinalIgnoreCase);
        }

        var ruleTool = rule[..parenIdx];
        if (!string.Equals(ruleTool, toolName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!rule.EndsWith(')')) return false;
        var pattern = rule[(parenIdx + 1)..^1];

        // domain:host —— WebFetch 等带 url 字段的工具
        if (pattern.StartsWith("domain:", StringComparison.Ordinal))
        {
            var wantHost = pattern[7..];
            if (toolInput.ValueKind == JsonValueKind.Object && toolInput.TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString();
                if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return string.Equals(uri.Host, wantHost, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // 其它 pattern 暂时不实现细粒度匹配，只要 ToolName 一致就放行（保守跟随 settings.json 用户意图）
        return true;
    }

    private static string? TryGetString(JsonElement payload, string property)
    {
        try
        {
            if (payload.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        catch { }
        return null;
    }

    private static bool IsStopEvent(string source, JsonElement payload)
    {
        if (payload.TryGetProperty("hook_event_name", out var name))
        {
            var v = name.GetString()?.ToLowerInvariant();
            return v == "stop";
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    /// <summary>
    /// 播放任务完成的"叮"声 —— 真降级链：每条仅在前一条失败时才执行，确保只播一次。
    /// 之前是三条串行 try/catch，每条都吞异常但不 return，结果 3 个声音重叠播放。
    /// </summary>
    private static void PlayBeep()
    {
        // 1) Asterisk（典型的"叮"提示音，跟系统通知同源） —— 首选
        try { if (MessageBeep(0x00000040)) return; } catch { }
        // 2) Simple Beep —— 不依赖系统声音方案的兜底
        try { if (MessageBeep(0x00000000)) return; } catch { }
        // 3) Console.Beep 880Hz 100ms —— 没声卡时主板蜂鸣
        try { Console.Beep(880, 100); } catch { }
    }
}
