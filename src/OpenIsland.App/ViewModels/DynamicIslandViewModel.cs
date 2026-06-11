using System.Collections.ObjectModel;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIsland.App.Services;
using OpenIsland.Core;
using OpenIsland.Core.Models;

namespace OpenIsland.App.ViewModels;

public interface IIslandSession
{
    string Id { get; }
    string Title { get; }
    string ToolIcon { get; }
    string Elapsed { get; }
    bool NeedsAttention { get; }
    bool ShowApproveButton { get; }
    IRelayCommand JumpCommand { get; }
    IRelayCommand? ApproveCommand { get; }
    IRelayCommand? DenyCommand { get; }
}

public partial class DynamicIslandViewModel : ObservableObject, IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly PopupWindowService _popupService;
    private readonly SystemStatsService _systemStats;
    private readonly PlanUsageService _planUsage;
    private readonly WorkspaceSettings _settings;
    private readonly ScreenshotService _screenshot;
    private readonly SkillInstallService _skillInstall;
    private readonly WebSyncService _webSync;
    private readonly System.Timers.Timer _greenStatusTimer;
    private bool _justCompletedTask = false;

    /// <summary>
    /// 上一次 UpdateStatusColor 算出的聚合 phase —— 用于做"沿边触发"提示音：
    ///   · Running → Idle/Completed  ⇒ 任务完成"叮"（#1）
    ///   · → WaitingForApproval/WaitingForAnswer 且上次不是同一关注态 ⇒ 需关注"叮"（#2）
    /// 只在 *转换发生的那一刻* 响一次，不是每次刷新都响。
    /// Previous aggregate phase, for edge-triggered chimes (fire once on transition).
    /// </summary>
    private SessionPhase? _prevAggregatePhase;

    /// <summary>
    /// 用户用小叉号临时收起的 session。key=sessionId，value=sawQuiet（被收起后
    /// 是否已经走到过 Idle/Completed 安静态）。语义：
    ///   · 被收起后仍 Running（本轮没结束）→ 继续隐藏
    ///   · 走到 Idle/Completed → sawQuiet=true，仍隐藏（本轮活动收尾）
    ///   · 安静后再次 Running（新一轮）→ 移出、重新显示
    ///   · 任意时刻进入 WaitingForApproval/WaitingForAnswer（需关注）→ 立即移出、
    ///     重新显示（阻塞性 prompt 不允许被永久藏掉）
    /// 进程退出 / 重启应用后自然清空（仅内存，符合"临时"语义）。
    /// </summary>
    private readonly Dictionary<string, bool> _dismissed = new();

    /// <summary>被图钉固定的会话 Id —— "清理任务"不会清掉它们（用户显式保留）。</summary>
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);

    // ── 系统状态栏：CPU / 内存 / GPU / 网速 ──
    [ObservableProperty] private string _cpuText = "CPU --";
    [ObservableProperty] private string _memText = "RAM --";
    [ObservableProperty] private string _gpuText = "GPU --";
    [ObservableProperty] private string _netText = "↓-- ↑--";

    // ── Plan usage 行：Claude 订阅 5h 滚动窗口用量（API 模式只显示 token 数） ──
    /// <summary>true = 走 API（按量付费），只显示 token 数，无进度条/重置。</summary>
    [ObservableProperty] private bool _planIsApi;
    /// <summary>左侧标签：Plan / API。</summary>
    [ObservableProperty] private string _planLabel = "Plan";
    /// <summary>Plan 模式百分比文本，如 "62%"。</summary>
    [ObservableProperty] private string _planPercentText = "";
    /// <summary>重置倒计时文本，如 "重置 2h13m"（无活动时空串）。</summary>
    [ObservableProperty] private string _planResetText = "";
    /// <summary>进度条填充比例 0..1。</summary>
    [ObservableProperty] private double _planBarFraction;
    /// <summary>API 模式 token 文本，如 "1.24M tokens"。</summary>
    [ObservableProperty] private string _planValueText = "";
    /// <summary>进度条颜色（hex 字符串，经 StrToBrush 转 Brush）：&lt;70 蓝 / 70-89 橙 / ≥90 红。</summary>
    [ObservableProperty] private string _planBarColor = "#0A84FF";

    // ── 媒体控制栏：上一首 / 播放暂停 / 下一首 / 音量 ──
    private readonly MediaControlService _media;
    private bool _suppressVolumeWriteback;
    /// <summary>系统主音量 0~100，绑滑块。setter 直接写 CoreAudio。</summary>
    [ObservableProperty] private double _volume = 50;

    partial void OnVolumeChanged(double value)
    {
        // 程序化从系统同步过来的不回写（防自激）；只有 UI 拖动才写 CoreAudio
        if (_suppressVolumeWriteback) return;
        _media.SetVolume((float)(value / 100.0));
        PlaySprite?.Invoke("headphones"); // 拖音量 = 在听歌，章鱼戴耳机
    }

    /// <summary>从系统主音量拉一次填到滑块（启动 + 定时同步），不触发回写。</summary>
    private void SyncVolumeFromSystem()
    {
        var v = _media.GetVolume();
        if (v < 0) return;
        _suppressVolumeWriteback = true;
        Volume = Math.Round(v * 100.0);
        _suppressVolumeWriteback = false;
    }

    /// <summary>请求头部小章鱼播放一次某动画（headphones / kamehameha / byebye）。
    /// View 的 code-behind 订阅，调 PixelStatusSprite.PlayOnce。</summary>
    public event Action<string>? PlaySprite;

    /// <summary>VM 内触发小章鱼一次性动画的便捷入口。</summary>
    public void TriggerSprite(string name) => PlaySprite?.Invoke(name);

    [RelayCommand] private void MediaPlayPause() { _media.PlayPause(); PlaySprite?.Invoke("headphones"); }
    [RelayCommand] private void MediaPrev() { _media.Previous(); PlaySprite?.Invoke("headphones"); }
    [RelayCommand] private void MediaNext() { _media.Next(); PlaySprite?.Invoke("headphones"); }

    /// <summary>区域截图：唤起全屏框选覆盖层，松手裁剪并复制到剪贴板（亦可用全局快捷键触发）。</summary>
    [RelayCommand] private void Screenshot() => _screenshot.Capture();

    // ── 余额行 ↔ 最近七天 token 柱状图 切换 ──

    /// <summary>false = 显示 5h 余额（默认）；true = 显示最近七天 token 柱状图。持久化，重启灵动岛恢复关闭时状态。</summary>
    [ObservableProperty] private bool _showUsageChart;

    /// <summary>七天柱状图的 7 根柱子（oldest→newest）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<UsageBar> UsageBars { get; } = new();

    /// <summary>柱状图右侧只显示的"总量"数字（最近七天 token 合计）。</summary>
    [ObservableProperty] private string _usageTotalText = "";

    partial void OnShowUsageChartChanged(bool value)
    {
        if (value) RefreshUsageChart();
    }

    /// <summary>点余额行：在"余额"与"七天柱状图"之间切换，并落盘（下次启动恢复）。</summary>
    [RelayCommand]
    private void ToggleUsageView()
    {
        ShowUsageChart = !ShowUsageChart;     // OnShowUsageChartChanged 负责刷新柱子
        _settings.SetShowUsageChart(ShowUsageChart);
    }

    /// <summary>重算最近七天每天 token 用量 → 柱高 + 颜色（用量越多绿色越深）+ 总量数字。</summary>
    private void RefreshUsageChart()
    {
        var daily = DashboardStats.ComputeDailyTokens(_sessionManager.GetAllSessions(), 7);
        ulong max = 0, total = 0;
        foreach (var d in daily) { if (d.Tokens > max) max = d.Tokens; total += d.Tokens; }

        UsageBars.Clear();
        foreach (var d in daily)
        {
            double frac = max > 0 ? d.Tokens / (double)max : 0;
            double h = d.Tokens > 0 ? Math.Max(3.0, frac * 22.0) : 1.5;
            UsageBars.Add(new UsageBar
            {
                BarHeight = h,
                Color = GreenForFraction(frac, d.Tokens > 0),
                Tooltip = $"{d.Date:MM/dd}  {FormatTokens(d.Tokens)}"
            });
        }
        UsageTotalText = FormatTokens(total);
    }

    /// <summary>用量占比 → 绿色深浅（占比越高越深）。无用量的日给极浅暗绿。</summary>
    private static string GreenForFraction(double frac, bool hasUsage)
    {
        if (!hasUsage) return "#2A3A2E";
        frac = Math.Clamp(frac, 0, 1);
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * frac);
        int r = Lerp(0x66, 0x1B);   // 浅 #66BB6A → 深 #1B5E20
        int g = Lerp(0xBB, 0x5E);
        int b = Lerp(0x6A, 0x20);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // ── 提示音开关（#3）：状态栏里的小喇叭按钮绑这里 ──
    /// <summary>提示音是否开启。初值取自 WorkspaceSettings.SoundEnabled（构造时设）。
    /// 改动经 OnSoundEnabledChanged 落盘 + 同步 SoundService.Enabled。</summary>
    [ObservableProperty] private bool _soundEnabled = true;

    partial void OnSoundEnabledChanged(bool value)
    {
        // 持久化到 settings.json（保留 workspaces / plan5hTokenBudget）+ 同步运行时总开关。
        _settings.SetSoundEnabled(value);
        SoundService.Enabled = value;
    }

    /// <summary>喇叭按钮命令：翻转开关（OnSoundEnabledChanged 负责落盘 + 同步）。</summary>
    [RelayCommand] private void ToggleSound() => SoundEnabled = !SoundEnabled;

    // ── 网页同步（手机/平板访问）：头部地球按钮，手动开关 ──

    /// <summary>网页同步是否开启（开 = 地球图标变绿）。刻意不持久化 —— 监听 0.0.0.0
    /// 是个有暴露面的动作，每次都该由用户显式开启，重启应用默认回到关。</summary>
    [ObservableProperty] private bool _webSyncOn;

    /// <summary>地球按钮 ToolTip：关→功能说明；开→访问地址（已复制到剪贴板）。</summary>
    [ObservableProperty] private string _webSyncTip = "";

    /// <summary>地球按钮命令：开 ↔ 关。开启时把局域网地址复制进剪贴板方便发到手机。</summary>
    [RelayCommand]
    private void ToggleWebSync()
    {
        if (!WebSyncOn)
        {
            try { _webSync.Start(); }
            catch (Exception ex)
            {
                // 端口被占用 / 防火墙策略拒绝等 —— 把原因直接挂在 ToolTip 上，不弹窗打断
                WebSyncTip = "⚠ " + ex.Message;
                return;
            }
            var url = _webSync.GetUrl();
            // 剪贴板可能被其它进程短暂占用 —— 复制失败不影响服务本身已开启
            try { System.Windows.Clipboard.SetText(url); } catch { }
            WebSyncOn = true;
            WebSyncTip = Loc.Format("Web_On", url);
        }
        else
        {
            _webSync.Stop();
            WebSyncOn = false;
            WebSyncTip = Loc.Get("Web_Off_Tip");
        }
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<IslandSessionItem> _sessions = new();
    [ObservableProperty] private int _runningCount;

    /// <summary>是否有多个会话同时在跑 —— 头部小章鱼据此切到"两只章鱼讨论"动画。</summary>
    [ObservableProperty] private bool _hasMultipleAgents;
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private bool _hasAttention;
    [ObservableProperty] private bool _hasAnySessions;

    /// <summary>
    /// 任一会话进入 WaitingForApproval 时翻成 true —— 触发岛宽度动画到 2x、切到权限专属视图，
    /// 临时把"几条 Claude 在跑"的列表让位给清晰的三选项 UI。用户做出选择后 phase 解锁，
    /// 这里翻回 false，岛恢复原宽 + 列表视图。
    /// </summary>
    [ObservableProperty] private bool _isPermissionMode;
    [ObservableProperty] private IslandSessionItem? _permissionSession;

    /// <summary>
    /// 用户拖岛贴到屏幕顶后吸附进的 Notch 形态 —— 仿 MacBook 刘海，黑底胶囊形横条，仅显示
    /// 一条最活跃 session 的图标 + 标题 + 数字徽章。再次拖离顶部一定距离则翻回 false 恢复默认。
    /// </summary>
    [ObservableProperty] private bool _isNotchMode;

    /// <summary>第一条活跃 session（给 Notch 用），按"权限优先 → Running → Idle"挑。</summary>
    public IslandSessionItem? PrimarySession =>
        Sessions.FirstOrDefault(s => s.Phase == SessionPhase.WaitingForApproval) ??
        Sessions.FirstOrDefault(s => s.Phase == SessionPhase.Running) ??
        Sessions.FirstOrDefault();

    // 灯颜色：红=需关注，蓝=运行中，绿=就绪/刚完成
    [ObservableProperty] private string _statusDotColor = "#4CAF50";

    /// <summary>聚合会话状态（给像素状态精灵绑）：需关注→WaitingForApproval，
    /// 任一 Running→Running，否则 Idle。跟 StatusDotColor 同一处计算。</summary>
    [ObservableProperty] private SessionPhase _aggregatePhase = SessionPhase.Idle;

    public DynamicIslandViewModel(SessionManager sessionManager, PopupWindowService popupService, SystemStatsService systemStats, MediaControlService media, PlanUsageService planUsage, WorkspaceSettings settings, ScreenshotService screenshot, SkillInstallService skillInstall, WebSyncService webSync)
    {
        _sessionManager = sessionManager;
        _popupService = popupService;
        _systemStats = systemStats;
        _planUsage = planUsage;
        _media = media;
        _settings = settings;
        _screenshot = screenshot;
        _skillInstall = skillInstall;
        _webSync = webSync;

        // 网页同步默认关，ToolTip 先放使用说明（开启后换成访问地址）
        WebSyncTip = Loc.Get("Web_Off_Tip");

        // 启动时把提示音开关对齐持久化值，并同步到 SoundService —— 否则 SoundService.Enabled
        // 默认 true，用户上次关了重启后仍会响一次才被纠正。
        // 直接写 backing field（非 SoundEnabled 属性）避免触发 OnSoundEnabledChanged 在
        // 构造期回写一遍 settings（值没变，没必要落盘）。
        _soundEnabled = settings.SoundEnabled;
        SoundService.Enabled = settings.SoundEnabled;

        // 余额行显示模式对齐持久化值（写 backing field 不触发落盘）。下次启动恢复关闭时的状态。
        _showUsageChart = settings.ShowUsageChart;
        // 毛玻璃开关镜像（驱动外层 Border 的 Margin DataTrigger）
        _glassOn = settings.GlassEnabled;

        _sessionManager.SessionsChanged += OnSessionsChanged;
        _sessionManager.TaskCompleted += OnTaskCompleted;
        _systemStats.StatsUpdated += OnSystemStatsUpdated;
        _planUsage.UsageUpdated += OnPlanUsageUpdated;
        // 启动时把滑块对齐到当前系统音量；之后跟着 SystemStats 的 1s tick 顺带同步，
        // 这样在别处（系统音量条/媒体键）改了音量，岛上滑块也会跟上。
        SyncVolumeFromSystem();

        // 绿色状态计时器：3秒后恢复
        _greenStatusTimer = new System.Timers.Timer(3000);
        _greenStatusTimer.Elapsed += (_, _) =>
        {
            _justCompletedTask = false;
            // 关闭程序时 Application.Current 可能已为 null（定时器最后一跳）；用 ?. 防退出崩溃。
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(UpdateStatusColor);
        };
        _greenStatusTimer.AutoReset = false;

        _settings.Changed += OnSettingsChangedForModels;
        // 语言切换：5h 余额行等动态文案立即按新语言重渲染（静态 XAML 文本走 indexer 绑定自动刷新）。
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        RefreshSessions();
        if (_showUsageChart) RefreshUsageChart(); // 启动即为柱状图模式时先把柱子算好
    }

    private void OnLanguageChanged()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_lastPlan is { } s) OnPlanUsageUpdated(this, s);
            // 地球按钮 ToolTip 是代码侧动态文案，语言切换时按新语言重建
            //（开启失败的 "⚠ " 一次性错误提示被覆盖成默认说明可接受）。
            WebSyncTip = WebSyncOn ? Loc.Format("Web_On", _webSync.GetUrl()) : Loc.Get("Web_Off_Tip");
        });
    }

    // ── 全局模型切换（音量条下方那一栏）：选中即切换，写 ~/.claude/settings.json，对新 CLI 会话生效 ──
    public System.Collections.Generic.IReadOnlyList<ModelProfile> ModelChoices
        => ModelPresets.BuiltInClaude.Concat(_settings.ModelProfiles).ToList();

    [ObservableProperty] private string? _globalModelStatus;
    [ObservableProperty] private bool _modelMenuOpen;
    private bool _busyModel;

    // 纯按钮 + 弹出列表：点列表里的某个模型即切换并收起菜单（不显示当前模型）。
    [RelayCommand]
    private async Task SwitchToModel(ModelProfile? profile)
    {
        if (profile == null) return;
        ModelMenuOpen = false;
        await SwitchGlobalModelAsync(profile);
    }

    /// <summary>毛玻璃是否开启（镜像 WorkspaceSettings.GlassEnabled）。外层 Border 的 Margin
    /// DataTrigger 绑它 —— 玻璃开时收掉外边距，让 accent 模糊背板不在岛外露出。</summary>
    [ObservableProperty] private bool _glassOn;

    private void OnSettingsChangedForModels(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ModelChoices));
            GlassOn = _settings.GlassEnabled;
        });

    private async Task SwitchGlobalModelAsync(ModelProfile profile)
    {
        if (_busyModel) return;
        _busyModel = true;
        try
        {
            GlobalModelStatus = Loc.Get("Model_Switching");
            var result = await _sessionManager.SwitchGlobalModelAsync(profile);
            if (result.Ok) _settings.SetActiveModelProfile(profile.Id);
            GlobalModelStatus = MapModelReason(result.Reason, result.Ok);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SwitchGlobalModelAsync failed: {ex.Message}");
            GlobalModelStatus = Loc.Get("Model_SwitchError");
        }
        finally { _busyModel = false; }
    }

    private static string MapModelReason(string? reason, bool ok) => reason switch
    {
        "switched-official" => Loc.Get("Model_SwitchedOfficial"),
        "needs-restart" => Loc.Get("Model_NeedsRestart"),
        "no-key" => Loc.Get("Model_NoKey"),
        "write-failed" => Loc.Get("Model_WriteFailed"),
        _ => Loc.Get(ok ? "Model_Switched" : "Model_SwitchFailed")
    };

    private void OnSystemStatsUpdated(object? sender, SystemStatsSnapshot s)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuText = $"CPU {s.CpuPercent:0}%";
            MemText = $"RAM {s.MemPercent:0}%";
            GpuText = s.GpuPercent < 0 ? "GPU --" : $"GPU {s.GpuPercent:0}%";
            NetText = $"↓{FormatRate(s.NetDownBytesPerSec)} ↑{FormatRate(s.NetUpBytesPerSec)}";
            SyncVolumeFromSystem(); // 顺带把滑块对齐系统音量（别处改了也跟上）
        });
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 点击 CPU / RAM 百分比触发：释放内存 —— GC 自己 + 把所有可访问进程的工作集换出物理内存
    /// （EmptyWorkingSet，类似 RAM 清理工具）。无权限的系统/受保护进程跳过。RAM% 随下个 1s tick 下降。
    /// </summary>
    [RelayCommand]
    private void ReleaseMemory()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                long Sum() { long t = 0; foreach (var pr in System.Diagnostics.Process.GetProcesses()) { try { t += pr.WorkingSet64; } catch { } finally { pr.Dispose(); } } return t; }
                var before = Sum();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                int trimmed = 0;
                foreach (var proc in System.Diagnostics.Process.GetProcesses())
                {
                    try { if (EmptyWorkingSet(proc.Handle)) trimmed++; }
                    catch { /* 无权限 / 已退出，跳过 */ }
                    finally { proc.Dispose(); }
                }
                var after = Sum();
                var freedMb = (before - after) / (1024.0 * 1024.0);
                try { System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openisland-mem.log"),
                    $"[{DateTime.Now:HH:mm:ss}] ReleaseMemory: trimmed {trimmed} procs, freed ~{freedMb:N0} MB\n"); } catch { }
            }
            catch { }
        });
    }

    /// <summary>字节/秒 → 紧凑字符串：&lt;1KB 显示 B/s，&lt;1MB 显示 KB/s，否则 MB/s。</summary>
    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0}B";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0}K";
        return $"{bytesPerSec / (1024.0 * 1024.0):0.0}M";
    }

    /// <summary>刷新按钮转圈中（也用于防连点：进行中禁用按钮）。</summary>
    [ObservableProperty] private bool _isRefreshingUsage;

    /// <summary>5h 余额行的刷新按钮：立即重新探一次真实用量，刷新余额与重置时间。</summary>
    [RelayCommand]
    private async Task RefreshUsage()
    {
        if (IsRefreshingUsage) return;
        IsRefreshingUsage = true;
        try { await _planUsage.RefreshNowAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RefreshUsage failed: {ex.Message}"); }
        finally { IsRefreshingUsage = false; }
    }

    /// <summary>
    /// Plan usage 快照 → UI 文本/进度条。marshal 到 UI 线程方式与 OnSystemStatsUpdated 一致。
    /// API 模式只显示 token 数；Plan 模式显示百分比 + 进度条 + 重置倒计时，
    /// 颜色阈值：&lt;70% 蓝 / 70-89% 橙 / ≥90% 红。
    /// </summary>
    private PlanUsageSnapshot? _lastPlan; // 缓存最近一帧，语言切换时按当前语言重渲染

    private void OnPlanUsageUpdated(object? sender, PlanUsageSnapshot s)
    {
        _lastPlan = s;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (s.IsApi)
            {
                PlanIsApi = true;
                PlanLabel = "API";
                PlanValueText = FormatTokens(s.UsedTokens) + " tokens";
            }
            else
            {
                PlanIsApi = false;
                PlanLabel = "5h";
                if (s.Indeterminate)
                {
                    // 还没拿到真实 5h 数据：显示"余 --"，中性灰、空条、无重置。绝不伪造百分比。
                    PlanPercentText = Loc.Get("Balance_Unknown");
                    PlanBarFraction = 0;
                    PlanResetText = "";
                    PlanBarColor = "#5A5A5E";
                }
                else
                {
                    // 显示「余额」（剩余），不是已用：余额 = 100% - 已用%。
                    int remaining = Math.Clamp(100 - s.Percent, 0, 100);
                    PlanPercentText = Loc.Format("Balance_Format", remaining);
                    // 余额条：满=绿（额度多），随消耗下降，少时转橙/红。
                    PlanBarFraction = Math.Clamp(1.0 - s.Fraction, 0, 1);
                    PlanResetText = s.ResetIn is { } r && r > TimeSpan.Zero
                        ? Loc.Format("Reset_Format", (int)r.TotalHours, r.Minutes)
                        : "";
                    PlanBarColor = remaining <= 10 ? "#E74C3C"
                                 : remaining <= 30 ? "#FF9F0A"
                                 : "#30D158";
                }
            }
        });
    }

    /// <summary>token 数人类化：≥1e6 → "1.24M"，≥1e3 → "320K"，否则原值 "950"。</summary>
    private static string FormatTokens(ulong t)
    {
        if (t >= 1_000_000UL) return $"{t / 1_000_000.0:0.##}M";
        if (t >= 1_000UL) return $"{t / 1_000.0:0.#}K";
        return t.ToString();
    }

    private void OnTaskCompleted(object? sender, AgentSession session)
    {
        // 关闭程序时 Application.Current 可能已为 null（completion 定时器在 teardown 后回调）；?. 防崩溃。
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _justCompletedTask = true;
            // 先刷新会话与 AttentionCount，再由 RefreshSessions 末尾的 UpdateStatusColor 基于
            // 最新状态算颜色与提示音边沿 —— 避免 TaskCompleted 早于 SessionsChanged 到达时读到旧聚合。
            RefreshSessions();
            _greenStatusTimer.Stop();
            _greenStatusTimer.Start();
        });
    }

    private void UpdateStatusColor()
    {
        // 颜色按 *会话 Phase* 判断，不再用"claude.exe 进程数"。claude.exe 在两轮对话之间
        // 一直存活，按进程数判断会让灯永远显示蓝；而用户期望"我说话→Claude 回复→停手等我"
        // 这段空闲期为绿色。任意 session 在 Running → 蓝（Claude 在思考）；都 Idle → 绿。
        // Color reflects session Phase, not live-process count: claude.exe stays resident
        // between turns, so process-count would keep the dot stuck on blue. Any session in
        // Running → blue (Claude is thinking); none Running → green (waiting on user).
        if (AttentionCount > 0)
        {
            StatusDotColor = "#E74C3C"; // 红：需关注
            AggregatePhase = SessionPhase.WaitingForApproval;
            // 注意：这里不能 return —— 还要走下面统一的提示音沿边判断（#1/#2），
            // 否则"需关注"分支永远触发不了 PlayAttention。
        }
        else
        {
            // 只看用户能在岛上看到的卡片（Sessions），不再扫 GetAllSessions 全部 75+ 条。
            // 之前用 GetAllSessions 有个隐蔽 bug：watcher 启动时全量扫描会把所有 transcripts
            // 灌进 SessionState，其中任意一条若被卡在 Running phase（比如 mtime 抖动判断、
            // emit 顺序问题等），灯就被永久钉死蓝色。灯只反映"用户实际正在看的会话状态"
            // 才是符合直觉的语义。
            var anyThinking = false;
            foreach (var s in Sessions)
            {
                if (s.Phase == SessionPhase.Running)
                {
                    anyThinking = true;
                    break;
                }
            }
            StatusDotColor = anyThinking ? "#2196F3" : "#4CAF50";
            AggregatePhase = anyThinking ? SessionPhase.Running : SessionPhase.Idle;
        }

        // ── 提示音沿边触发（#1 / #2）—— AggregatePhase 已是最终值 ──
        // SoundService 内部已做总开关 + 1.5s 去抖，这里只负责"判断是不是发生了
        // 该响的状态转换"。聚合级判断（不是单会话级）：覆盖桌面端 session，因为
        // watcher 在桌面端也驱动 Idle。
        // Edge-triggered chimes; SoundService handles mute + debounce internally.
        var newPhase = AggregatePhase;

        // #1 任务完成：Running → Idle/Completed（上一轮在思考，这一轮停手等用户）
        if (_prevAggregatePhase == SessionPhase.Running
            && newPhase is SessionPhase.Idle or SessionPhase.Completed)
        {
            SoundService.PlayTaskComplete();
        }

        // #2 需关注：进入橙色权限(WaitingForApproval) / 红色待回答(WaitingForAnswer)。
        // SessionPhase 枚举只有 Running / WaitingForApproval / WaitingForAnswer /
        // Completed / Idle —— 没有单独的 Attention/红色成员，"需关注"就是这两个。
        // 仅在"上一次还不是同一个关注态"时响 —— 否则每次刷新都会重复叮。
        if (newPhase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer
            && _prevAggregatePhase != newPhase)
        {
            SoundService.PlayAttention();
        }

        _prevAggregatePhase = newPhase;
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void OpenControlCenter()
    {
        _popupService.OpenControlCenter();
        IsExpanded = false;
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RefreshSessions();
            if (ShowUsageChart) RefreshUsageChart(); // 柱状图模式下随会话变化刷新用量
        });
    }

    private void RefreshSessions()
    {
        var runningProcesses = _sessionManager.GetRunningProcesses();

        // Windows 下 claude.exe 拿不到进程的 cwd（PEB 读需要 PROCESS_VM_READ，且
        // claude.exe 没 MainWindowTitle、没命令行参数），所以以前每个运行进程都
        // 退到 RunningSessionInfo 的项目名兜底，再退到字面量 "Claude"。
        // 改成 cc-switch 的做法：按转录文件 mtime 排序的 scan 会话直接当"当前活跃
        // 会话"列表用，把 ProcessMonitor 的运行进程数仅当作"应该显示几条"的依据。
        var sessionsByRecency = _sessionManager.GetAllSessions()
            .Where(s => s.Tool == AgentTool.ClaudeCode && !string.IsNullOrEmpty(s.ClaudeMetadata?.TranscriptPath))
            .Select(s => new { Session = s, Mtime = TryGetMtime(s.ClaudeMetadata!.TranscriptPath!) })
            .Where(x => x.Mtime.HasValue)
            .OrderByDescending(x => x.Mtime!.Value)
            .Select(x => x.Session)
            .ToList();

        var assignedIds = new HashSet<string>();
        var desired = new List<(string Key, AgentSession? Session, RunningSessionInfo? Info)>();

        foreach (var r in runningProcesses)
        {
            AgentSession? session = null;

            // 1. cwd 命中（罕见，但 hook 写入的 session 会有正确 cwd）
            if (!string.IsNullOrEmpty(r.WorkingDirectory))
            {
                session = sessionsByRecency.FirstOrDefault(s =>
                    !assignedIds.Contains(s.Id) &&
                    s.JumpTarget?.WorkingDirectory != null &&
                    string.Equals(
                        s.JumpTarget.WorkingDirectory.TrimEnd('\\', '/'),
                        r.WorkingDirectory.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase));
            }

            // 2. 没 cwd 时退回到"最近被改写过的 .jsonl 对应的 scan 会话"
            session ??= sessionsByRecency.FirstOrDefault(s => !assignedIds.Contains(s.Id));

            if (session != null)
            {
                assignedIds.Add(session.Id);
                if (IsHiddenByDismiss(session.Id, session.Phase)) continue;
                desired.Add((session.Id, session, null));
            }
            else
            {
                if (IsHiddenByDismiss(r.SessionId, null)) continue;
                desired.Add((r.SessionId, null, r));
            }
        }

        var capped0 = desired.Count > 20 ? desired.GetRange(0, 20) : desired;
        // 去重 Key —— RunningSessionInfo 的合成 SessionId 可能撞车；重复 Key 会让下面的协调越界崩。
        var seenKeys = new HashSet<string>();
        var capped = new List<(string Key, AgentSession? Session, RunningSessionInfo? Info)>();
        foreach (var d in capped0)
            if (seenKeys.Add(d.Key)) capped.Add(d);

        // 增量协调 Sessions：按 Id 复用同一张卡片实例（就地 Update），只增删/重排差异。
        // 不整列 Clear()+重建 —— 进场/变色/脉冲动画只在卡片真正新增或状态真变化时触发，
        // 而不是每次 transcript 刷新都狂闪；正在输入的快捷回复 / 展开态也天然保留（同一实例）。
        var desiredKeys = new HashSet<string>(capped.Select(c => c.Key));
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!desiredKeys.Contains(Sessions[i].Id)) Sessions.RemoveAt(i);

        for (int i = 0; i < capped.Count; i++)
        {
            var (key, session, info) = capped[i];
            var found = -1;
            for (int j = i; j < Sessions.Count; j++)
                if (Sessions[j].Id == key) { found = j; break; }

            if (found < 0)
            {
                var item = session != null
                    ? new IslandSessionItem(session, _sessionManager, DismissSession, _settings, TogglePinSession)
                    : new IslandSessionItem(info!, _sessionManager, DismissSession, _settings, TogglePinSession);
                item.IsPinned = _pinned.Contains(key);
                Sessions.Insert(i, item); // 前 i 项已就位，i <= Count，安全
            }
            else
            {
                if (found != i) Sessions.Move(found, i);
                if (session != null) Sessions[i].Update(session); else Sessions[i].Update(info!);
                Sessions[i].IsPinned = _pinned.Contains(key); // 图钉状态以 VM 的 _pinned 为准
            }
        }

        // 去掉尾部多余项（残留重复等）
        while (Sessions.Count > capped.Count) Sessions.RemoveAt(Sessions.Count - 1);

        RunningCount = runningProcesses.Count;
        HasMultipleAgents = runningProcesses.Count >= 2; // 多会话 → 双章鱼讨论动画
        AttentionCount = _sessionManager.GetAttentionCount();
        HasAttention = AttentionCount > 0;
        HasAnySessions = Sessions.Count > 0;

        // 剪枝 _dismissed：移除已彻底消失的会话（既不在已知会话、也不在运行进程里）。
        // 否则其条目会永久驻留（内存泄漏），且若该 id 日后以 Idle 重现，会被状态机永久卡隐藏。
        if (_dismissed.Count > 0)
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in _sessionManager.GetAllSessions())
                if (!string.IsNullOrEmpty(s.Id)) alive.Add(s.Id);
            foreach (var r in runningProcesses)
                if (!string.IsNullOrEmpty(r.SessionId)) alive.Add(r.SessionId);
            foreach (var k in _dismissed.Keys.Where(k => !alive.Contains(k)).ToList())
                _dismissed.Remove(k);
        }

        // 找第一条 WaitingForApproval —— 用作权限专属视图的数据源
        IslandSessionItem? perm = null;
        foreach (var s in Sessions)
        {
            if (s.Phase == SessionPhase.WaitingForApproval)
            {
                perm = s;
                break;
            }
        }
        PermissionSession = perm;
        IsPermissionMode = perm != null;
        OnPropertyChanged(nameof(PrimarySession));

        UpdateStatusColor();

        if (HasAttention && !IsExpanded)
            IsExpanded = true;
    }

    private static DateTime? TryGetMtime(string path)
    {
        try { return System.IO.File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    /// <summary>用户点小叉号：记录收起，立刻刷新列表把卡片移出。</summary>
    private void DismissSession(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _dismissed[id] = false; // sawQuiet 初值 false，下一次 RefreshSessions 按 phase 推进
        RefreshSessions();
    }

    /// <summary>卡片图钉按钮：切换该会话的固定状态（固定后"清理任务"不会清掉它）。</summary>
    private void TogglePinSession(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_pinned.Remove(id)) _pinned.Add(id);
        RefreshSessions(); // 让卡片 IsPinned 跟着更新（图钉变色）
    }

    /// <summary>
    /// 点 "Open Island" 头部触发的"一键清空当前列表"——把此刻列表里的每条 session
    /// 都按"单卡叉号收起"的同一套语义记进 _dismissed（value=false，沿用 sawQuiet 状态机），
    /// 然后刷新一次。效果：列表立刻清空，之后谁再活动谁再回来：
    ///   · 阻塞性 prompt（WaitingForApproval/WaitingForAnswer）—— IsHiddenByDismiss 里
    ///     "需关注 phase 压过收起"那条规则会在本次 RefreshSessions 立即把它移出 _dismissed
    ///     并保留可见，未答的权限不会被清掉（用户仍要回应）。
    ///   · 其余收起的 session —— 跑完转 Idle/Completed 继续藏；安静后再次 Running（新一轮）
    ///     或进入需关注，由既有 IsHiddenByDismiss 自动放回。
    /// 复用既有 dismiss/reappear 状态机，不另写过滤逻辑；运行数徽章仍由 RefreshSessions
    /// 末尾按真实运行进程数算，与"视觉清空"解耦。
    ///
    /// Clears the visible session list on "Open Island" header click by marking every
    /// currently-listed session as dismissed using the *same* _dismissed semantics as the
    /// per-card ✕ button, then refreshing once. Blocking prompts are preserved because
    /// IsHiddenByDismiss force-unhides WaitingForApproval/WaitingForAnswer; idle/completed
    /// stay hidden and reappear organically when they go Running again or need attention.
    /// The running-count badge stays accurate (recomputed from live processes in RefreshSessions).
    /// </summary>
    private void ClearAllSessions()
    {
        if (Sessions.Count == 0) return;

        // 快照当前 id（直接遍历 Sessions 时 RefreshSessions 会改集合，先拷出来）
        foreach (var s in Sessions.ToList())
        {
            if (string.IsNullOrEmpty(s.Id)) continue;
            // 不收起当前正活动的会话（Running / 需关注）—— 折叠/清空灵动岛不该把"正在跑的任务"
            // 弄没了：一条持续 Running 的会话被收起后，要等它本轮结束再开新一轮才会回来，期间岛上
            // 既无卡片、聚合状态又显示空闲，看着就像任务消失了。这里只清理已结束/空闲的卡片，
            // 活动中的会话保持可见（聚合状态也就仍正确显示"有任务在跑"）。
            if (s.Phase is SessionPhase.Running
                or SessionPhase.WaitingForApproval
                or SessionPhase.WaitingForAnswer)
                continue;
            // 图钉固定的会话：用户显式保留，不清理。
            if (_pinned.Contains(s.Id))
                continue;
            // 已在 _dismissed 里的不要覆盖其 sawQuiet（保持它在状态机里的既有进度）
            if (!_dismissed.ContainsKey(s.Id))
                _dismissed[s.Id] = false;
        }

        // 刷新：IsHiddenByDismiss 会把刚记下的统统隐藏，但阻塞性 prompt 那条会被
        // 立即移出 _dismissed 并保留显示——未答权限不会被误清。
        RefreshSessions();
    }

    /// <summary>
    /// 头部点击命令（短点而非拖拽时由 code-behind 调用）：只切换展开/收起态，不再清理任务。
    /// 清理任务改由模型栏的"清理任务"按钮（ClearTasksCommand）显式触发。
    /// 见 DynamicIslandWindow.Header_MouseUp。
    /// Header tap command: toggle expand/collapse only — no longer clears tasks. Called from
    /// code-behind only on a genuine click (not a drag); see Header_MouseUp.
    /// </summary>
    public void OnHeaderTapped()
    {
        // 收起/展开灵动岛只切换展开态，不再清理任务 —— 清理改由模型栏的"清理任务"按钮显式触发，
        // 避免折叠/展开就把刚结束（或正在跑）的任务卡片弄没了。
        IsExpanded = !IsExpanded;
    }

    /// <summary>模型栏"清理任务"按钮：显式清理任务卡片（沿用 ClearAllSessions：清已结束/空闲，保留活动中的）。</summary>
    [RelayCommand]
    private void ClearTasks() => ClearAllSessions();

    // ── 安装 Skill：模型栏按钮弹出小面板，粘贴 claude plugin 命令 / owner/repo 后台安装 ──
    [ObservableProperty] private bool _skillMenuOpen;
    [ObservableProperty] private string _skillInstallInput = "";
    [ObservableProperty] private string _skillInstallStatus = "";
    private bool _isSkillInstalling;

    /// <summary>
    /// 解析输入 → 后台逐条跑 claude plugin 命令。安装过程中再点直接忽略（防并发重入）；
    /// 进度回调来自后台线程，需经 Dispatcher 回 UI 线程；await 之后由 async 上下文
    /// 自动回到 UI 线程，结果状态直接赋值即可。
    /// </summary>
    [RelayCommand]
    private async Task InstallSkill()
    {
        if (_isSkillInstalling) return;

        var cmds = SkillInstallService.ParseCommands(SkillInstallInput);
        if (cmds.Count == 0)
        {
            SkillInstallStatus = Loc.Get("Skill_Invalid");
            return;
        }

        _isSkillInstalling = true;
        SkillInstallStatus = Loc.Get("Skill_Installing");
        try
        {
            var (ok, output) = await _skillInstall.RunAsync(cmds,
                p => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => SkillInstallStatus = p));
            SkillInstallStatus = ok
                ? Loc.Get("Skill_Done")
                : Loc.Format("Skill_Failed", output.Length <= 300 ? output : output[^300..]);
        }
        finally
        {
            _isSkillInstalling = false;
        }
    }

    /// <summary>
    /// 返回 true = 这条 session 当前应保持隐藏（被收起且还没"再次活动"）。
    /// 会按 phase 推进 _dismissed 的状态机（见 _dismissed 字段注释）。
    /// </summary>
    private bool IsHiddenByDismiss(string id, SessionPhase? phase)
    {
        if (string.IsNullOrEmpty(id) || !_dismissed.TryGetValue(id, out var sawQuiet))
            return false;

        // 需关注 phase 压过收起 —— 阻塞性 prompt 必须冒出来
        if (phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer)
        {
            _dismissed.Remove(id);
            return false;
        }

        // 走到安静态：本轮活动收尾，标记 sawQuiet，仍隐藏
        if (phase is SessionPhase.Idle or SessionPhase.Completed)
        {
            _dismissed[id] = true;
            return true;
        }

        // 安静之后又 Running = 新一轮活动 → 重新显示
        if (phase is SessionPhase.Running && sawQuiet)
        {
            _dismissed.Remove(id);
            return false;
        }

        // 其它（收起时还在 Running，本轮没结束；或没有 phase 信息的兜底项）→ 继续隐藏
        return true;
    }

    /// <summary>
    /// 取消所有事件订阅并停掉定时器。VM 是 DI singleton —— 应用退出时
    /// App.OnExit → ServiceProvider.Dispose() 会自动调用本方法，无需手动接线。
    /// </summary>
    public void Dispose()
    {
        _sessionManager.SessionsChanged -= OnSessionsChanged;
        _sessionManager.TaskCompleted -= OnTaskCompleted;
        _systemStats.StatsUpdated -= OnSystemStatsUpdated;
        _planUsage.UsageUpdated -= OnPlanUsageUpdated;
        _settings.Changed -= OnSettingsChangedForModels;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _greenStatusTimer.Stop();
        _greenStatusTimer.Dispose();
    }
}

