using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using OpenIsland.Core;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Services;

/// <summary>
/// 终端跳转服务 - 实现一键跳转到各种终端
/// </summary>
public class TerminalJumpService
{
    private readonly ILogger<TerminalJumpService>? _logger;

    public TerminalJumpService(ILogger<TerminalJumpService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 跳转到指定工作目录的终端
    /// </summary>
    public async Task<bool> JumpToWorkingDirectoryAsync(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            _logger?.LogWarning("Invalid working directory: {Dir}", workingDirectory);
            return false;
        }

        // 1. 先尝试按工作目录名找已有终端窗口并置前
        var dirName = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (!string.IsNullOrEmpty(dirName) && BringTerminalToFront(dirName))
            return true;

        // 2. 找不到已有窗口时开新终端
        var target = new JumpTarget { WorkingDirectory = workingDirectory };
        if (IsWindowsTerminalAvailable())
            return await JumpToWindowsTerminalAsync(target);

        return await JumpToPowerShellAsync(target);
    }

    /// <summary>
    /// 跳转到指定会话的终端：优先激活已有窗口，找不到再开新窗口
    /// </summary>
    public async Task<bool> JumpToSessionAsync(AgentSession session)
    {
        if (session.JumpTarget == null)
        {
            _logger?.LogWarning("Session {SessionId} has no jump target", session.Id);
            return false;
        }

        var target = session.JumpTarget;

        // 1. 先尝试按工作目录名找已有终端窗口并置前
        if (!string.IsNullOrEmpty(target.WorkingDirectory))
        {
            var dirName = Path.GetFileName(target.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));
            if (!string.IsNullOrEmpty(dirName) && BringTerminalToFront(dirName))
                return true;
        }

        // 2. 按窗口标题找
        if (!string.IsNullOrEmpty(target.WindowTitle) && BringTerminalToFront(target.WindowTitle))
            return true;

        // 3. 找不到已有窗口时开新终端
        if (IsWindowsTerminalAvailable())
            return await JumpToWindowsTerminalAsync(target);

