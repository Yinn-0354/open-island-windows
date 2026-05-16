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
/// - GPU: 走 PDH "英文计数器" API（PdhAddEnglishCounter，与系统语言无关）查询
///   "\GPU Engine(*)\Utilization Percentage"，累加 3D/Graphics/Compute 引擎；
///   PDH 初始化失败再退回托管 PerformanceCounter（按本地化类目名兜底）；
///   仍失败（无独显驱动计数器 / 访问拒绝）则上报 -1，UI 显示 "—"
///
///   * 为何不直接用托管 PerformanceCounter："GPU Engine" 是英文类目名，在
///     非英文（如简体中文 zh-CN）Windows 上托管层无法把英文名解析到本地化
///     PerfLib 类目，cat.GetInstanceNames() 会抛 "Category does not exist"，
///     于是 GPU 永远不可用。PDH 的 PdhAddEnglishCounter 在任意语言下都按英文
///     名解析，故作为首选。
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

    // GPU 采集状态（懒加载；不可用则永久关闭，避免每 tick 重试卡顿）
    private bool _gpuInitTried;
    private bool _gpuAvailable;
    // 首选：PDH 英文计数器查询（与系统语言无关）。句柄常驻，Dispose 释放。
    private IntPtr _pdhQuery;          // PDH 查询句柄
    private IntPtr _pdhGpuCounter;     // \GPU Engine(*)\Utilization Percentage
    private bool _gpuViaPdh;
    private bool _gpuPdhPrimed;        // 是否已采过第一帧（PDH 速率计数器首帧无意义）
    // 兜底：托管 PerformanceCounter（按本地化类目名）。
    private List<PerformanceCounter>? _gpuCounters;

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

    // 只统计 3D / Graphics / Compute 引擎，跳过 video encode/decode/copy 等噪声
    // instance（避免总和虚高）。instance 名形如
    // "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D"，engtype_* 段不被本地化。
    private static bool IsCountedGpuEngine(string instanceName)
        => instanceName.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0
        || instanceName.IndexOf("engtype_Graphics", StringComparison.OrdinalIgnoreCase) >= 0
        || instanceName.IndexOf("engtype_Compute", StringComparison.OrdinalIgnoreCase) >= 0;

    private double ReadGpuPercent()
    {
        if (!_gpuInitTried)
        {
            _gpuInitTried = true;
            // 1) 首选 PDH 英文计数器：与系统语言无关，无需管理员权限。
            _gpuViaPdh = TryInitGpuPdh();
            // 2) PDH 不可用才退回托管 PerformanceCounter（按本地化类目名兜底）。
            if (!_gpuViaPdh)
                TryInitGpuManaged();
            _gpuAvailable = _gpuViaPdh || (_gpuCounters != null && _gpuCounters.Count > 0);
        }

        if (!_gpuAvailable) return -1;

        if (_gpuViaPdh)
            return ReadGpuPdh();

        // —— 托管兜底路径 ——
        if (_gpuCounters == null) return -1;
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

    /// <summary>
    /// 用 PDH 英文计数器 API 打开 "\GPU Engine(*)\Utilization Percentage" 查询。
    /// PdhAddEnglishCounter 在任意系统语言下都按英文名解析，绕开托管
    /// PerformanceCounter 在非英文 Windows 上无法解析英文类目名的问题。
    /// 句柄常驻 _pdhQuery/_pdhGpuCounter，Dispose() 释放。
    /// </summary>
    private bool TryInitGpuPdh()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != PDH_OK)
                return false;
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage",
                    IntPtr.Zero, out var counter) != PDH_OK)
            {
                PdhCloseQuery(query);
                return false;
            }
            // 第一帧只为建立基线（速率型计数器首帧无意义）；故意不计 priming。
            PdhCollectQueryData(query); // 失败也无妨，下个 tick 还会再采
            _pdhQuery = query;
            _pdhGpuCounter = counter;
            _gpuPdhPrimed = false;
            return true;
        }
        catch
        {
            // pdh.dll 缺失 / P/Invoke 失败等 —— 交给托管兜底
            return false;
        }
    }

    /// <summary>
    /// 每 tick 采一帧 PDH 数据，把所有 GPU Engine instance 的利用率按
    /// 3D/Graphics/Compute 过滤后累加，clamp 到 0..100。首帧返回 0（基线）。
    /// </summary>
    private double ReadGpuPdh()
    {
        try
        {
            if (PdhCollectQueryData(_pdhQuery) != PDH_OK)
                return _gpuPdhPrimed ? 0 : -1;

            if (!_gpuPdhPrimed)
            {
                // 至此已采到第二帧，速率才有意义；本帧之后开始读数
                _gpuPdhPrimed = true;
            }

            // 先用 0 长度探到所需缓冲大小
            uint bufSize = 0, itemCount = 0;
            uint st = PdhGetFormattedCounterArrayW(
                _pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, IntPtr.Zero);
            if (st != PDH_MORE_DATA || bufSize == 0 || itemCount == 0)
                return 0; // 暂时没有 instance（或刚 priming）—— 报 0 而非不可用

            IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                st = PdhGetFormattedCounterArrayW(
                    _pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, buf);
                if (st != PDH_OK)
                    return 0;

                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                double sum = 0;
                for (int i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(
                        buf + i * stride);
                    if (item.FmtValue.CStatus != PDH_OK) continue; // 该 instance 本帧无效
                    if (string.IsNullOrEmpty(item.szName)) continue;
                    if (!IsCountedGpuEngine(item.szName)) continue;
                    sum += item.FmtValue.doubleValue;
                }
                return Math.Clamp(sum, 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 托管兜底：扫描所有计数器类目，找类目名（不区分大小写）含 "GPU" 的那个
    /// （非英文系统上它是本地化名，如 "GPU 引擎"）。instance 名不被本地化，
    /// 仍按 engtype 过滤。注意：此路径在部分非英文 Windows 上 .NET 枚举不出
    /// GPU 类目（此时只能靠上面的 PDH），保留它是为了覆盖 PDH 不可用的环境。
    /// </summary>
    private void TryInitGpuManaged()
    {
        try
        {
            PerformanceCounterCategory? gpuCat = null;
            foreach (var cat in PerformanceCounterCategory.GetCategories())
            {
                if (cat.CategoryName.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    gpuCat = cat;
                    break;
                }
            }
            if (gpuCat == null) return;

            var instances = gpuCat.GetInstanceNames();
            var counters = new List<PerformanceCounter>();
            foreach (var inst in instances)
            {
                if (!IsCountedGpuEngine(inst)) continue;
                try
                {
                    // 同一 instance 下通常有两个计数器：累计型 "Running Time"
                    // （本地化名，CounterType 为 *Timer*/100Ns）和 0..100 的
                    // "Utilization Percentage"。本地化后不能靠英文名匹配，按
                    // 计数器类型挑那个百分比型的（非 *Timer* 的那条）。
                    PerformanceCounter? util = null;
                    foreach (var c in gpuCat.GetCounters(inst))
                    {
                        var typeName = c.CounterType.ToString();
                        if (typeName.IndexOf("Timer", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue; // 跳过累计运行时间
                        util = c;
                    }
                    if (util != null) counters.Add(util);
                }
                catch { /* 该 instance 取计数器失败，跳过 */ }
            }
            _gpuCounters = counters.Count > 0 ? counters : null;
        }
        catch
        {
            _gpuCounters = null;
        }
    }

    // ===== PDH P/Invoke =====
    private const uint PDH_OK = 0x00000000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath,
        IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat,
        ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr ItemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_DOUBLE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string szName;
        public PDH_FMT_COUNTERVALUE_DOUBLE FmtValue;
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
        // 释放 PDH 查询（会一并释放其下所有 counter 句柄）
        if (_pdhQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_pdhQuery); } catch { }
            _pdhQuery = IntPtr.Zero;
            _pdhGpuCounter = IntPtr.Zero;
        }
        if (_gpuCounters != null)
        {
            foreach (var c in _gpuCounters)
            {
                try { c.Dispose(); } catch { }
            }
        }
    }
}