/// <summary>七天柱状图的一根柱子：高度(px) + 颜色(hex，用量越多绿色越深) + 悬浮提示。</summary>
public sealed class UsageBar
{
    public double BarHeight { get; init; }
    public string Color { get; init; } = "#4CAF50";
    public string Tooltip { get; init; } = "";
}

public partial class IslandSessionItem : ObservableObject
{
    private AgentSession? _session;
    private RunningSessionInfo? _runningInfo;
    private readonly SessionManager? _sessionManager;
    private readonly Action<string>? _onDismiss;
    private Action<string>? _onTogglePin;

    /// <summary>是否被图钉固定（固定后"清理任务"不会清掉它）。由 VM 的 _pinned 集合驱动。</summary>
    [ObservableProperty] private bool _isPinned;
    private readonly WorkspaceSettings? _settings;

    public string Id => _session?.Id ?? _runningInfo?.SessionId ?? "";
    public string Title => _session?.Title ?? GetRunningInfoTitle() ?? "Claude";
    /// <summary>
    /// 暴露给 DynamicIslandViewModel.UpdateStatusColor —— 让灯只看用户能在卡片上看见的 phase，
    /// 不看后台几十条历史 session 任意一条卡 Running 就让灯永远蓝。
    /// 若仅有 RunningSessionInfo（transcript 还没扫到），按 Idle 算（保守，灯倾向于绿）。
    /// </summary>
    public SessionPhase Phase => _session?.Phase ?? SessionPhase.Idle;

