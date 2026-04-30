using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Services;

/// <summary>
/// 进程监控服务 - 检测真正运行中的 AI Agent 进程
/// </summary>
public class ProcessMonitorService : IDisposable
{
    private readonly Timer _monitorTimer;
    private readonly Dictionary<int, RunningSessionInfo> _runningSessions = new();
    private readonly object _lock = new();

    public event EventHandler? RunningSessionsChanged;

    public ProcessMonitorService()
    {
        // 每3秒扫描一次进程
        _monitorTimer = new Timer(_ => ScanProcesses(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 获取当前运行中的会话列表
    /// </summary>
    public IReadOnlyCollection<RunningSessionInfo> GetRunningSessions()
    {
        lock (_lock)
        {
            return _runningSessions.Values.ToList();
        }
    }

    /// <summary>
    /// 获取运行中的会话数量
    /// </summary>
    public int GetRunningCount()
    {
        lock (_lock)
        {
            return _runningSessions.Count;
        }
    }

    /// <summary>
    /// 检查指定会话ID对应的进程是否仍在运行
    /// </summary>
    public bool IsSessionRunning(string sessionId)
    {
        lock (_lock)
        {
            return _runningSessions.Values.Any(s =>
                s.SessionId == sessionId ||
                s.WorkingDirectory?.Contains(sessionId) == true);
        }
    }

    /// <summary>
    /// 获取会话的工作目录（如果进程在运行）
    /// </summary>
    public string? GetSessionWorkingDirectory(string sessionId)
    {
        lock (_lock)
        {
            var session = _runningSessions.Values.FirstOrDefault(s =>
                s.SessionId == sessionId ||
                s.WorkingDirectory?.Contains(sessionId) == true);
            return session?.WorkingDirectory;
        }
    }

    private void ScanProcesses()
    {
        try
        {
            var currentSessions = new Dictionary<int, RunningSessionInfo>();
            var processes = Process.GetProcesses();

            foreach (var process in processes)
            {
                try
                {
                    if (IsClaudeCodeProcess(process))
                    {
                        var info = ExtractSessionInfo(process);
                        if (info != null)
                        {
                            currentSessions[process.Id] = info;
                        }
                    }
                }
                catch { /* 忽略无法访问的进程 */ }
            }

            lock (_lock)
            {
                var hasChanges = false;

                // 检查是否有新会话
                foreach (var (pid, info) in currentSessions)
                {
                    if (!_runningSessions.ContainsKey(pid))
                    {
                        hasChanges = true;
                        break;
                    }
                }

                // 检查是否有会话结束
                if (!hasChanges)
                {
                    foreach (var pid in _runningSessions.Keys)
                    {
                        if (!currentSessions.ContainsKey(pid))
                        {
                            hasChanges = true;
                            break;
                        }
                    }
                }

                _runningSessions.Clear();
                foreach (var (pid, info) in currentSessions)
                {
                    _runningSessions[pid] = info;
                }

                if (hasChanges)
                {
                    RunningSessionsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanProcesses error: {ex.Message}");
        }
    }

    private bool IsClaudeCodeProcess(Process process)
    {
        try
        {
            var name = process.ProcessName.ToLowerInvariant();

            // 直接匹配 claude 进程
            if (name.Contains("claude"))
            {
                return true;
            }

            // node 进程需要检查命令行
            if (name == "node" || name.Contains("node"))
            {
                var cmdLine = GetCommandLine(process);
                if (cmdLine != null)
                {
                    return cmdLine.Contains("claude") ||
                           cmdLine.Contains("@anthropic") ||
                           cmdLine.Contains("codex-cli") ||
                           cmdLine.Contains("codex");
                }
            }
        }
        catch { }

        return false;
    }

    private RunningSessionInfo? ExtractSessionInfo(Process process)
    {
        try
        {
            var info = new RunningSessionInfo
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                StartTime = process.StartTime,
                Tool = AgentTool.ClaudeCode
            };

            // 获取命令行参数 + 父 PID（一次 WMI 查询拿全）
            var (cmdLine, parentPid) = GetCommandLineAndParent(process);
            info.ParentProcessId = parentPid;

            // 1. 优先从进程的主窗口标题提取
            if (!string.IsNullOrEmpty(process.MainWindowTitle))
            {
                var titleParts = process.MainWindowTitle.Split(new[] { ':' }, 2);
                if (titleParts.Length == 2)
                {
                    var titleDir = titleParts[0].Trim();
                    if (Directory.Exists(titleDir))
                    {
                        info.WorkingDirectory = titleDir;
                    }
                }
            }

            if (!string.IsNullOrEmpty(cmdLine))
            {
                info.CommandLine = cmdLine;

                // 2. 从命令行提取工作目录
                if (string.IsNullOrEmpty(info.WorkingDirectory))
                {
                    // 模式: --cwd=path 或 --cwd path
                    var cwdMatch = Regex.Match(cmdLine, @"--cwd[=\s]([^\s]+)", RegexOptions.IgnoreCase);
                    if (cwdMatch.Success)
                    {
                        var cwd = cwdMatch.Groups[1].Value.Trim('"');
                        if (Directory.Exists(cwd))
                            info.WorkingDirectory = cwd;
                    }
                }

                if (string.IsNullOrEmpty(info.WorkingDirectory) &&
                    (cmdLine.Contains("claude") || cmdLine.Contains("codex")))
                {
                    var parts = cmdLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = parts.Length - 1; i >= 0; i--)
                    {
                        var part = parts[i].Trim('"');
                        if ((part.Length >= 2 && char.IsLetter(part[0]) && part[1] == ':') &&
                            !part.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                            Directory.Exists(part))
                        {
                            info.WorkingDirectory = part;
                            break;
                        }
                    }
                }
            }

            // 3. 读 claude.exe 的 PEB 拿真实 cwd —— 这是多 claude 场景下分辨"哪个 claude 对应哪个
            //    session 卡片"的唯一可靠路径。WMI 不暴露 cwd，Process.MainWindowTitle 对 claude.exe
            //    永远为空，而 PEB.ProcessParameters.CurrentDirectory.DosPath 准确无误。
            //    PEB read is the only reliable way to map a running claude.exe to its on-disk cwd
            //    on Windows—WMI doesn't expose it and claude has no main window title.
            if (string.IsNullOrEmpty(info.WorkingDirectory))
            {
                var pebCwd = TryReadProcessCwd(process.Id);
                if (!string.IsNullOrEmpty(pebCwd) && Directory.Exists(pebCwd))
                    info.WorkingDirectory = pebCwd;
            }

            // 4. 最后后备：尝试从进程获取当前工作目录
            if (string.IsNullOrEmpty(info.WorkingDirectory))
            {
                try
                {
                    info.WorkingDirectory = process.StartInfo.WorkingDirectory;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(info.WorkingDirectory))
            {
                info.SessionId = GenerateSessionId(info.WorkingDirectory);
                info.ProjectName = Path.GetFileName(info.WorkingDirectory);
            }
            else
            {
                info.SessionId = $"pid-{process.Id}";
            }

            System.Diagnostics.Debug.WriteLine(
                $"ExtractSessionInfo: pid={process.Id}, name={process.ProcessName}, wd={info.WorkingDirectory ?? "N/A"}, project={info.ProjectName ?? "N/A"}");

            return info;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExtractSessionInfo error: {ex.Message}");
            return null;
        }
    }

    private string? GetCommandLine(Process process) => GetCommandLineAndParent(process).cmdLine;

    /// <summary>
    /// 一次 WMI 查询同时拿命令行和父 PID。父 PID 用于"点会话卡片激活终端"的父链查找。
    /// Single WMI query that returns both CommandLine and ParentProcessId so the parent-PID
    /// chain (used by terminal activation) doesn't pay for a second round-trip.
    /// </summary>
    private (string? cmdLine, int? parentPid) GetCommandLineAndParent(Process process)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ParentProcessId FROM Win32_Process WHERE ProcessId = {process.Id}");
            using var objects = searcher.Get();
            var obj = objects.Cast<ManagementObject>().FirstOrDefault();
            if (obj == null) return (null, null);
            var cmd = obj["CommandLine"]?.ToString();
            int? parent = null;
            var rawParent = obj["ParentProcessId"];
            if (rawParent != null && int.TryParse(rawParent.ToString(), out var p) && p > 0)
                parent = p;
            return (cmd, parent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetCommandLineAndParent error: {ex.Message}");
            return (null, null);
        }
    }

    private string GenerateSessionId(string workingDirectory)
    {
        // 使用工作目录的哈希作为会话ID
        var hash = workingDirectory.GetHashCode(StringComparison.Ordinal).ToString("x8");
        return $"claude-{hash}";
    }

    public void Dispose()
    {
        _monitorTimer?.Dispose();
    }

    #region PEB cwd 读取（仅 64-bit 进程，需 PROCESS_QUERY_INFORMATION + PROCESS_VM_READ）

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr handle, int infoClass, ref PROCESS_BASIC_INFORMATION pbi, int size, out int returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    // 64-bit PEB layout（Windows 10/11 native 64-bit 进程）：
    //   PEB + 0x20 → ProcessParameters (RTL_USER_PROCESS_PARAMETERS*)
    //   ProcessParameters + 0x38 → CurrentDirectory.DosPath (UNICODE_STRING)
    private const int OFFSET_PROCESS_PARAMETERS = 0x20;
    private const int OFFSET_CURRENT_DIRECTORY = 0x38;

    /// <summary>
    /// 读取目标进程 PEB 中的 CurrentDirectory.DosPath。失败返回 null。
    /// 仅适用于 64-bit 目标进程（claude.exe 是 native 64-bit Node 包装），
    /// 对 32-bit Wow64 目标会返回错误偏移的数据 —— 不在我们的部署形态里，先不处理。
    /// </summary>
    private static string? TryReadProcessCwd(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(
                handle, 0, ref pbi,
                System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB → ProcessParameters
            var paramsAddr = ReadPointer(handle, IntPtr.Add(pbi.PebBaseAddress, OFFSET_PROCESS_PARAMETERS));
            if (paramsAddr == IntPtr.Zero) return null;

            // ProcessParameters → CurrentDirectory.DosPath (UNICODE_STRING: 16 bytes on 64-bit)
            //   Length:USHORT (2) | MaximumLength:USHORT (2) | _pad:4 | Buffer:PWSTR (8)
            var unicodeBytes = new byte[16];
            if (!ReadProcessMemory(handle, IntPtr.Add(paramsAddr, OFFSET_CURRENT_DIRECTORY),
                unicodeBytes, 16, out _)) return null;

            ushort length = BitConverter.ToUInt16(unicodeBytes, 0);
            if (length == 0 || length > 32 * 1024) return null;
            var bufferPtr = (IntPtr)BitConverter.ToInt64(unicodeBytes, 8);
            if (bufferPtr == IntPtr.Zero) return null;

            var pathBytes = new byte[length];
            if (!ReadProcessMemory(handle, bufferPtr, pathBytes, length, out _)) return null;

            var path = System.Text.Encoding.Unicode.GetString(pathBytes).TrimEnd('\\', '/', '\0');
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr handle, IntPtr addr)
    {
        var buf = new byte[8];
        return ReadProcessMemory(handle, addr, buf, 8, out _)
            ? (IntPtr)BitConverter.ToInt64(buf, 0)
            : IntPtr.Zero;
    }

    #endregion
}

/// <summary>
/// 运行中的会话信息
/// </summary>
public class RunningSessionInfo
{
    public string SessionId { get; set; } = "";
    public int ProcessId { get; set; }
    /// <summary>
    /// 父进程 PID（来自 WMI Win32_Process.ParentProcessId），用于沿父链向上找
    /// 承载终端的窗口宿主，从而准确激活到 *这个* claude.exe 所在的终端窗口。
    /// Parent PID from WMI; used to walk up to the terminal host window so we can
    /// activate exactly the terminal that's running this claude.exe.
    /// </summary>
    public int? ParentProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? CommandLine { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? ProjectName { get; set; }
    public DateTime StartTime { get; set; }
    public AgentTool Tool { get; set; }
}
