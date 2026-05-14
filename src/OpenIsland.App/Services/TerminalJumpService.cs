using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
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
                var args = $"-d \"{cwd}\" powershell.exe -NoProfile -NoExit -Command \"claude --resume {sessionId}\"";
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
    public bool ActivateClaudeDesktopWindow()
    {
        var claudePids = new HashSet<int>(
            Process.GetProcessesByName("claude").Select(p => p.Id));

        IntPtr foundHwnd = IntPtr.Zero;

        // 第一遍：claude 进程持窗
        if (claudePids.Count > 0)
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!claudePids.Contains((int)pid)) return true;
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) <= 0) return true;
                foundHwnd = hWnd;
                return false;
            }, IntPtr.Zero);
        }

        // 第二遍：标题含 Claude 的可见窗口
        if (foundHwnd == IntPtr.Zero)
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) <= 0) return true;
                var title = sb.ToString();
                if (title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (title.IndexOf("Open Island", StringComparison.OrdinalIgnoreCase) >= 0) return true; // 排除自己
                foundHwnd = hWnd;
                return false;
            }, IntPtr.Zero);
        }

        if (foundHwnd == IntPtr.Zero) return false;
        ActivateWindow(foundHwnd);
        return true;
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

    private static ushort CharToVk(char c) => c switch
    {
        '\r' or '\n' => 0x0D,                  // VK_RETURN
        >= '0' and <= '9' => (ushort)(0x30 + (c - '0')),
        _ => 0
    };

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
    /// 父链查找的 HWND 提取版（不激活），给 <see cref="SendKeysToTerminalAsync"/> 用 ——
    /// 它需要先拿到 HWND 才能在 SendInput 之前做前台校验。返回 IntPtr.Zero 表示链上没找到。
    /// </summary>
    private IntPtr FindTerminalHwndByPidChain(int claudePid)
    {
        var visited = new HashSet<int>();
        int? cur = claudePid;
        for (int hops = 0; hops < 8 && cur is int pid && pid > 0; hops++)
        {
            if (!visited.Add(pid)) break; // 防成环

            string procName;
            try
            {
                using var p = Process.GetProcessById(pid);
                procName = p.ProcessName;
            }
            catch { return IntPtr.Zero; }

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

            cur = GetParentProcessId(pid);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// WMI 查父 PID。一次性的同步调用，相对廉价（&lt;5ms 内本机）。
    /// </summary>
    private static int? GetParentProcessId(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            using var objects = searcher.Get();
            var obj = objects.Cast<ManagementObject>().FirstOrDefault();
            if (obj == null) return null;
            var raw = obj["ParentProcessId"];
            if (raw != null && int.TryParse(raw.ToString(), out var p) && p > 0) return p;
        }
        catch { /* 父进程查询失败：返回 null 让调用方退出循环 */ }
        return null;
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
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