    /// <summary>
    /// 单卡片左侧状态点的颜色 —— 跟顶端 DynamicIslandViewModel.UpdateStatusColor 同套规则：
    /// 蓝=Running（Claude 思考/工作中）、绿=Idle（一轮完成等输入）、橙=需关注（权限/问题）、灰=Completed。
    /// 让用户一眼看出每条会话当前状态，而不只是顶端灯反映"任意一条在工作"的聚合状态。
    /// </summary>
    public string StatusColor => Phase switch
    {
        SessionPhase.Running => "#2196F3",
        SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer => "#FF9800",
        SessionPhase.Completed => "#9E9E9E",
        SessionPhase.Idle => "#4CAF50",
        _ => "#757575"
    };

    // 状态点颜色缓存（避免每次读 StatusColorValue 都解析字符串，也消除 ConvertFromString 的 NRE/异常风险）。
    private static readonly System.Windows.Media.Color _cRunning   = System.Windows.Media.Color.FromRgb(0x21, 0x96, 0xF3);
    private static readonly System.Windows.Media.Color _cAttention = System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly System.Windows.Media.Color _cCompleted = System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E);
    private static readonly System.Windows.Media.Color _cIdle      = System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly System.Windows.Media.Color _cDefault   = System.Windows.Media.Color.FromRgb(0x75, 0x75, 0x75);

    /// <summary>状态点颜色的 Color 值（给 Anim.FillColor 平滑变色用）。与 StatusColor 同套规则。</summary>
    public System.Windows.Media.Color StatusColorValue => Phase switch
    {
        SessionPhase.Running => _cRunning,
        SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer => _cAttention,
        SessionPhase.Completed => _cCompleted,
        SessionPhase.Idle => _cIdle,
        _ => _cDefault
    };

    /// <summary>是否 Running —— 给"思考中"3 点指示器显隐用。</summary>
    public bool IsRunningPhase => Phase == SessionPhase.Running;

    /// <summary>是否需关注（权限/问题）—— 给注意力脉冲用。</summary>
    public bool IsAttentionPhase => Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer;

    private string? GetRunningInfoTitle()
    {
        if (_runningInfo?.WorkingDirectory == null) return _runningInfo?.ProjectName;

        // 使用工作目录的最后两级作为标题，例如 "claudeai/web"
        var dir = _runningInfo.WorkingDirectory.TrimEnd('\\', '/');
        var parts = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
        {
            return $"{parts[parts.Length - 2]}/{parts[parts.Length - 1]}";
        }
        else if (parts.Length == 1)
        {
            return parts[0];
        }

        return _runningInfo.ProjectName ?? "Claude";
    }
    public bool NeedsAttention => _session?.NeedsAttention ?? false;
    public bool ShowApproveButton => _session?.Phase == SessionPhase.WaitingForApproval;

    /// <summary>
    /// 桌面端会话（Claude Desktop）—— 权限按钮要严格镜像 Electron 弹窗的真实文案
    /// （Allow once / Deny），不能套终端那套 1/2/3 模板。
    /// </summary>
    private bool IsDesktopSession
        => string.Equals(_session?.ClaudeMetadata?.Entrypoint, "claude-desktop",
                          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 主按钮文案：桌面端 = Claude Desktop 真实的 "Allow once"；CLI = 终端的 "1. Yes"。
    /// </summary>
    public string YesButtonLabel => IsDesktopSession ? "Allow once" : "1. Yes";

    /// <summary>
    /// 拒绝按钮文案：桌面端 = "Deny"；CLI = 终端那条长文案。
    /// </summary>
    public string NoButtonLabel
        => IsDesktopSession ? "Deny" : "3. No, and tell Claude what to do differently";

    /// <summary>
    /// 第二按钮文案。SuggestedAlwaysAllow 推到时用 "Yes, don't ask again for {scope}"，
    /// 推不到（hook payload 缺 tool_name 等极端情况）兜底成纯 "2. Yes, don't ask again"，
    /// 不让按钮因为没规则就消失 —— 用户体验上 1/2/3 三键应该恒定可见。
    /// </summary>
    public string AlwaysButtonLabel
        => _session?.PermissionRequest?.SuggestedAlwaysAllow?.ToButtonLabel()
           ?? "2. Yes, don't ask again";

    /// <summary>
    /// 第二按钮的显隐：CLI 跟 phase 走；桌面端隐藏 —— Claude Desktop 基础权限弹窗
    /// 就 Allow once / Deny 两个，没有"总是允许"，岛上多一个会对不上（用户反馈的核心）。
    /// </summary>
    public bool ShowAlwaysButton
        => _session?.Phase == SessionPhase.WaitingForApproval && !IsDesktopSession;

    /// <summary>
    /// 权限/问题卡底部提示。AskUserQuestion 用"点击选项 —— 同步到 Claude / 终端"；
    /// 普通权限按 entrypoint 分（桌面端不提"在终端按 1/2/3"）。
    /// </summary>
    public string PermissionHintText
        => IsQuestion
            ? "点击选项 —— 同步到 Claude / 终端"
            : IsDesktopSession
                ? "点击 Allow once / Deny —— 直接同步到 Claude Desktop"
                : "点击上方任一项，或在 Claude 终端按 1 / 2 / 3";

    /// <summary>该会话当前排队的待批准请求总数（并行 subagent 共享 session_id 时会 > 1）。</summary>
    public int PendingPermissionCount => _session?.PendingPermissions.Count ?? 0;

    /// <summary>队列里还有不止一个待批准（并发场景）时为 true，用于显示"还有 N 个"徽标。</summary>
    public bool HasMultiplePending => PendingPermissionCount > 1;

    /// <summary>并发待批准徽标文案，如 "+2 个待批准"（不含当前正显示的队头那个）。</summary>
    public string PendingPermissionBadge => HasMultiplePending ? $"+{PendingPermissionCount - 1} 个待批准" : "";

    /// <summary>
    /// 权限请求时显示的"工具名 + 主要参数"标题行，例如 "WebFetch · https://vibeisland.app/"。
    /// B 模式下岛只镜像 Claude 终端 prompt，不再交互，标题就要把请求内容讲清楚。
    /// </summary>
    public string PermissionHeadline
    {
        get
        {
            var req = _session?.PermissionRequest;
            if (req == null) return "";
            var detail = ExtractPermissionDetail(req);
            return string.IsNullOrEmpty(detail) ? req.ToolName : $"{req.ToolName} · {detail}";
        }
    }

    /// <summary>
    /// 权限请求的完整入参展开（多行 JSON-ish），供"详细一点"展示。
    /// AskUserQuestion 单独按"Q + 编号选项 + 描述"格式化，避免直接喷 JSON 转义码。
    /// </summary>
    public string PermissionFullDetail
    {
        get
        {
            var req = _session?.PermissionRequest;
            if (req?.ToolInput == null || req.ToolInput.Count == 0) return req?.Description ?? "";

            // AskUserQuestion 工具：读 questions[0].question + options[].label/description
            if (string.Equals(req.ToolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase))
            {
                var formatted = FormatAskUserQuestion(req.ToolInput);
                if (!string.IsNullOrEmpty(formatted)) return formatted;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var kv in req.ToolInput)
            {
                var v = kv.Value?.ToString() ?? "";
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append(": ").Append(v);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// AskUserQuestion 的 tool_input 解析结果：第一条 question 的标题文本 +
    /// 该 question 的结构化选项列表。XAML 的问题选项按钮和文本详情块都从这里取，
    /// 解析逻辑只此一处（不再在 FormatAskUserQuestion 里重复走一遍 JSON）。
    /// </summary>
    private readonly record struct ParsedAskQuestion(
        string Title,
        System.Collections.Generic.IReadOnlyList<QuestionOption> Options);

    /// <summary>
    /// AskUserQuestion 的 tool_input 通常长这样（Claude Code 实际格式）：
    /// {"questions":[{"question":"...","header":"...","options":[{"label":"...","description":"..."},...],"multiSelect":false}]}
    /// 走 JsonElement 解析比 Dictionary&lt;string,object&gt; 强壮（嵌套数组/对象）。
    /// 解析 questions[0]：拿 question 文本 + options[] → QuestionOption{Number,Label,Description}。
    /// 单一解析入口，FormatAskUserQuestion（文本块）与 VM 结构化属性共用，避免重复。
    /// </summary>
    private static ParsedAskQuestion? ParseAskUserQuestion(System.Collections.Generic.IDictionary<string, object> toolInput)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(toolInput);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("questions", out var qs)
                || qs.ValueKind != System.Text.Json.JsonValueKind.Array
                || qs.GetArrayLength() == 0)
                return null;

            // 只取第一条 question（Claude Code 的 AskUserQuestion 实际就发一条；
            // 多条时岛上聚焦第一条，其余仍可在终端/桌面端原生 UI 处理）。
            var q = qs[0];
            var title = q.TryGetProperty("question", out var question)
                ? (question.GetString() ?? "")
                : "";

            var options = new System.Collections.Generic.List<QuestionOption>();
            if (q.TryGetProperty("options", out var opts)
                && opts.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int i = 1;
                foreach (var opt in opts.EnumerateArray())
                {
                    var label = (opt.TryGetProperty("label", out var l) ? l.GetString() : "") ?? "";
                    var desc = (opt.TryGetProperty("description", out var d) ? d.GetString() : "") ?? "";
                    options.Add(new QuestionOption { Number = i++, Label = label, Description = desc });
                }
            }
            return new ParsedAskQuestion(title, options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// AskUserQuestion 文本详情块（PermissionFullDetail 用）："Q: …" + 编号选项 + 描述。
    /// 复用 <see cref="ParseAskUserQuestion"/>，不再单独走一遍 JSON。
    /// </summary>
    private static string FormatAskUserQuestion(System.Collections.Generic.IDictionary<string, object> toolInput)
    {
        var parsed = ParseAskUserQuestion(toolInput);
        if (parsed is not { } p) return "";

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(p.Title))
            sb.Append("Q: ").AppendLine(p.Title);
        foreach (var opt in p.Options)
        {
            sb.Append(opt.Number).Append(". ").AppendLine(opt.Label);
            if (!string.IsNullOrEmpty(opt.Description))
                sb.Append("   ").AppendLine(opt.Description);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 这条会话当前是不是 AskUserQuestion（而非普通工具权限）。
    /// 为 true 时 XAML 显示"选项按钮组 + Skip"，为 false 时显示原 Allow once/Deny。
    /// </summary>
    public bool IsQuestion
        => string.Equals(_session?.PermissionRequest?.ToolName, "AskUserQuestion",
                          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// AskUserQuestion 的结构化候选答案（questions[0].options[]）。
    /// 每项渲染成一个可点按钮，CommandParameter = Number。无 ToolInput / 解析失败 → 空。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<QuestionOption> QuestionOptions
    {
        get
        {
            var input = _session?.PermissionRequest?.ToolInput;
            if (input == null || input.Count == 0) return System.Array.Empty<QuestionOption>();
            return ParseAskUserQuestion(input)?.Options ?? System.Array.Empty<QuestionOption>();
        }
    }

    /// <summary>questions[0].question 文本（问题标题，给问题模式的小标题用）。</summary>
    public string QuestionTitle
    {
        get
        {
            var input = _session?.PermissionRequest?.ToolInput;
            if (input == null || input.Count == 0) return "";
            return ParseAskUserQuestion(input)?.Title ?? "";
        }
    }

    private static string ExtractPermissionDetail(PermissionRequest req)
    {
        if (req.ToolInput == null) return "";
        // 按惯例字段挑最有信息量的一项给标题
        foreach (var key in new[] { "url", "command", "file_path", "path", "query", "pattern" })
        {
            if (req.ToolInput.TryGetValue(key, out var v) && v != null)
            {
                var s = v.ToString() ?? "";
                if (s.Length > 80) s = s[..80] + "…";
                return s;
            }
        }
        return "";
    }

    public string ToolIcon => _session?.Tool switch
    {
        AgentTool.ClaudeCode => "🟣",
        AgentTool.Codex or AgentTool.CodexApp => "⚫",
        AgentTool.Cursor => "⚪",
        AgentTool.GeminiCLI => "🔵",
        AgentTool.KimiCLI => "🟡",
        AgentTool.OpenCode => "🟢",
        _ => "🤖"
    } ?? "🟣";

    public string Elapsed
    {
        get
        {
            var updatedAt = _session?.UpdatedAt ?? _runningInfo?.StartTime ?? DateTime.UtcNow;
            var e = DateTime.UtcNow - updatedAt;
            if (e.TotalHours >= 1) return $"{(int)e.TotalHours}h";
            if (e.TotalMinutes >= 1) return $"{(int)e.TotalMinutes}m";
            return "now";
        }
    }

    public IslandSessionItem(AgentSession session, SessionManager sessionManager, Action<string>? onDismiss = null, WorkspaceSettings? settings = null, Action<string>? onTogglePin = null)
    {
        _session = session;
        _sessionManager = sessionManager;
        _onDismiss = onDismiss;
        _settings = settings;
        _onTogglePin = onTogglePin;
    }

    public IslandSessionItem(RunningSessionInfo runningInfo, SessionManager sessionManager, Action<string>? onDismiss = null, WorkspaceSettings? settings = null, Action<string>? onTogglePin = null)
    {
        _runningInfo = runningInfo;
        _sessionManager = sessionManager;
        _onDismiss = onDismiss;
        _settings = settings;
        _onTogglePin = onTogglePin;
    }

    /// <summary>VM 在卡片复用时重置回调（构造时传的可能是首次的；复用走 Update 不重建，回调不变即可）。</summary>
    public void SetTogglePin(Action<string>? cb) => _onTogglePin = cb;

    /// <summary>就地更新为新的 AgentSession 并刷新所有派生属性（卡片复用，动画只在真状态变化时触发）。</summary>
    public void Update(AgentSession session)
    {
        _session = session;
        OnPropertyChanged(string.Empty); // 通知所有绑定重读（Title/Phase/StatusColorValue/IsRunningPhase…）
    }

    /// <summary>就地更新为新的 RunningSessionInfo（仅有进程信息、scan 还没扫到时）。</summary>
    public void Update(RunningSessionInfo info)
    {
        if (_session == null) _runningInfo = info;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// 小叉号：临时把这条 session 从灵动岛收起。再次活动（新一轮 Running，或
    /// 进入需关注 phase）时由 DynamicIslandViewModel 自动放回。
    /// </summary>
    [RelayCommand]
    private void Dismiss() => _onDismiss?.Invoke(Id);

    /// <summary>图钉按钮：切换固定状态（固定后"清理任务"不清它）。实际状态由 VM 的 _pinned 持有。</summary>
    [RelayCommand]
    private void TogglePin() => _onTogglePin?.Invoke(Id);

    // ── 权限模式切换三按钮（#4）——均为 BEST-EFFORT ──
    // Claude Code 切权限模式只能在终端按 Shift+Tab *循环*（accept edits / auto /
    // plan / normal），没有"直接设为模式 X"的幂等手段，所以这三个命令都只是请
    // SessionManager 发一次 Shift+Tab 循环一格，无法保证精确停在对应模式；Claude
    // Desktop 端无可靠机制（仅记日志后 no-op）。详见 SessionManager.SendModeSwitchAsync。
    // Best-effort: one Shift+Tab cycle per click; exact target mode not guaranteed.

    [RelayCommand]
    private async Task AcceptEditsAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.SendModeSwitchAsync(_session.Id, ModeKind.AcceptEdits);
    }

    [RelayCommand]
    private async Task AutoModeAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.SendModeSwitchAsync(_session.Id, ModeKind.Auto);
    }

    [RelayCommand]
    private async Task PlanModeAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.SendModeSwitchAsync(_session.Id, ModeKind.Plan);
    }

    // ── 快捷回复（F1）：在卡片上敲一句直接发进该会话（粘贴 + 回车），不用切回终端 ──
    // 默认收起，点卡片上的回复图标才展开输入框（避免每张卡常驻输入框撑大灵动岛）。
    [ObservableProperty] private bool _showQuickReply;
    [ObservableProperty] private string _quickReplyInput = "";
    [ObservableProperty] private string? _quickReplyStatus;
    private bool _busy; // 防重入：发送 / 模型切换进行中时忽略再次触发

    [RelayCommand]
    private void ToggleQuickReply() => ShowQuickReply = !ShowQuickReply;

    // 用户继续输入时清掉上次的状态提示。
    partial void OnQuickReplyInputChanged(string value)
    {
        if (!string.IsNullOrEmpty(QuickReplyStatus))
            QuickReplyStatus = null;
    }

    [RelayCommand]
    private async Task SendQuickReplyAsync()
    {
        if (_sessionManager == null || _session == null) return;
        var text = QuickReplyInput;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_busy) return;
        _busy = true;
        try
        {
            QuickReplyStatus = Loc.Get("Reply_Sending");
            var result = await _sessionManager.SendQuickReplyAsync(_session.Id, text);
            if (result.Ok)
            {
                QuickReplyInput = "";
                QuickReplyStatus = Loc.Get("Reply_Sent");
            }
            else
            {
                QuickReplyStatus = QuickReplyReasonText(result.Reason);
            }
        }
        catch (Exception ex)
        {
            // 与 SwitchGlobalModelAsync 一致：兜住注入路径的异常，绝不让它从 async 命令逃逸崩溃。
            System.Diagnostics.Debug.WriteLine($"SendQuickReplyAsync failed: {ex.Message}");
            QuickReplyStatus = Loc.Get("Reply_SendError");
        }
        finally { _busy = false; }
    }

    private static string QuickReplyReasonText(string? reason) => reason switch
    {
        "empty" => Loc.Get("Reply_Empty"),
        "too long" => Loc.Get("Reply_TooLong"),
        "no-session" => Loc.Get("Reply_NoSession"),
        "no-terminal" or "no-terminal-match" => Loc.Get("Reply_NoTerminal"),
        "foreground-mismatch" => Loc.Get("Reply_ForegroundMismatch"),
        "foreground-lost" => Loc.Get("Reply_ForegroundLost"),
        "inject-error" => Loc.Get("Reply_InjectError"),
        "desktop-activate-failed" or "no-desktop-window" => Loc.Get("Reply_DesktopActivateFailed"),
        "clipboard-failed" => Loc.Get("Reply_ClipboardFailed"),
        _ => Loc.Get("Reply_SendFailed")
    };


    [RelayCommand]
    private async Task JumpAsync()
    {
        // 优先通过 AgentSession 跳转 —— 不再要求 JumpTarget 非空：
        //   - desktop session 走 ActivateClaudeDesktopWindow（根本不需要 cwd）
        //   - CLI session 在 JumpToSessionAsync 里有 ResolveFallbackWorkingDirectory 兜底
        // 之前的 `_session?.JumpTarget != null` 把 JumpTarget 因任何原因为 null 的卡片
        // 直接吞掉（点击无反应），尤其卡 desktop session 在 watcher 重新 emit JumpTarget
        // 的瞬间。
        if (_session != null && _sessionManager != null)
        {
            await _sessionManager.JumpToSessionAsync(_session.Id);
            return;
        }

        // 仅有 RunningSessionInfo（transcript 还没扫到）—— 用 cwd 启动新终端
        if (_runningInfo?.WorkingDirectory != null && _sessionManager != null)
        {
            await _sessionManager.JumpToWorkingDirectoryAsync(_runningInfo.WorkingDirectory);
            return;
        }
    }

    /// <summary>
    /// 1./2./3. 按钮：按 entrypoint 分流响应权限 —— CLI 注入终端按键，Claude Desktop
    /// 用 UIA 点 Electron 窗口里的允许/拒绝按钮（桌面端没有终端可注入）。两条路径都在
    /// 成功后本地清岛卡。
    /// </summary>
    [RelayCommand]
    private async Task RespondYesAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondToPermissionAsync(_session.Id, '1');
    }

    [RelayCommand]
    private async Task RespondAlwaysAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondToPermissionAsync(_session.Id, '2');
    }

    [RelayCommand]
    private async Task RespondNoAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondToPermissionAsync(_session.Id, '3');
    }

    /// <summary>
    /// AskUserQuestion 选项按钮：按 1-based 编号回答。CommandParameter 绑 QuestionOption.Number
    /// （XAML 里是 int；防御性兼容 string）。按 entrypoint 分流（CLI 终端注入数字 / Claude
    /// Desktop UIA 点选项行），成功后清岛卡 —— 全在 SessionManager.AnswerQuestionAsync 里。
    /// </summary>
    [RelayCommand]
    private async Task AnswerQuestionAsync(object? optionNumber)
    {
        if (_sessionManager == null || _session == null) return;
        int n;
        switch (optionNumber)
        {
            case int i: n = i; break;
            case string s when int.TryParse(s, out var parsed): n = parsed; break;
            default: return;
        }
        if (n < 1) return;
        await _sessionManager.AnswerQuestionAsync(_session.Id, n, skip: false);
    }

    /// <summary>AskUserQuestion 的 "Skip" 按钮：跳过该问题（best-effort，见 SessionManager）。</summary>
    [RelayCommand]
    private async Task SkipQuestionAsync()
    {
        if (_sessionManager == null || _session == null) return;
        await _sessionManager.AnswerQuestionAsync(_session.Id, 0, skip: true);
    }

    // 旧 Approve/Deny/AlwaysAllow 命令保留作 IIslandSession 接口兼容兜底
    [RelayCommand]
    private void Approve()
    {
        if (_sessionManager != null && _session != null)
            _sessionManager.ResolvePermission(_session.Id, true);
    }

    [RelayCommand]
    private void Deny()
    {
        if (_sessionManager != null && _session != null)
            _sessionManager.ResolvePermission(_session.Id, false);
    }

    [RelayCommand]
    private void AlwaysAllow()
    {
        if (_sessionManager == null || _session == null) return;
        var rule = _session.PermissionRequest?.SuggestedAlwaysAllow;
        _sessionManager.ResolvePermission(_session.Id, true, rule);
    }
}
