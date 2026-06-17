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

    /// <summary>每条命令的超时上限：claude CLI 要拉 git 仓库，可能较慢。</summary>
    private static readonly TimeSpan PerCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 整个命令序列的全局总超时预算：owner/repo 展开 2 条、连写可任意多条，
    /// 仅靠每条 5min 时最坏 N×5min 不可控；总预算给命令序列封顶。
    /// </summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(12);

    /// <summary>
    /// 逐条顺序执行命令（任意一条失败即停止）。onProgress 报告当前执行到哪条；
    /// 每条命令 5 分钟超时、整个序列 12 分钟总超时（取两者先到者），另接收外部
    /// <paramref name="externalToken"/> 供 UI"取消"按钮中断；任一触发都杀进程树并判失败。
    /// 返回 (是否全部成功, 累计输出尾部最多 4000 字符)。
    /// </summary>
    /// <remarks>
    /// 关键修复：stdout/stderr 的 ReadToEndAsync 也必须受超时/取消约束。claude CLI
    /// spawn 的 git/node 孙进程会继承重定向管道的写端句柄；即便父进程退出，只要孙进程
    /// 还攥着写端不放，ReadToEndAsync 就永不返回 —— 过去只对 WaitForExitAsync 设超时，
    /// 读取却裸 await，于是功能永久锁死在"安装中"。现在读取走 WaitAsync(token)，
    /// 超时/取消分支统一 Kill(entireProcessTree) 并观测被丢弃的读取任务，确保任何
    /// 情况下本方法都能返回。
    /// </remarks>
    public async Task<(bool Ok, string Output)> RunAsync(
        IReadOnlyList<string> commands,
        Action<string>? onProgress,
        CancellationToken externalToken = default)
    {
        var output = new StringBuilder();

        // 全局总超时：整个序列共享一个预算；与外部取消令牌联动，二者任一触发即中断。
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        totalCts.CancelAfter(TotalTimeout);

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

                // 本条命令的有效令牌 = 每条 5min ∪ 全局总预算 ∪ 外部取消，先到者生效。
                using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                cmdCts.CancelAfter(PerCommandTimeout);
                var token = cmdCts.Token;

                try
                {
                    // 退出 + 两路读取都必须落在同一令牌下；任一被取消即抛 OperationCanceledException。
                    await proc.WaitForExitAsync(token);
                    var stdout = await stdoutTask.WaitAsync(token);
                    var err = await stderrTask.WaitAsync(token);

                    output.AppendLine(stdout);
                    if (!string.IsNullOrWhiteSpace(err)) output.AppendLine(err);
                }
                catch (OperationCanceledException)
                {
                    // 超时 / 外部取消：杀整棵进程树（claude CLI 会再起 git 等子进程），整体判失败。
                    try { proc.Kill(entireProcessTree: true); } catch { }

                    // 观测被丢弃的读取任务，避免 stdoutTask/stderrTask 成为未观测异常任务
                    // （进程树已杀，管道写端关闭，读取通常会迅速以异常/空串收尾；给 2s 兜底）。
                    try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { /* 已杀进程，读取的异常/超时无需再处理 */ }

                    // 区分"用户主动取消"与"超时"：外部令牌被触发 → 取消；否则 → 超时。
                    output.AppendLine(externalToken.IsCancellationRequested
                        ? $"[{cmd}] canceled"
                        : $"[{cmd}] timed out");
                    return (false, Tail(output));
                }

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
