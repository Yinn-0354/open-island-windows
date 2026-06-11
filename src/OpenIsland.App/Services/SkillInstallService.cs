using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenIsland.App.Services;

/// <summary>
/// 灵动岛"安装 Skill"服务 — 把用户粘贴的 claude plugin 命令（或 owner/repo 简写）
/// 解析成一组安全命令，再在后台 PowerShell 里逐条执行（不弹窗）。
/// </summary>
/// <remarks>
/// 安全是第一位的：命令最终会拼进 powershell.exe -Command，因此 ParseCommands 用
/// 严格白名单正则过滤每一条命令 —— 只允许 claude plugin 的固定子命令 + 一个
/// 仅含 [\w@./:-] 的参数，; | &amp; 反引号 $ () &lt;&gt; 引号等 shell 元字符根本进不来，
/// 从源头杜绝注入。
/// </remarks>
public class SkillInstallService
{
    /// <summary>每条命令必须整体匹配的白名单：claude plugin 的固定子命令 + 单个安全参数。</summary>
    private static readonly Regex SafeCommand = new(
        @"^claude\s+plugin\s+(marketplace\s+add|marketplace\s+update|install|update|uninstall)\s+[\w@./:-]+$",
        RegexOptions.Compiled);

    /// <summary>owner/repo 简写形式（如 Yuan1z0825/nature-skills）。</summary>
    private static readonly Regex OwnerRepo = new(@"^[\w.-]+/[\w.-]+$", RegexOptions.Compiled);

    /// <summary>
    /// 把用户输入解析成待执行命令列表。支持两种输入：
    ///   ① 一段（可能连写/换行的）claude plugin 命令 —— 用前瞻在每个 "claude plugin" 处切分；
    ///   ② owner/repo 简写 —— 展开成 marketplace add + install repo@repo 两条。
    /// 解析后逐条过白名单，不合法的直接丢弃（宁缺毋滥）。
    /// </summary>
    public static List<string> ParseCommands(string input)
    {
        var result = new List<string>();
        var text = (input ?? "").Trim();
        if (text.Length == 0) return result;

        // 注意分支条件必须是 "claude plugin" 整词，不能只看是否含 "claude" —— 否则
        // anthropics/claude-skills 这类 repo 名里带 claude 的 owner/repo 简写会被误判成命令。
        if (Regex.IsMatch(text, @"\bclaude\s+plugin\b"))
        {
            // README 里常见 "cmd1 && cmd2" / "cmd1; cmd2" 连写 —— 先把命令间分隔符归一化成
            // 空格再切分（白名单仍对每段整体校验，分隔符后挂恶意尾巴依旧过不了 SafeCommand）。
            text = Regex.Replace(text, @"\s*(?:&&|;)\s*", " ");

            // 按 "claude plugin" 边界前瞻切分；每段内部换行/连续空白压成单空格后再校验。
            foreach (var piece in Regex.Split(text, @"(?=\bclaude\s+plugin\b)"))
            {
                var cmd = Regex.Replace(piece, @"\s+", " ").Trim();
                if (cmd.Length == 0) continue;
                // 首段可能是 "$ " 之类复制来的提示符前缀，宽容跳过（不以 claude 开头的垃圾段）。
                if (!cmd.StartsWith("claude", StringComparison.Ordinal)) continue;
                // 原子语义：任何一条 claude plugin 命令不合法 → 整体拒绝（返回空表 → UI 提示
                // Skill_Invalid），绝不部分执行 —— 否则 marketplace add 被丢、install 单跑必失败。
                if (!SafeCommand.IsMatch(cmd)) { result.Clear(); return result; }
                result.Add(cmd);
            }
        }
        else if (OwnerRepo.IsMatch(text))
        {
            // owner/repo 简写：约定 marketplace 名与 repo 同名，展开成标准两步安装。
            var repo = text[(text.IndexOf('/') + 1)..];
            var add = $"claude plugin marketplace add {text}";
            var install = $"claude plugin install {repo}@{repo}";
            if (SafeCommand.IsMatch(add) && SafeCommand.IsMatch(install))
            {
                result.Add(add);
                result.Add(install);
            }
        }

        return result;
    }

    /// <summary>
    /// 逐条顺序执行命令（任意一条失败即停止）。onProgress 报告当前执行到哪条；
    /// 每条命令 5 分钟超时（claude CLI 要拉 git 仓库，可能较慢），超时杀进程树并判失败。
    /// 返回 (是否全部成功, 累计输出尾部最多 4000 字符)。
    /// </summary>
    public async Task<(bool Ok, string Output)> RunAsync(IReadOnlyList<string> commands, Action<string>? onProgress)
    {
        var output = new StringBuilder();
        for (int i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            onProgress?.Invoke($"({i + 1}/{commands.Count}) {cmd}");

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    output.AppendLine($"[{cmd}] failed to start powershell");
                    return (false, Tail(output));
                }

                // 先挂上异步读取再等退出，避免 stdout 缓冲区写满导致子进程卡死。
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 超时：杀整棵进程树（claude CLI 会再起 git 等子进程），整体判失败。
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    output.AppendLine($"[{cmd}] timed out after 5 minutes");
                    return (false, Tail(output));
                }

                output.AppendLine(await stdoutTask);
                var err = await stderrTask;
                if (!string.IsNullOrWhiteSpace(err)) output.AppendLine(err);

                if (proc.ExitCode != 0)
                {
                    output.AppendLine($"[{cmd}] exit code {proc.ExitCode}");
                    return (false, Tail(output));
                }
            }
            catch (Exception ex)
            {
                output.AppendLine($"[{cmd}] {ex.Message}");
                return (false, Tail(output));
            }
        }

        return (true, Tail(output));
    }

    /// <summary>累计输出只保留尾部 4000 字符 —— 错误信息几乎总在最后，全量留着没意义。</summary>
    private static string Tail(StringBuilder sb)
    {
        var s = sb.ToString();
        return s.Length <= 4000 ? s : s[^4000..];
    }
}
