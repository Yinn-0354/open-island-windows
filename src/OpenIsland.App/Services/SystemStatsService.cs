using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace OpenIsland.App.Services;

/// <summary>
/// 系统状态快照 —— CPU / 内存 / GPU 占用百分比 + 网络上下行速率（字节/秒）。
/// </summary>
public record SystemStatsSnapshot
{
    public double CpuPercent { get; init; }
    public double MemPercent { get; init; }
    /// <summary>GPU 占用，-1 表示当前环境拿不到（无 "GPU Engine" 计数器）。</summary>
    public double GpuPercent { get; init; } = -1;
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }
}

/// <summary>
/// 后台定时（1s）采集 Windows 系统资源占用。给灵动岛状态栏用。
///
/// - CPU: GetSystemTimes 两次采样差值算占用（不走 PerformanceCounter —— 后者首次
///   读固定返回 0、且类目初始化慢）
/// - 内存: GlobalMemoryStatusEx.dwMemoryLoad（系统物理内存占用 %，开箱即用）
/// - GPU: PerformanceCounter "GPU Engine\Utilization Percentage" 累加所有 instance，
///   失败（无独显驱动计数器 / 访问拒绝）则上报 -1，UI 显示 "—"
/// - 网络: NetworkInterface 累计字节差值 / 时间差
/// </summary>
public sealed class SystemStatsService : IDisposable
{
    private readonly System.Timers.Timer _timer;

    // CPU 采样状态
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuBaseline;

    // 网络采样状态
    private long _prevRx, _prevTx;
    private DateTime _prevNetSampleUtc;
    private bool _hasNetBaseline;

    // GPU 计数器（懒加载；不可用则永久关闭，避免每 tick 重试卡顿）
    private List<PerformanceCounter>? _gpuCounters;
    private bool _gpuInitTried;
    private bool _gpuAvailable;

    public event EventHandler<SystemStatsSnapshot>? StatsUpdated;

    public SystemStatsService()
    {
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
        _timer.Start();
        // 立即跑一次，建立 CPU/网络基线（首帧数据下一 tick 才准，但能尽快出 UI）
        Sample();
    }

    private void Sample()
    {
        try
        {
            var snapshot = new SystemStatsSnapshot
            {
                CpuPercent = ReadCpuPercent(),
                MemPercent = ReadMemPercent(),
                GpuPercent = ReadGpuPercent(),
                NetDownBytesPerSec = 0,
                NetUpBytesPerSec = 0
            };
            var (down, up) = ReadNetBytesPerSec();
            snapshot = snapshot with { NetDownBytesPerSec = down, NetUpBytesPerSec = up };
            StatsUpdated?.Invoke(this, snapshot);
        }
        catch
        {
            /* 单次采样失败不应崩溃定时器 —— 下个 tick 再来 */
        }
    }

    #region CPU

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return 0;

        ulong idle = ToUlong(idleFt);
        ulong kernel = ToUlong(kernelFt);
        ulong user = ToUlong(userFt);

        if (!_hasCpuBaseline)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuBaseline = true;
            return 0;
        }

        // kernel time 含 idle time，所以 total = (kernel - idle) + user 才是真正"忙"
        ulong idleDelta = idle - _prevIdle;
        ulong kernelDelta = kernel - _prevKernel;
        ulong userDelta = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        ulong total = kernelDelta + userDelta;
        if (total == 0) return 0;
        double busy = (double)(total - idleDelta) / total * 100.0;
        return Math.Clamp(busy, 0, 100);
    }

    private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    #endregion

    #region Memory

    private double ReadMemPercent()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return 0;
        return mem.dwMemoryLoad; // 已是 0-100 的系统物理内存占用
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion

    #region GPU

    private double ReadGpuPercent()
    {
        if (!_gpuInitTried)
        {
            _gpuInitTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var instances = cat.GetInstanceNames();
                _gpuCounters = new List<PerformanceCounter>();
                foreach (var inst in instances)
                {
                    // 只统计 3D / Compute / Graphics 引擎，跳过 video encode/decode 等
                    // 噪声 instance（避免总和虚高）。instance 名形如
                    // "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D"
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0 &&
                        inst.IndexOf("engtype_Graphics", StringComparison.OrdinalIgnoreCase) < 0 &&
                        inst.IndexOf("engtype_Compute", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _gpuCounters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst));
                }
                _gpuAvailable = _gpuCounters.Count > 0;
            }
            catch
            {
                _gpuAvailable = false;
                _gpuCounters = null;
            }
        }

        if (!_gpuAvailable || _gpuCounters == null) return -1;

        try
        {
            double sum = 0;
            foreach (var c in _gpuCounters)
            {
                try { sum += c.NextValue(); } catch { /* 某 instance 已消失，跳过 */ }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    #endregion

    #region Network

    private (double down, double up) ReadNetBytesPerSec()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = ni.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch
        {
            return (0, 0);
        }

        var now = DateTime.UtcNow;
        if (!_hasNetBaseline)
        {
            _prevRx = rx;
            _prevTx = tx;
            _prevNetSampleUtc = now;
            _hasNetBaseline = true;
            return (0, 0);
        }

        double seconds = (now - _prevNetSampleUtc).TotalSeconds;
        if (seconds <= 0) return (0, 0);

        double down = Math.Max(0, (rx - _prevRx) / seconds);
        double up = Math.Max(0, (tx - _prevTx) / seconds);

        _prevRx = rx;
        _prevTx = tx;
        _prevNetSampleUtc = now;

        return (down, up);
    }

    #endregion

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        if (_gpuCounters != null)
        {
            foreach (var c in _gpuCounters)
            {
                try { c.Dispose(); } catch { }
            }
        }
    }
}