        return await JumpToPowerShellAsync(target);
    }

    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "wt", "conhost", "cmd", "powershell", "pwsh",
        "alacritty", "wezterm-gui", "wezterm", "tabby", "fluent-terminal"
    };

    /// <summary>
    /// 枚举所有可见窗口，找属于终端进程且标题含 searchText 的窗口并激活。
    /// 之前没限制进程名，短关键词（比如用户名 / 项目名前缀）会命中浏览器 / 文件管理器，
    /// 把错误窗口推到前台，看上去像"点了下拉但没出来终端"。
    /// </summary>
    private bool BringTerminalToFront(string searchText)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var sb = new StringBuilder(512);
            if (GetWindowText(hWnd, sb, 512) <= 0) return true;
            var title = sb.ToString();
            if (!title.Contains(searchText, StringComparison.OrdinalIgnoreCase)) return true;

            // 只接受属于终端进程的窗口
            try
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0) return true;
                using var proc = Process.GetProcessById((int)pid);
                if (TerminalProcessNames.Contains(proc.ProcessName))
                {
                    found = hWnd;
                    return false; // 停止枚举
                }
            }
            catch { /* 进程已退出 / 访问被拒：跳过 */ }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        ActivateWindow(found);
        return true;
    }

    /// <summary>
    /// 检查Windows Terminal是否可用
    /// </summary>
    public bool IsWindowsTerminalAvailable()
    {
        try
        {
            // 检查Windows Terminal是否安装
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wtPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");

            if (File.Exists(wtPath))
            {
                return true;
            }

            // 检查是否有Windows Terminal包
            var packagesPath = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packagesPath))
            {
                var wtPackages = Directory.GetDirectories(packagesPath, "Microsoft.WindowsTerminal*");
                return wtPackages.Length > 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在新终端里启动 `claude --resume {sessionId}`，恢复指定的 Claude Code 会话。
    ///
    /// 套 pwsh 壳子（不直接 wt -d {cwd} claude --resume）—— 这样：
    ///   1) pwsh 起来时加载用户 profile，PATH/conda env/别名 都齐全，比 wt 直接 spawn
    ///      claude.exe 的环境更接近用户日常用法，避免 "No conversation found" 这种因
    ///      env 不全找不到 transcript 的报错。
    ///   2) -NoExit 让 pwsh 留着不退 —— 即便 --resume 真挂了，用户仍能在终端里手动
    ///      `claude --resume {id}` 或 `claude` 救场，不会一闪而过。
    /// </summary>
    public async Task<bool> LaunchClaudeResumeAsync(string sessionId, string? workingDirectory)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        // sessionId comes from untrusted hook/transcript data and is interpolated into the
        // `claude --resume {sessionId}` shell command below. Reject anything outside the safe
        // allow-list (letters/digits/-/_) to prevent command injection. See SessionIdValidator.
        if (!SessionIdValidator.IsValid(sessionId))
        {
            _logger?.LogWarning("Refusing claude --resume: session id failed validation");
            return false;
        }
        var cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 命令里 *绝不能* 有 `;` —— wt 把分号当成"多 tab 分隔符"。
        // -NoProfile 跳过用户 profile —— 部分用户 profile 里有 conda activate 等
        // 自动初始化，遇到 PATH 里的非 ASCII 字符会触发 GBK 编码 crash。Claude CLI
        // 不依赖任何 profile 设置（claude.exe 在用户 PATH 直链）。
        // -NoExit 留窗口，万一出错用户在原地有可用 prompt 救场。
        try
        {
            if (IsWindowsTerminalAvailable())
            {
                // `-w 0 new-tab`: 在最近使用的 WT 窗口里开 *新 tab*，而不是 `-d` 那样开
                // 一个新的 WT 窗口。这样用户已有的 WT 窗口被复用 / 拉前，避免桌面被
                // 一堆独立 WT 窗口塞满。如果当前没有 WT 窗口存在，wt 会自动创建一个。
                // 对应 issue：用户点会话卡片，希望复用现有终端而不是每次开新窗。
                var args = $"-w 0 new-tab -d \"{cwd}\" powershell.exe -NoProfile -NoExit -Command \"claude --resume {sessionId}\"";
                _logger?.LogInformation("Resuming Claude session via wt+powershell: {Args}", args);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = args,
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
                return true;
            }

            // 没 wt：直接起 PowerShell 兜底，WorkingDirectory 设 cwd
            var psArgs = $"-NoProfile -NoExit -Command \"claude --resume {sessionId}\"";
            _logger?.LogInformation("Resuming Claude session via PowerShell: {Args}", psArgs);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = psArgs,
                WorkingDirectory = cwd,
                UseShellExecute = true,
                CreateNoWindow = false
            });
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to launch claude --resume for session {Sid}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// 把 Claude Code 桌面端窗口拉到前台。两遍兜底找窗口：
    ///   1) 进程名 claude + 拥有可见顶层窗口（Electron 直接持窗的常规情况）
    ///   2) 标题含 "Claude" 的可见顶层窗口（UWP/MSIX 打包形态下窗口可能被
    ///      ApplicationFrameHost.exe 持有，owning pid 不是 claude）
    /// 排除 "Open Island" 自己的标题以免误激活。
    /// </summary>
    public bool ActivateClaudeDesktopWindow(string? sessionTitle = null)
    {
        var claudePids = new HashSet<int>(
            Process.GetProcessesByName("claude").Select(p => p.Id));

        IntPtr bestHwnd = IntPtr.Zero;
        long bestArea = 0;
        int bestOwnerPid = 0;

        // Claude Desktop（Electron）同一进程下有多个 Chrome_WidgetWin_1 窗口：
        //   - 真正的用户 UI 窗口：尺寸大（例如 608x472+），rect 在屏幕内
        //   - 任务栏代理 helper：尺寸小（例如 158x26）放在 offscreen 远端（rect 像
        //     (-21333,-21333)），即使最小化后 IsWindowVisible 仍返回 True
        // 不能用 "visible=True" 来挑 —— 会选到 helper，激活后用户什么也看不到。
        // 按窗口面积选最大的那个就是用户的真实 UI（即使当前 visible=False / iconic=True），
        // 然后 SW_SHOWNORMAL 强制恢复显示 + 拉到前台。
        if (claudePids.Count > 0)
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!claudePids.Contains((int)pid)) return true;
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) <= 0) return true;
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, 64);
                if (!cls.ToString().Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!GetWindowRect(hWnd, out var rect)) return true;
                long w = rect.Right - rect.Left;
                long h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0) return true;
                long area = w * h;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestHwnd = hWnd;
                    bestOwnerPid = (int)pid;
                }
                return true;
            }, IntPtr.Zero);
        }

        // 兜底：claude 进程名没匹配上（例如 ApplicationFrameHost 持窗的旧 UWP 形态），
        // 退回到"标题含 Claude 的可见窗口"。
        if (bestHwnd == IntPtr.Zero)
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) <= 0) return true;
                var title = sb.ToString();
                if (title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (title.IndexOf("Open Island", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                bestHwnd = hWnd;
                return false;
            }, IntPtr.Zero);
        }

        if (bestHwnd == IntPtr.Zero) return false;
        _logger?.LogInformation(
            "Activating Claude Desktop hwnd=0x{Hwnd:X} area={Area}",
            bestHwnd.ToInt64(), bestArea);
        // SW_SHOWNORMAL 对最小化 / 隐藏 / 可见都能正确恢复到正常窗口状态
        ShowWindow(bestHwnd, SW_SHOWNORMAL);
        ActivateWindow(bestHwnd);

        // 进一步：如果传了 sessionTitle，通过 UI Automation 在侧边栏点击对应 session 按钮 ——
        // Claude Desktop 没有 URL scheme 深链到 session（只有 /new 和 cowork/shared-artifact），
        // UIA 是唯一可行路径。侧边栏每个 session 在 UIA 树里是 Type=Button、Name="<Phase>
        // <Title>"（例 "Awaiting input ppt"、"Idle 实践"）的元素，支持 InvokePattern。
        if (!string.IsNullOrEmpty(sessionTitle) && bestOwnerPid > 0)
        {
            TryNavigateClaudeDesktopToSession(bestOwnerPid, sessionTitle);
        }
        return true;
    }

    /// <summary>
    /// 用 UI Automation 在 Claude Desktop（PID = claudeDesktopPid）的侧边栏找到 Name 包含
    /// sessionTitle 的 session 按钮，滚动到可见并 Invoke（等价用户点击）。
    /// 失败静默 —— 激活窗口已经成功，导航只是 best-effort。
    /// </summary>
    private void TryNavigateClaudeDesktopToSession(int claudeDesktopPid, string sessionTitle)
    {
        // 落地诊断到本地文件 —— 由于 _logger 在 OpenIsland 当前 DI 配置里是 null（没接
        // logger provider），出问题没法看日志。临时写文件直到问题确诊。
        var diagPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openisland-uia.log");
        void Diag(string msg) { try { System.IO.File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { } }

        try
        {
            Diag($"begin: pid={claudeDesktopPid} title='{sessionTitle}'");
            // Claude Desktop 从隐藏 / iconic 恢复后，Electron 渲染 sidebar 需要时间。
            // UIA 立刻查询常拿到不完整的树（button 还没挂载）。重试 + 退避，每次 250ms，
            // 最多 ~2s。一旦找到目标 button 立刻 Invoke 返回。
            for (int attempt = 0; attempt < 8; attempt++)
            {
                System.Threading.Thread.Sleep(250);

                AutomationElementCollection buttons;
                try
                {
                    var root = AutomationElement.RootElement;
                    var pidCond = new PropertyCondition(AutomationElement.ProcessIdProperty, claudeDesktopPid);
                    var typeCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                    var query = new AndCondition(pidCond, typeCond);
                    buttons = root.FindAll(TreeScope.Descendants, query);
                }
                catch (Exception findEx)
                {
                    Diag($"  attempt {attempt}: FindAll threw {findEx.GetType().Name}: {findEx.Message}");
                    continue;
                }

                AutomationElement? target = null;
                int scanned = 0;
                foreach (AutomationElement btn in buttons)
                {
                    scanned++;
                    string name;
                    try { name = btn.Current.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith("More options for", StringComparison.OrdinalIgnoreCase)) continue;
                    // session 按钮的 Name = "<Phase> <Title>"（"Awaiting input ppt"、"Idle 实践" 等）
                    if (name.EndsWith(" " + sessionTitle, StringComparison.Ordinal) ||
                        name.Equals(sessionTitle, StringComparison.Ordinal))
                    {
                        target = btn;
                        Diag($"  attempt {attempt}: matched button Name='{name}' (scanned {scanned})");
                        break;
                    }
                }

                if (target == null)
                {
                    Diag($"  attempt {attempt}: button count={buttons.Count}, no match");
                    continue;
                }

                try
                {
                    var scrollPattern = target.GetCurrentPattern(ScrollItemPattern.Pattern) as ScrollItemPattern;
                    scrollPattern?.ScrollIntoView();
                }
                catch (Exception scrollEx)
                {
                    Diag($"  scroll failed: {scrollEx.Message}");
                }
                // 关键：必须先 SetFocus，单纯 InvokePattern.Invoke 在 Claude Desktop 上无效。
                // 侧边栏按钮的 CSS 是 `data-[selected=focused]:text-text-000` —— React 实现里
                // 选中 / 导航是基于 *键盘焦点*，不是 onClick 事件。programmatic click 不带焦点
                // 状态变化，React 不识别为"选中此 session"。SetFocus 后再 Invoke 两件事都触发。
                try { target.SetFocus(); } catch (Exception focusEx) { Diag($"  SetFocus failed: {focusEx.Message}"); }
                System.Threading.Thread.Sleep(80);
                var invokePattern = target.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                invokePattern?.Invoke();
                Diag($"  SetFocus + Invoke called -> done");
                _logger?.LogInformation(
                    "Navigated Claude Desktop to session '{Title}' after {Attempts} attempt(s)",
                    sessionTitle, attempt + 1);
                return;
            }

            Diag($"all 8 attempts exhausted, target not found");
            _logger?.LogInformation(
                "Claude Desktop sidebar: session button for title '{Title}' not found after 8 retries",
                sessionTitle);
        }
        catch (Exception ex)
        {
            Diag($"unexpected exception: {ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(ex, "Failed to navigate Claude Desktop to session '{Title}'", sessionTitle);
        }
    }

    /// <summary>
    /// Claude Desktop 主 UI 窗口的 hwnd —— 同 ActivateClaudeDesktopWindow 的"按面积选最大
    /// Chrome_WidgetWin_1"启发式。给 UIA 把搜索范围收窄到这个窗口子树用（不扫全桌面，避免
    /// RootElement.FindAll 在 Electron 长跑后 COM 超时 / 命中 500+ 无关按钮）。
    /// </summary>
    private IntPtr FindClaudeDesktopMainHwnd()
    {
        var claudePids = new HashSet<int>(
            Process.GetProcessesByName("claude").Select(p => p.Id));
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        if (claudePids.Count > 0)
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!claudePids.Contains((int)pid)) return true;
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, 64);
                if (!cls.ToString().Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!GetWindowRect(hWnd, out var rect)) return true;
                long w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0) return true;
                long area = w * h;
                if (area > bestArea) { bestArea = area; best = hWnd; }
                return true;
            }, IntPtr.Zero);
        }
        return best;
    }

    /// <summary>
    /// 桌面端权限审批：Claude Desktop（Electron）派生的子 claude.exe 跑了 PreToolUse hook 把
    /// 请求镜像到岛上，但真正的按钮在 Electron 窗口里。Claude Desktop 实测就 2 个按钮：
    /// <c>Allow once</c> / <c>Deny</c>（部分工具多一个 <c>Allow always</c>）。
    ///
    /// 关键修正（旧实现踩的坑，见 openisland-uia.log）：
    ///   1. 不再用会话标题做侧边栏导航 —— 权限弹窗就在当前活动会话里，标题还常是超长 URL
    ///      匹配不上；只把窗口拉前台。
    ///   2. <b>精确等值</b>匹配归一化按钮名，不再 contains —— 否则 "Look…" 命中 "ok"、
    ///      弹窗标题 "Allow Claude to use PowerShell?" 命中 "allow"（且它不是真正的按钮，
    ///      Invoke 抛 "不支持的模式"）。
    ///   3. 取**最后一个非离屏**的精确匹配 —— 历史已答的权限卡可能还残留在消息流里。
    ///   4. Invoke 不支持时回退 LegacyIAccessible.DoDefaultAction。
    ///
    /// digit: '1'=Allow once / '2'=Allow always（无则退回 once）/ '3'=Deny。返回是否点到。
    /// </summary>
    public bool RespondInClaudeDesktop(string sessionTitle, char digit)
    {
        if (digit is not ('1' or '2' or '3')) return false;

        var diagPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openisland-uia.log");
        void Diag(string msg) { try { System.IO.File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] perm: {msg}\n"); } catch { } }

        // 不做侧边栏导航：权限弹窗在当前会话里，传 null 让 ActivateClaudeDesktopWindow
        // 只把 Electron 主窗口拉前台（按面积选真 UI + SW_SHOWNORMAL）。
        if (!ActivateClaudeDesktopWindow(null))
        {
            Diag($"ActivateClaudeDesktopWindow failed (digit {digit}, session '{sessionTitle}')");
            return false;
        }

        var claudePids = new HashSet<int>(
            System.Diagnostics.Process.GetProcessesByName("claude").Select(p => p.Id));
        if (claudePids.Count == 0) { Diag("no claude.exe process"); return false; }

        // 归一化：去空白 + 转小写，做**精确等值**比较。
        static string Norm(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

        // 真实按钮名（实测）：Deny / "Allow once Ctrl+Enter"（带快捷键后缀！）。
        // 所以用**前缀**匹配而非精确等值。digit '1' 只认 "allowonce" 开头 —— 不要加
        // 裸 "allow"，否则会命中弹窗标题 "Allow Claude to use X?"。
        string[] want = digit switch
        {
            '2' => new[] { "allowalways", "alwaysallow", "allowforthis", "allowonce" }, // 无 always 退回 once
            '3' => new[] { "deny", "reject", "decline", "don'tallow", "dontallow" },
            _ => new[] { "allowonce" },
        };

        var winHwnd = FindClaudeDesktopMainHwnd();
        Diag($"window hwnd=0x{winHwnd.ToInt64():X} (scoped UIA search)");

        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                System.Threading.Thread.Sleep(250);

                AutomationElementCollection buttons;
                try
                {
                    // 收窄到 Claude Desktop 窗口子树（拿不到 hwnd 才退回全桌面）。
                    // 全桌面 RootElement.FindAll 在 Electron 长跑后会 COM 超时 / 扫到 500+
                    // 无关按钮（见 openisland-uia.log 的 COMException / no match 回归）。
                    var searchRoot = winHwnd != IntPtr.Zero
                        ? AutomationElement.FromHandle(winHwnd)
                        : AutomationElement.RootElement;
                    var typeCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                    buttons = searchRoot.FindAll(TreeScope.Descendants, typeCond);
                }
                catch (Exception findEx)
                {
                    Diag($"  attempt {attempt}: FindAll threw {findEx.GetType().Name}: {findEx.Message}");
                    continue;
                }

                // 前缀匹配，两段择优：
                //  ① strict：可用 + 非离屏，最高优先级、DOM 最靠后（最新那张卡）
                //  ② loose ：忽略 offscreen/enabled 的命中 —— Claude Desktop 长跑后常把
                //     真按钮误报 offscreen/disabled，硬跳过会漏点（回归根因之一）。
                // 有 strict 用 strict，没有就软回退到 loose。
                AutomationElement? strict = null; int strictRank = int.MaxValue;
                AutomationElement? loose = null; int looseRank = int.MaxValue;
                var allNames = new List<string>();
                foreach (AutomationElement btn in buttons)
                {
                    string name; bool offscreen = false, enabled = true;
                    try
                    {
                        name = btn.Current.Name;
                        try { offscreen = btn.Current.IsOffscreen; } catch { }
                        try { enabled = btn.Current.IsEnabled; } catch { }
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (attempt == 7) allNames.Add(name); // 没命中时最后一轮才 dump，省日志

                    var n = Norm(name);
                    int rank = -1;
                    for (int i = 0; i < want.Length; i++)
                        if (n.StartsWith(want[i], StringComparison.Ordinal)) { rank = i; break; }
                    if (rank < 0) continue;

                    if (rank <= looseRank) { looseRank = rank; loose = btn; }
                    if (!offscreen && enabled && rank <= strictRank) { strictRank = rank; strict = btn; }
                }

                var target = strict ?? loose;
                if (target == null)
                {
                    Diag($"  attempt {attempt}: {buttons.Count} buttons, no match for digit {digit}");
                    if (attempt == 7 && allNames.Count > 0)
                        Diag("  all button names: " + string.Join(" | ", allNames));
                    continue;
                }
                if (strict == null)
                    Diag($"  soft match (button reported offscreen/disabled) for digit {digit}");

                var matchedName = "?";
                try { matchedName = target.Current.Name; } catch { }

                try
                {
                    var scrollPattern = target.GetCurrentPattern(ScrollItemPattern.Pattern) as ScrollItemPattern;
                    scrollPattern?.ScrollIntoView();
                }
                catch (Exception scrollEx) { Diag($"  scroll failed: {scrollEx.Message}"); }

                // 先 SetFocus 再 Invoke（单纯 Invoke 在 Claude Desktop React 上无效）。
                try { target.SetFocus(); } catch (Exception fe) { Diag($"  SetFocus failed: {fe.Message}"); }
                System.Threading.Thread.Sleep(80);

                bool acted = false;
                try
                {
                    if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                    {
                        ((InvokePattern)ip).Invoke();
                        acted = true;
                    }
                    else
                    {
                        Diag($"  '{matchedName}' has no InvokePattern; retrying");
                    }
                }
                catch (Exception invEx)
                {
                    Diag($"  invoke failed on '{matchedName}': {invEx.GetType().Name}: {invEx.Message}");
                }

                if (acted)
                {
                    Diag($"  clicked '{matchedName}' for digit {digit} after {attempt + 1} attempt(s)");
                    _logger?.LogInformation(
                        "Claude Desktop permission: clicked '{Btn}' for digit {Digit}", matchedName, digit);
                    return true;
                }
                // Invoke 没成 —— 继续重试（可能 React 还没挂好 handler）
            }

            Diag($"all 8 attempts exhausted, no clickable button for digit {digit}");
            _logger?.LogInformation(
                "Claude Desktop permission button for digit {Digit} not found/clickable", digit);
        }
        catch (Exception ex)
        {
            Diag($"unexpected exception: {ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(ex, "Failed to respond to Claude Desktop permission");
        }
        return false;
    }

    /// <summary>
    /// 桌面端 AskUserQuestion 回答：Claude Desktop（Electron）里 AskUserQuestion 弹窗
    /// 渲染为 —— 标题(问题) + 每个选项一行(label 粗体 + description + 右侧数字徽章
    /// 1/2/3) + Other 行(4，自由文本) + Skip 按钮 + Submit 按钮(带 Enter 提示)。
    /// 用户希望岛上点"香蕉/苹果/西瓜"等真实选项后，在 Claude Desktop 里选中并提交。
    ///
    /// 复用 <see cref="RespondInClaudeDesktop"/> 的 *已验证* 结构：
    ///   ActivateClaudeDesktopWindow(null) → 收窄到主窗口子树的 UIA FindAll →
    ///   归一化宽松匹配 → 跳过离屏作 SOFT 偏好(软回退) → SetFocus()+Invoke() →
    ///   重试退避 → 全量诊断写 %TEMP%\openisland-uia.log（前缀 <c>askq:</c>）。
    ///
    /// ⚠ 匹配 *刻意宽松且重日志*：Claude Desktop 的 AskUserQuestion 选项行 / Submit
    /// 按钮的 UIA accessible name 我们 *还没有* 实测确认（与当初权限按钮一样）。
    /// 选项行匹配：归一化名字 *包含 label* 或 *以编号结尾* 或 *同时含 label 与编号*；
    /// Submit 按钮：名字含 "submit"/"提交"；Skip：名字是/含 "skip"/"跳过"。
    /// 每个候选元素名（按钮 + 列表项 + 任意可点）都写进日志 —— 跑一次真实测试即可
    /// 暴露真实结构，再据此收紧匹配（与当初破解权限按钮完全一样的诊断打法）。
    ///
    /// optionNumber: 1-based 选项序号；skip=true 时点 Skip（忽略 optionNumber/label）。
    /// 返回是否成功点到（选项行点到即算成功，Submit/Enter 为尽力补刀）。
    /// </summary>
    public bool AnswerQuestionInClaudeDesktop(
        string sessionTitle, int optionNumber, string optionLabel, bool skip)
    {
        var diagPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openisland-uia.log");
        void Diag(string msg) { try { System.IO.File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] askq: {msg}\n"); } catch { } }

        Diag($"begin: optionNumber={optionNumber} label='{optionLabel}' skip={skip} session='{sessionTitle}'");

        // 不做侧边栏导航：AskUserQuestion 弹窗就在当前活动会话里（与权限路径同理）。
        if (!ActivateClaudeDesktopWindow(null))
        {
            Diag("ActivateClaudeDesktopWindow failed");
            return false;
        }

        var claudePids = new HashSet<int>(
            System.Diagnostics.Process.GetProcessesByName("claude").Select(p => p.Id));
        if (claudePids.Count == 0) { Diag("no claude.exe process"); return false; }

        // 归一化：去空白 + 转小写。选项行匹配要"包含 label"或"以编号结尾"，
        // 不能用精确等值（行文本含 label+description+编号，不会等于任何单值）。
        static string Norm(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

        var normLabel = Norm(optionLabel ?? "");
        var numStr = optionNumber.ToString();

        var winHwnd = FindClaudeDesktopMainHwnd();
        Diag($"window hwnd=0x{winHwnd.ToInt64():X} (scoped UIA search)");

        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                System.Threading.Thread.Sleep(250);

                // 选项行不一定是 ControlType.Button —— Claude Desktop 用 React，
                // 行可能是 ListItem / Text / Group / Hyperlink 等。所以 *不按 ControlType
                // 过滤*，FindAll 拿子树所有元素（收窄到主窗口子树，避免全桌面 COM 超时），
                // 在遍历时按 name 宽松匹配 + 记录全部候选名供日后收紧。
                AutomationElementCollection all;
                try
                {
                    var searchRoot = winHwnd != IntPtr.Zero
                        ? AutomationElement.FromHandle(winHwnd)
                        : AutomationElement.RootElement;
                    all = searchRoot.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                }
                catch (Exception findEx)
                {
                    Diag($"  attempt {attempt}: FindAll threw {findEx.GetType().Name}: {findEx.Message}");
                    continue;
                }

                // 候选打分（越大越优先）。三段：
                //  3 = 名字同时含 label 与编号（最强信号，几乎确定是目标行）
                //  2 = 名字含非空 label
                //  1 = 名字以编号结尾（label 为空 / 没对上时退而求其次）
                // strict（可用 + 非离屏）优先于 loose（软回退，Claude Desktop 长跑
                // 常把真元素误报 offscreen/disabled，硬跳过会漏点 —— 权限路径同款坑）。
                AutomationElement? strict = null; int strictScore = 0;
                AutomationElement? loose = null; int looseScore = 0;
                AutomationElement? skipStrict = null, skipLoose = null;
                AutomationElement? submitStrict = null, submitLoose = null;
                var allCandidates = new List<string>();

                foreach (AutomationElement el in all)
                {
                    string name; bool offscreen = false, enabled = true;
                    try
                    {
                        name = el.Current.Name;
                        try { offscreen = el.Current.IsOffscreen; } catch { }
                        try { enabled = el.Current.IsEnabled; } catch { }
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    var n = Norm(name);

                    // 诊断：把所有有名字的候选都 dump（含 ControlType），一次真实
                    // 运行就能看清 Claude Desktop AskUserQuestion 的真实 UIA 结构。
                    if (attempt == 0 || attempt == 7)
                    {
                        string ct = "?";
                        try { ct = el.Current.ControlType.ProgrammaticName; } catch { }
                        allCandidates.Add($"[{ct}] off={offscreen} en={enabled} '{name}'");
                    }

                    // Skip 按钮：名字是/含 skip / 跳过
                    if (n == "skip" || n.Contains("skip") || n.Contains("跳过"))
                    {
                        skipLoose ??= el;
                        if (!offscreen && enabled) skipStrict ??= el;
                    }

                    // Submit 按钮：名字含 submit / 提交（屏幕上是 "Submit" + Enter 提示）
                    if (n.Contains("submit") || n.Contains("提交"))
                    {
                        submitLoose ??= el;
                        if (!offscreen && enabled) submitStrict ??= el;
                    }

                    if (skip) continue; // skip 模式只找 Skip 按钮，不评选项行

                    // 选项行评分
                    int score = 0;
                    bool hasLabel = normLabel.Length > 0 && n.Contains(normLabel);
                    bool endsNum = n.EndsWith(numStr, StringComparison.Ordinal);
                    if (hasLabel && (endsNum || n.Contains(numStr))) score = 3;
                    else if (hasLabel) score = 2;
                    else if (endsNum) score = 1;
                    if (score == 0) continue;

                    if (score > looseScore) { looseScore = score; loose = el; }
                    if (!offscreen && enabled && score > strictScore) { strictScore = score; strict = el; }
                }

                if (attempt == 0 || attempt == 7)
                    Diag($"  attempt {attempt}: {all.Count} elems; candidates: " +
                         (allCandidates.Count > 0 ? string.Join(" | ", allCandidates.Take(120)) : "(none with name)"));

                // ── Skip 分支 ──
                if (skip)
                {
                    var skipTarget = skipStrict ?? skipLoose;
                    if (skipTarget == null)
                    {
                        Diag($"  attempt {attempt}: no Skip element yet");
                        continue;
                    }
                    if (InvokeElement(skipTarget, Diag, "Skip"))
                    {
                        Diag($"  clicked Skip after {attempt + 1} attempt(s)");
                        return true;
                    }
                    continue;
                }

                // ── 选项行分支 ──
                var target = strict ?? loose;
                if (target == null)
                {
                    Diag($"  attempt {attempt}: {all.Count} elems, no option-row match " +
                         $"(label='{normLabel}' num={numStr})");
                    continue;
                }
                if (strict == null)
                    Diag($"  soft match (option row reported offscreen/disabled)");

                var matchedName = "?";
                try { matchedName = target.Current.Name; } catch { }

                if (!InvokeElement(target, Diag, $"option#{optionNumber}"))
                {
                    // Invoke 没成 —— 继续重试（React handler 可能还没挂）
                    continue;
                }
                Diag($"  clicked option row '{matchedName}' (num={numStr}) after {attempt + 1} attempt(s)");

                // 选项点中后通常仍需显式 Submit。先找 Submit 按钮点；找不到就给
                // 当前焦点窗口补一个 Enter（屏幕上 Submit 带 "Enter" 提示，回车等价提交）。
                System.Threading.Thread.Sleep(150);
                var submit = submitStrict ?? submitLoose;
                if (submit == null)
                {
                    // 重新扫一遍找 Submit（点选项后 DOM 可能才挂出/启用 Submit）
                    try
                    {
                        var searchRoot = winHwnd != IntPtr.Zero
                            ? AutomationElement.FromHandle(winHwnd)
                            : AutomationElement.RootElement;
                        var rescan = searchRoot.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        foreach (AutomationElement el in rescan)
                        {
                            string nm; try { nm = el.Current.Name; } catch { continue; }
                            if (string.IsNullOrEmpty(nm)) continue;
                            var nn = Norm(nm);
                            if (nn.Contains("submit") || nn.Contains("提交")) { submit = el; break; }
                        }
                    }
                    catch (Exception reEx) { Diag($"  submit rescan threw: {reEx.Message}"); }
                }

                if (submit != null && InvokeElement(submit, Diag, "Submit"))
                {
                    Diag("  clicked Submit");
                }
                else
                {
                    Diag("  no Submit element clickable -> sending Enter to foreground as fallback");
                    SendVk(0x0D); // VK_RETURN —— Claude Desktop 已在前台（ActivateClaudeDesktopWindow）
                }
                return true;
            }

            Diag($"all 8 attempts exhausted (skip={skip} num={numStr} label='{normLabel}')");
            _logger?.LogInformation(
                "Claude Desktop AskUserQuestion: no match (skip={Skip} num={Num})", skip, optionNumber);
        }
        catch (Exception ex)
        {
            Diag($"unexpected exception: {ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(ex, "Failed to answer Claude Desktop AskUserQuestion");
        }
        return false;
    }

    /// <summary>
    /// AnswerQuestionInClaudeDesktop 用：对一个 UIA 元素 ScrollIntoView → SetFocus →
    /// Invoke（Claude Desktop React UI 单纯 Invoke 无效，必须先 SetFocus —— 与
    /// RespondInClaudeDesktop 同款必要步骤）。选项行常不是标准 button（没有
    /// InvokePattern），退回 SelectionItemPattern.Select（单选选项行的常见实现）。
    /// 都没有时给焦点元素补 Enter（已 SetFocus，回车等价激活当前项）。
    /// 返回是否真的触发了某个动作。诊断走传入的 Diag。
    /// （注：WPF 引用的托管 UIAutomation 程序集没有 LegacyIAccessiblePattern，
    ///   故不走它；Invoke + SelectionItem + Enter 兜底已覆盖 React 行/按钮两类。）
    /// </summary>
    private static bool InvokeElement(AutomationElement el, Action<string> Diag, string tag)
    {
        try
        {
            var scroll = el.GetCurrentPattern(ScrollItemPattern.Pattern) as ScrollItemPattern;
            scroll?.ScrollIntoView();
        }
        catch (Exception scrollEx) { Diag($"  [{tag}] scroll failed: {scrollEx.Message}"); }

        try { el.SetFocus(); } catch (Exception fe) { Diag($"  [{tag}] SetFocus failed: {fe.Message}"); }
        System.Threading.Thread.Sleep(80);

        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
                return true;
            }
            // 单选选项行常用 SelectionItemPattern 而非 InvokePattern
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
            {
                ((SelectionItemPattern)sp).Select();
                return true;
            }
            // 既不能 Invoke 也不能 Select，但已 SetFocus —— 补一个 Enter 激活当前焦点项。
            Diag($"  [{tag}] no Invoke/SelectionItem pattern; sending Enter to focused element");
            SendVk(0x0D); // VK_RETURN
            return true;
        }
        catch (Exception invEx)
        {
            Diag($"  [{tag}] invoke failed: {invEx.GetType().Name}: {invEx.Message}");
        }
        return false;
    }

    /// <summary>
    /// 跳转到Windows Terminal
    /// </summary>
    private async Task<bool> JumpToWindowsTerminalAsync(JumpTarget target)
    {
        try
        {
            var workingDir = target.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
            {
                workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            // 构建Windows Terminal命令
            var arguments = $"-d \"{workingDir}\"";

            // 如果指定了profile，可以添加 -p 参数
            // 但目前我们使用默认profile

            _logger?.LogInformation("Starting Windows Terminal with args: {Args}", arguments);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    CreateNoWindow = false
                }
            };

            var started = process.Start();
            if (started)
            {
                // 尝试激活窗口
                await Task.Delay(500);
                ActivateWindow(process.MainWindowHandle);
            }

            return started;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to jump to Windows Terminal");
            return false;
        }
    }

    /// <summary>
    /// 跳转到PowerShell
    /// </summary>
    private async Task<bool> JumpToPowerShellAsync(JumpTarget target)
    {
        try
        {
            var workingDir = target.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
            {
                workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            _logger?.LogInformation("Starting PowerShell in: {Dir}", workingDir);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    CreateNoWindow = false
                }
            };

            return process.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to jump to PowerShell");
            return false;
        }
    }

    /// <summary>
    /// 激活 claude.exe 所在终端窗口后，往焦点窗口发一串按键 —— 用来让岛上"1/2/3"按钮
    /// 等价于在 Claude 终端键入 1/2/3，B 模式下两边任意一侧都能解析权限 prompt。
    /// keys 例: "1\r" 表示按 '1' + Enter。每键之间 20ms 间隔，激活后留 350ms 等焦点稳定。
    ///
    /// 关键不变量：SendInput 不指定目标窗口，注入到 *当前* 前台。所以必须在发键之前确认
    /// 前台 HWND 等于我们刚激活的终端，否则键会被打到错误的应用 —— 典型场景：用户点岛上
    /// "1" 时 Claude Desktop 正在前台，ActivateWindow 因 Windows 反窃焦机制没真正切焦点，
    /// "1" 就被注入到 Claude Desktop 的聊天框里。校验失败就 abort（return false），
    /// 用户看到"没生效"远比"被静默错打到错的窗口"好。
    /// </summary>
    public async Task<bool> SendKeysToTerminalAsync(int claudePid, string keys)
    {
        // 1) 找承载终端的窗口（沿父进程链）
        var targetHwnd = FindTerminalHwndByPidChain(claudePid);
        if (targetHwnd == IntPtr.Zero)
        {
            _logger?.LogWarning(
                "SendKeysToTerminalAsync: no terminal window found for claude pid {Pid}; aborting", claudePid);
            return false;
        }

        // 2) 激活到前台
        ActivateWindow(targetHwnd);
        // AttachThreadInput + topmost flicker 链通常 < 200ms 生效，留 350ms 给慢机也有机会
        await Task.Delay(350);

        // 3) 验前台。失败就 abort，绝不能让 SendInput 把 "1\r" 打到错误的前台应用。
        //    See xmldoc above for full rationale.
        var fg = GetForegroundWindow();
        if (fg != targetHwnd)
        {
            GetWindowThreadProcessId(fg, out var fgPid);
            var fgTitle = new StringBuilder(256);
            GetWindowText(fg, fgTitle, 256);
            _logger?.LogWarning(
                "SendKeysToTerminalAsync: foreground switch failed. target hwnd=0x{Tgt:X} claudePid={Pid} | actual foreground hwnd=0x{Fg:X} pid={FgPid} title='{Title}'. Aborting SendInput to avoid typing into wrong window.",
                targetHwnd.ToInt64(), claudePid, fg.ToInt64(), fgPid, fgTitle.ToString());
            return false;
        }

        // 4) 焦点 OK，发键
        foreach (var c in keys)
        {
            SendVk(CharToVk(c));
            await Task.Delay(20);
        }
        return true;
    }

    /// <summary>
    /// 激活 claude.exe 所在终端窗口后发一次 <b>Shift+Tab</b> —— 用来让岛上"模式切换"
    /// 按钮等价于用户在 Claude 终端按 Shift+Tab 循环权限模式（accept edits / auto /
    /// plan / normal）。Claude Code 没有"直接设为模式 X"的幂等按键，只能循环，所以
    /// 每次点只发一格，精确落点不保证（best-effort，调用方已注释说明）。
    ///
    /// 复用 <see cref="SendKeysToTerminalAsync"/> 同一套不变量：SendInput 注入到 *当前
    /// 前台*，所以必须先把目标终端激活并校验前台 HWND 一致，否则 abort —— 绝不能把
    /// Shift+Tab 打到错误的应用（典型：用户点击时 Claude Desktop 在前台）。
    /// </summary>
    public async Task<bool> SendShiftTabToTerminalAsync(int claudePid)
    {
        var targetHwnd = FindTerminalHwndByPidChain(claudePid);
        if (targetHwnd == IntPtr.Zero)
        {
            _logger?.LogWarning(
                "SendShiftTabToTerminalAsync: no terminal window for claude pid {Pid}; aborting", claudePid);
            return false;
        }

        ActivateWindow(targetHwnd);
        await Task.Delay(350);

        var fg = GetForegroundWindow();
        if (fg != targetHwnd)
        {
            _logger?.LogWarning(
                "SendShiftTabToTerminalAsync: foreground switch failed for claude pid {Pid}; aborting to avoid wrong-window input.",
                claudePid);
            return false;
        }

        SendShiftTab();
        return true;
    }

    private static ushort CharToVk(char c) => c switch
    {
        '\r' or '\n' => 0x0D,                  // VK_RETURN
        >= '0' and <= '9' => (ushort)(0x30 + (c - '0')),
        _ => 0
    };

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_TAB = 0x09;

    /// <summary>
    /// 发一次 Shift+Tab 组合键：SHIFT 按下 → TAB 按下 → TAB 抬起 → SHIFT 抬起。
    /// 一次 SendInput 投递全部 4 个事件，保证修饰键包住 TAB（分多次投递偶尔会被
    /// 终端在中间插入处理，导致 Shift 没被识别成按住）。
    /// </summary>
    private static void SendShiftTab()
    {
        var inputs = new INPUT[4];
        // SHIFT down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_SHIFT;
        // TAB down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_TAB;
        // TAB up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = VK_TAB;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        // SHIFT up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = VK_SHIFT;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendVk(ushort vk)
    {
        if (vk == 0) return;
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 沿 claude.exe 的父进程链向上找承载它的终端窗口并激活。
    /// claude.exe → shell (pwsh/cmd/wsl) → 终端宿主 (WindowsTerminal/conhost/alacritty/...)。
    /// 终端宿主的 HWND 通过枚举进程拥有的可见顶层窗口拿到 —— 不能依赖 Process.MainWindowHandle，
    /// 因为 WindowsTerminal 单进程托多 tab 时 Process.MainWindowHandle 也只指一个窗口，
    /// 而且 conhost 在 WT 模式下根本没主窗口。
    ///
    /// Walk up claude.exe's parent-process chain to find the terminal host hosting it and
    /// activate that window. Returns false if the chain doesn't yield a terminal-process window.
    /// 找不到 → 调用方 fallback 到旧的"标题 substring + 进程名白名单"逻辑。
    /// </summary>
    public bool ActivateTerminalByPidChain(int claudePid)
    {
        var hwnd = FindTerminalHwndByPidChain(claudePid);
        if (hwnd == IntPtr.Zero) return false;
        ActivateWindow(hwnd);
        return true;
    }

    /// <summary>
    /// 末位兜底：父链 / AttachConsole 都没拿到 claude 对应的终端时，根据可见窗口的
    /// 标题给所有终端宿主窗口打分，挑分数最高的激活。
    ///
    /// 适用场景：claude 通过 .lnk / start /B / Win+R / 文件夹右键 "Open in Terminal" 等
    /// 启动，原始 shell 已死或被 detach；OS 级 process tree 找不到承载终端，
    /// 但用户确实能在某个 WT/conhost 窗口里看到这个 claude 的 TUI。
    ///
    /// 评分（仅在分数 &gt; 0 时激活）：
    ///   - 项目目录名命中标题 +100（最强信号；只在 tab 标题被设成项目名时才有；少见但精准）
    ///   - "claude" 字面命中 +10（标题里出现 claude 几乎必为 claude session）
    ///   - Claude Code 活动标记 "✳" +5（正在跑工具的 tab 通常带这个）
    ///
    /// 当用户有多个 WT 窗口（每个独立 claude session）时无法精确区分，但选中带 ✳ 的
    /// "正在工作"窗口仍然比开新终端可用得多。
    ///
    /// Fallback when both parent-chain walk and AttachConsole fail to identify the
    /// terminal hosting claude — score visible terminal-process windows by title and
    /// activate the highest-scoring one.
    /// </summary>
    public bool ActivateTerminalByWindowHeuristic(string? projectDirName)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;
            string procName;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch { return true; }
            if (!TerminalProcessNames.Contains(procName)) return true;

            var ttl = new StringBuilder(512);
            if (GetWindowText(hWnd, ttl, 512) <= 0) return true;
            var title = ttl.ToString();

            int score = 0;
            if (!string.IsNullOrEmpty(projectDirName) &&
                title.IndexOf(projectDirName, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 100;
            if (title.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10;
            if (title.IndexOf('✳') >= 0)
                score += 5;

            if (score > bestScore)
            {
                bestScore = score;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero || bestScore == 0) return false;

        _logger?.LogInformation(
            "Activating terminal by window heuristic: hwnd=0x{Hwnd:X} score={Score} project={Project}",
            best.ToInt64(), bestScore, projectDirName ?? "(none)");
        ActivateWindow(best);
        return true;
    }

    /// <summary>
    /// 父链查找的 HWND 提取版（不激活），给 <see cref="SendKeysToTerminalAsync"/> 用 ——
    /// 它需要先拿到 HWND 才能在 SendInput 之前做前台校验。返回 IntPtr.Zero 表示链上没找到。
    /// </summary>
    private IntPtr FindTerminalHwndByPidChain(int claudePid)
    {
        // 一次 toolhelp 快照拿全机器进程的 parent map，后续父链查找全在内存里走 ——
        // 替代原先逐 hop 走 WMI 的方案。某些 Windows 环境 WMI 服务会偶发卡死（用户机
        // 上观测过 30s+ 不返回 / "远程过程调用失败"），导致点会话卡片时 UI 长期无响应。
        // toolhelp32 是纯 Win32，几 ms 出结果。
        var parentMap = BuildProcessParentMap();
        var visited = new HashSet<int>();
        int? cur = claudePid;
        int? topLevelClaude = null; // 父链中最顶层的 claude.exe，给 AttachConsole 兜底用
        for (int hops = 0; hops < 8 && cur is int pid && pid > 0; hops++)
        {
            if (!visited.Add(pid)) break; // 防成环

            string procName;
            try
            {
                using var p = Process.GetProcessById(pid);
                procName = p.ProcessName;
            }
            catch { break; }

            // 记录沿途的最顶层 claude.exe —— 防止 ResolveClaudeProcessId 选中子 claude
            // 时，AttachConsole 兜底走到子 claude 自己 alloc 的 console，而非用户的主终端。
            // 用 StartsWith 而不是 Equals："claude.exe.old.<ts>"（自更新重命名后的旧
            // exe 仍在运行）也算 claude 进程。Claude Code 在 Windows 上不能直接替换运行中
            // 的 claude.exe，所以会把旧的改名为 claude.exe.old.<timestamp> 放着继续跑。
            if (procName.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
                topLevelClaude = pid;

            if (TerminalProcessNames.Contains(procName))
            {
                var hwnd = FindTopLevelVisibleWindowForProcess(pid);
                if (hwnd != IntPtr.Zero)
                {
                    _logger?.LogInformation(
                        "Resolved terminal {Name}#{Pid} (hops={Hops}) for claude pid {ClaudePid}",
                        procName, pid, hops, claudePid);
                    return hwnd;
                }
            }

            cur = parentMap.TryGetValue(pid, out var parent) && parent > 0 ? (int?)parent : null;
        }

        // 兜底：父链里没有终端进程（典型场景：claude.exe 通过 .lnk / start /B / 文件夹
        // 右键 "Open in Terminal" 启动，父进程是 explorer.exe）。Windows 会给它分配一个
        // conhost.exe —— conhost 是 claude 的 *子* / sibling 进程（由 csrss 创建），父链
        // 向上找永远找不到。改用 OS 级 console 绑定直接拿 conhost 的可见 HWND。
        //
        // Fallback when parent chain doesn't contain a terminal process — e.g., claude.exe
        // started via shortcut/`start` (parent = explorer). Its console host is created as a
        // sibling, not an ancestor, so we use the OS-level console binding to find it.
        if (topLevelClaude is int topPid)
        {
            var consoleHwnd = TryGetConsoleHostWindow(topPid);
            if (consoleHwnd != IntPtr.Zero)
            {
                _logger?.LogInformation(
                    "Resolved console host hwnd=0x{Hwnd:X} for claude pid {ClaudePid} via AttachConsole(top={TopPid})",
                    consoleHwnd.ToInt64(), claudePid, topPid);
                return consoleHwnd;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 用 AttachConsole + GetConsoleWindow 拿目标进程 console host 的窗口句柄。
    /// 处理 claude.exe 不是从已知终端启动（父进程 = explorer 等）的情况 ——
    /// 父链向上找不到终端进程，但 OS 给 claude 分配的 conhost.exe 是 sibling 关系，
    /// 通过 OS 级 console 绑定能直接拿到它的可见 HWND。
    ///
    /// 副作用：调用期间本进程短暂 attach 到目标 console，但不重定向 std handles，
    /// 所以不干扰 claude 的 IO，也不污染本进程的 stdin/stdout（WPF 进程根本不用它们）。
    ///
    /// 限制：ConPTY（Windows Terminal / Alacritty / WezTerm）下 GetConsoleWindow 返回
    /// 隐藏的伪窗口，用 IsWindowVisible 过滤掉。这些宿主走父链查找路径已经能命中，
    /// 不需要本 fallback。
    /// </summary>
    private IntPtr TryGetConsoleHostWindow(int pid)
    {
        // 防御性 detach：如果本进程已挂在别的 console（极少见：dotnet run 从终端启动
        // 会继承宿主 console），AttachConsole 会以 ERROR_ACCESS_DENIED 失败。
        // WPF 应用通常没有 console，FreeConsole 是 no-op，无副作用。
        FreeConsole();
        if (!AttachConsole(pid)) return IntPtr.Zero;
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (!IsWindowVisible(hwnd)) return IntPtr.Zero;
            return hwnd;
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>
    /// 用 CreateToolhelp32Snapshot 一次拿全系统进程 → 父进程 PID 的映射表。
    /// 替代原先逐 hop 走 WMI 的方案 —— WMI 在某些环境会偶发卡几十秒（observed:
    /// "远程过程调用失败" / RPC 调用超时），导致 JumpToSessionAsync 整个挂死。
    /// toolhelp32 是纯 Win32 内核 API，几 ms 完成，无 IPC 依赖。
    /// </summary>
    private static Dictionary<int, int> BuildProcessParentMap()
    {
        var map = new Dictionary<int, int>(256);
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        // INVALID_HANDLE_VALUE = -1
        if (snap == IntPtr.Zero || snap.ToInt64() == -1) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>
    /// 枚举所有可见顶层窗口，返回第一个属于指定 PID 的窗口句柄。
    /// </summary>
    private static IntPtr FindTopLevelVisibleWindowForProcess(int targetPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)targetPid)
            {
                // 进一步要求窗口有标题，避开隐藏的工具窗口
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) > 0)
                {
                    found = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 尝试通过进程ID找到终端窗口并激活
    /// </summary>
    public async Task<bool> ActivateTerminalWindowAsync(int? processId)
    {
        if (processId == null) return false;

        try
        {
            var process = Process.GetProcessById(processId.Value);
            if (process.HasExited) return false;

            // 等待窗口句柄可用
            for (int i = 0; i < 10; i++)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ActivateWindow(process.MainWindowHandle);
                    return true;
                }
                await Task.Delay(100);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 根据窗口标题查找并激活终端窗口
    /// </summary>
    public bool ActivateWindowByTitle(string titleSubstring)
    {
        if (string.IsNullOrEmpty(titleSubstring)) return false;

        try
        {
            var processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowTitle.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            ActivateWindow(process.MainWindowHandle);
                            return true;
                        }
                    }
                }
                catch { /* 忽略访问被拒绝的进程 */ }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    #region Windows API

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // AttachConsole / FreeConsole / GetConsoleWindow：用于 TryGetConsoleHostWindow，
    // 通过 OS 级 console 绑定查找 claude.exe 关联的 conhost 窗口（父链查不到时的兜底）。
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    // CreateToolhelp32Snapshot 系列：用于 BuildProcessParentMap 一次性拿全系统进程的
    // ParentProcessId（替代易卡死的 WMI 查询）。纯 Win32 内核 API，无 IPC 依赖。
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // SendInput 用的常量 + 结构（标准 union 布局以兼容 x64 上 MOUSEINPUT/KEYBDINPUT 大小差异）
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        // 1) 还原 / 显示。同步 ShowWindow 比异步 ShowWindowAsync 对 UWP / Electron 包装窗口
        // 更可靠（异步偶尔被消息循环 dropped）。
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);
        else
            ShowWindow(hWnd, SW_SHOW);

        // 2) AttachThreadInput 绕开 SetForegroundWindow 12s 失活限制
        uint currentThread = GetCurrentThreadId();
        uint windowThread = GetWindowThreadProcessId(hWnd, out _);
        if (currentThread != windowThread)
            AttachThreadInput(currentThread, windowThread, true);

        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        // SwitchToThisWindow(true) 模拟 Alt+Tab 切换 —— 对 UWP / 受保护窗口比 SetForegroundWindow 更激进
        SwitchToThisWindow(hWnd, true);

        // 3) TOPMOST 闪一下强制置前，再回常规层级
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        if (currentThread != windowThread)
            AttachThreadInput(currentThread, windowThread, false);
    }

    #endregion
}
