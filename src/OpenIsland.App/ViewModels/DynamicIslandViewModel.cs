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
    private readonly UpdateService _update;
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
    /// 用户用小叉号 / "清理任务" 临时收起的 session。key=sessionId。
    ///
    /// 判定改用 **transcript 内容水位**（mtime）而不是 phase：phase 会说谎 ——
    /// SyncProcessStatus 在进程存活翻转时会把 phase 拉成 Running（同目录/标题模糊匹配，
    /// 任何 claude 进程出现都能"点亮"一批历史会话），按 phase 判"新一轮活动"会让
    /// 清掉的卡片过一阵子集体复活（实测根因）。mtime 不会说谎：转录文件真有新字节
    /// 落盘才算"有新活动"。语义：
    ///   · 收起后本轮还在写（mtime 持续前进）→ 水位跟进，继续隐藏
    ///   · 停笔超过 DismissQuietGap → sawQuiet=true（本轮收尾），仍隐藏
    ///   · 安静后 mtime 再次前进（真·新内容）→ 移出、重新显示
    ///   · 任意时刻进入 WaitingForApproval/WaitingForAnswer（需关注）→ 立即移出、
    ///     重新显示（阻塞性 prompt 不允许被永久藏掉）
    /// 进程退出 / 重启应用后自然清空（仅内存，符合"临时"语义）。
    /// </summary>
    private sealed class DismissRecord
    {
        public bool SawQuiet;       // 收起后是否已观察到"停笔"（水位静止超过 DismissQuietGap）
        public DateTime Watermark;  // 收起后见过的最新 transcript 写入时间（UTC）
    }

    private readonly Dictionary<string, DismissRecord> _dismissed = new();

    /// <summary>转录停笔多久算"本轮收尾"。太短：长工具静默期会被误判收尾，
    /// 工具出结果时卡片弹回；太长：清掉后紧接着的新一轮可能被吞。</summary>
    private static readonly TimeSpan DismissQuietGap = TimeSpan.FromSeconds(120);

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

    // ── 会话来源筛选（命令栏按钮，三态循环）──
    // 0=全部，1=仅终端(CLI)，2=仅客户端(Claude Desktop)。依据 ClaudeMetadata.Entrypoint
    //（"claude-desktop" 为客户端，其余/缺失按终端算）。仅内存态，不持久化。
    [ObservableProperty] private int _sourceFilter;

    /// <summary>筛选按钮 ToolTip（随状态/语言重建）。</summary>
    [ObservableProperty] private string _sourceFilterTip = "";

    partial void OnSourceFilterChanged(int value)
    {
        SourceFilterTip = Loc.Get(value switch
        {
            1 => "Island_SrcFilter_Cli",
            2 => "Island_SrcFilter_Desktop",
            _ => "Island_SrcFilter_All",
        });
        RefreshSessions();
    }

    /// <summary>命令栏来源筛选按钮：全部 → 仅终端 → 仅客户端 → 全部 循环。</summary>
    [RelayCommand]
    private void CycleSourceFilter() => SourceFilter = (SourceFilter + 1) % 3;

    /// <summary>该会话是否通过当前来源筛选（session 为 null 的占位卡按终端算）。</summary>
    private bool PassesSourceFilter(AgentSession? session)
    {
        if (SourceFilter == 0) return true;
        var isDesktop = string.Equals(session?.ClaudeMetadata?.Entrypoint, "claude-desktop",
            StringComparison.OrdinalIgnoreCase);
        return SourceFilter == 2 ? isDesktop : !isDesktop;
    }

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

    // ── 胶囊背景样式：液态玻璃 / 纯黑（卡片菜单里的"背景样式"按钮）──
    /// <summary>胶囊背景是否用液态玻璃。初值取自 WorkspaceSettings（构造时写 backing field）。
    /// 改动经 OnLiquidGlassEnabledChanged 落盘；DynamicIslandWindow 监听本属性变化启/停离屏渲染循环。</summary>
    [ObservableProperty] private bool _liquidGlassEnabled;

    /// <summary>离屏渲染出的玻璃帧（MainBorder.Background 经 GlassBackgroundConverter 绑这里）。
    /// 由 DynamicIslandWindow 每帧写入；null 或开关关闭 → 回退纯黑 #0D0D0D。</summary>
    [ObservableProperty] private System.Windows.Media.ImageSource? _glassFrame;

    partial void OnLiquidGlassEnabledChanged(bool value)
    {
        _settings.SetLiquidGlassEnabled(value);
        if (!value) GlassFrame = null; // 关 → 立即丢掉玻璃帧，背景回纯黑
    }

    /// <summary>"背景样式"按钮命令：液态玻璃 ↔ 纯黑。</summary>
    [RelayCommand] private void ToggleLiquidGlass() => LiquidGlassEnabled = !LiquidGlassEnabled;

    // ── 正在播放 · 波浪律动：头部标题跑马灯显示曲名 + 胶囊内叠加音浪 ──

    private readonly NowPlayingService _nowPlaying = new();
    private readonly AudioLevelReactor _audioReactor = new();
    private readonly System.Windows.Threading.DispatcherTimer _waveAmplitudeTimer;

    /// <summary>换封面判重用的指纹（长度 + 稀疏采样字节的 hash）。故意不比较 Title/Artist——
    /// 网易云等播放器常常先把新曲名/歌手推给 SMTC，隔一拍才把新封面推上去；如果按曲名判重，
    /// 封面姗姗来迟的那次事件会因为"曲名没变"被直接跳过，波浪颜色就会永远卡在换歌那一刻
    /// 抓到的旧封面上（这正是"波浪颜色永远是上一首"这个 bug 的根因）。改成认封面内容本身
    /// 变没变，不管曲名此刻是不是"新"的。</summary>
    private int? _lastThumbnailFingerprint;

    /// <summary>波浪律动总开关。初值取自 WorkspaceSettings（构造时写 backing field）。默认开启。
    /// 改动经 OnNowPlayingWaveEnabledChanged 落盘 + 启停 NowPlayingService/AudioLevelReactor。</summary>
    [ObservableProperty] private bool _nowPlayingWaveEnabled;

    /// <summary>SMTC 报的当前曲名；没有播放/未开启时为空串。</summary>
    [ObservableProperty] private string _nowPlayingTitle = "";

    /// <summary>是否"真的在播放"：开关打开 && 有活动会话 && IsPlaying==true。驱动波浪显隐与头部标题。</summary>
    [ObservableProperty] private bool _isNowPlayingActive;

    /// <summary>波浪三层配色（从前到后），来自专辑封面主色提取（AlbumPaletteExtractor.ExtractTop3），
    /// 换歌才重新算一次，不逐帧算。</summary>
    [ObservableProperty] private System.Windows.Media.Color _waveColor1 = System.Windows.Media.Color.FromRgb(0x6a, 0x5a, 0xcd);
    [ObservableProperty] private System.Windows.Media.Color _waveColor2 = System.Windows.Media.Color.FromRgb(0x40, 0x8a, 0xd6);
    [ObservableProperty] private System.Windows.Media.Color _waveColor3 = System.Windows.Media.Color.FromRgb(0x2f, 0x4f, 0x8f);

    /// <summary>0..1，波浪振幅；由 AudioLevelReactor.Level 经 50ms 定时器驱动。仅 IsNowPlayingActive
    /// 为 true 时才非零，否则钉在 0。</summary>
    [ObservableProperty] private double _waveAmplitude;

    partial void OnNowPlayingWaveEnabledChanged(bool value)
    {
        _settings.SetNowPlayingWaveEnabled(value);
        if (value)
        {
            _nowPlaying.Start();
            _audioReactor.Start();
            _waveAmplitudeTimer.Start();
        }
        else
        {
            // 关闭：两个服务都停掉省资源，波浪相关属性一并清零、不再更新。
            _nowPlaying.Dispose();
            _audioReactor.Dispose();
            _waveAmplitudeTimer.Stop();
            _lastThumbnailFingerprint = null;
            NowPlayingTitle = "";
            IsNowPlayingActive = false;
            WaveAmplitude = 0;
        }
    }

    /// <summary>命令栏"波浪律动"chip 命令：翻转开关。</summary>
    [RelayCommand] private void ToggleNowPlayingWave() => NowPlayingWaveEnabled = !NowPlayingWaveEnabled;

    partial void OnIsNowPlayingActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(HeaderTitleText));
        OnPropertyChanged(nameof(ShowNowPlayingWave));
    }
    partial void OnNowPlayingTitleChanged(string value) => OnPropertyChanged(nameof(HeaderTitleText));
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowNowPlayingWave));

    /// <summary>头部标题：正在播放时显示曲名（跑马灯），否则显示 "Open Island"。</summary>
    public string HeaderTitleText => IsNowPlayingActive && !string.IsNullOrEmpty(NowPlayingTitle)
        ? NowPlayingTitle
        : "Open Island";

    /// <summary>波浪是否真的要画出来：正在播放 && 岛处于收起（胶囊）态。展开时波浪不显示，
    /// 岛回到原来的样子——波浪只属于胶囊，不属于展开后的会话列表视图。</summary>
    public bool ShowNowPlayingWave => IsNowPlayingActive && !IsExpanded;

    /// <summary>NowPlayingService 的回调（可能来自非 UI 线程）：切回 UI 线程更新曲名/播放态；
    /// 换封面（指纹变化，见 _lastThumbnailFingerprint 的注释）才重新跑一遍主色提取，不逐帧算。</summary>
    private void OnNowPlayingChanged(object? sender, NowPlayingInfo? info)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (info == null)
            {
                NowPlayingTitle = "";
                IsNowPlayingActive = false;
                return;
            }

            NowPlayingTitle = info.Title ?? "";
            IsNowPlayingActive = NowPlayingWaveEnabled && info.IsPlaying;

            if (info.ThumbnailBytes != null)
            {
                int fingerprint = ThumbnailFingerprint(info.ThumbnailBytes);
                if (fingerprint != _lastThumbnailFingerprint)
                {
                    _lastThumbnailFingerprint = fingerprint;
                    var colors = AlbumPaletteExtractor.ExtractTop3(info.ThumbnailBytes);
                    WaveColor1 = colors[0];
                    WaveColor2 = colors[1];
                    WaveColor3 = colors[2];
                }
            }
        });
    }

    /// <summary>封面 byte[] 的轻量指纹：长度 + 等间隔采样若干字节做 hash。只用来判断"封面
    /// 内容变没变"，不追求密码学强度，算整张图的 hash 没必要。</summary>
    private static int ThumbnailFingerprint(byte[] bytes)
    {
        var hash = new HashCode();
        hash.Add(bytes.Length);
        int step = Math.Max(1, bytes.Length / 64);
        for (int i = 0; i < bytes.Length; i += step) hash.Add(bytes[i]);
        return hash.ToHashCode();
    }

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
            _webSync.ClearSelected(); // 关同步 = 清空选中
            WebSyncOn = false;
            WebSyncTip = Loc.Get("Web_Off_Tip");
            foreach (var s in Sessions) s.IsWebFeatured = false;
        }
    }

    /// <summary>
    /// 卡片状态圆点被点（仅网页同步开启时圆点可点）：切换该会话的"网页选中"状态。
    /// 选中任意会话后，网页**只显示**选中的会话（多选并行展示，每个带 60 条完整历史）；
    /// 全部取消选中则回到默认信息流。圆点橙色外圈 = 已选中。
    /// </summary>
    private void SyncSessionToWeb(string id)
    {
        if (string.IsNullOrEmpty(id) || !WebSyncOn) return;
        _webSync.ToggleSelected(id);
        foreach (var s in Sessions) s.IsWebFeatured = _webSync.IsSelected(s.Id);
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

    public DynamicIslandViewModel(SessionManager sessionManager, PopupWindowService popupService, SystemStatsService systemStats, MediaControlService media, PlanUsageService planUsage, WorkspaceSettings settings, ScreenshotService screenshot, SkillInstallService skillInstall, UpdateService update, WebSyncService webSync)
    {
        _sessionManager = sessionManager;
        _popupService = popupService;
        _systemStats = systemStats;
        _planUsage = planUsage;
        _media = media;
        _settings = settings;
        _screenshot = screenshot;
        _skillInstall = skillInstall;
        _update = update;
        _webSync = webSync;

        // 网页同步默认关，ToolTip 先放使用说明（开启后换成访问地址）
        WebSyncTip = Loc.Get("Web_Off_Tip");
        SourceFilterTip = Loc.Get("Island_SrcFilter_All");

        // accept 循环异常崩溃（非用户 Stop）：服务已自清理，这里把开关/圆点状态拨回"关"，
        // 否则地球按钮显示开启而服务实际已死。事件来自线程池线程，必须回 UI 线程。
        _webSync.StoppedUnexpectedly += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!WebSyncOn) return;
                WebSyncOn = false;
                WebSyncTip = Loc.Get("Web_Off_Tip");
                _webSync.ClearSelected();
                foreach (var s in Sessions) s.IsWebFeatured = false;
            });

        // 启动时把提示音开关对齐持久化值，并同步到 SoundService —— 否则 SoundService.Enabled
        // 默认 true，用户上次关了重启后仍会响一次才被纠正。
        // 直接写 backing field（非 SoundEnabled 属性）避免触发 OnSoundEnabledChanged 在
        // 构造期回写一遍 settings（值没变，没必要落盘）。
        _soundEnabled = settings.SoundEnabled;
        SoundService.Enabled = settings.SoundEnabled;

        // 胶囊背景样式对齐持久化值（写 backing field 不触发落盘）。DynamicIslandWindow 据此启/停玻璃渲染。
        _liquidGlassEnabled = settings.LiquidGlassEnabled;

        // 余额行显示模式对齐持久化值（写 backing field 不触发落盘）。下次启动恢复关闭时的状态。
        _showUsageChart = settings.ShowUsageChart;

        // 波浪律动开关对齐持久化值（写 backing field 不触发落盘），默认开启。
        _nowPlayingWaveEnabled = settings.NowPlayingWaveEnabled;
        _nowPlaying.NowPlayingChanged += OnNowPlayingChanged;
        _waveAmplitudeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _waveAmplitudeTimer.Tick += (_, _) => WaveAmplitude = IsNowPlayingActive ? _audioReactor.Level : 0;
        // 只在开关打开时才启动这两个服务 + 定时器，关闭时不占用系统资源。
        if (_nowPlayingWaveEnabled)
        {
            _nowPlaying.Start();
            _audioReactor.Start();
            _waveAmplitudeTimer.Start();
        }

        _sessionManager.SessionsChanged += OnSessionsChanged;
        _sessionManager.TaskCompleted += OnTaskCompleted;
        _systemStats.StatsUpdated += OnSystemStatsUpdated;
        _planUsage.UsageUpdated += OnPlanUsageUpdated;
        // 在线升级：订阅"发现新版"事件（来自后台线程，marshal 到 UI 线程，仿 OnSystemStatsUpdated），
        // 并启动一次启动静默检查（延迟几秒，仿 PlanUsageService 的延迟触发）。
        _update.UpdateAvailable += OnUpdateAvailable;
        _update.StartSilentCheck();
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
            // 来源筛选按钮 ToolTip 同理
            SourceFilterTip = Loc.Get(SourceFilter switch
            { 1 => "Island_SrcFilter_Cli", 2 => "Island_SrcFilter_Desktop", _ => "Island_SrcFilter_All" });
            // "取消"按钮文案是代码侧双语直出，语言切换时通知绑定重读
            OnPropertyChanged(nameof(SkillCancelLabel));
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

    private void OnSettingsChangedForModels(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(ModelChoices)));

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
        var withMtime = _sessionManager.GetAllSessions()
            .Where(s => s.Tool == AgentTool.ClaudeCode && !string.IsNullOrEmpty(s.ClaudeMetadata?.TranscriptPath))
            .Where(s => !IsTokenProbeSession(s)) // 滤掉 token 探针 ping 会话（历史 + 未来）
            .Select(s => new { Session = s, Mtime = TryGetMtime(s.ClaudeMetadata!.TranscriptPath!) })
            .Where(x => x.Mtime.HasValue)
            // 只保留活动时间 ≥ 启动锚定 cutoff（启动前 10 分钟）的会话。整个候选池就此收口，
            // 后面无论 cwd 精确匹配还是按新鲜度凑对，都只可能挑到这些会话——一次性把启动时
            // 一堆陈年历史会话挡在外面（见 _activityCutoffUtc 的说明）。
            .Where(x => x.Mtime!.Value >= _activityCutoffUtc)
            .OrderByDescending(x => x.Mtime!.Value)
            .ToList();
        var sessionsByRecency = withMtime.Select(x => x.Session).ToList();
        // 折叠"同一对话被拆成多个 transcript"的重复分身：Claude Code 上下文溢出后继续会
        // 新建一个 transcript 文件（新 sessionId），但逻辑上还是同一个对话 —— 同 cwd、同
        // 标题（都取自同一条首条用户消息）。列表已按 mtime 降序，这里保留最新那个（当前
        // 还在写的），丢掉旧分身，避免同一个对话在灵动岛上冒出两张卡（用户报的"两个
        // https://github.com/... 项目"根因：991cef1e 当前会话 + 3a018db1 溢出前的旧 transcript）。
        if (sessionsByRecency.Count > 1)
        {
            var seenLogical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<AgentSession>(sessionsByRecency.Count);
            foreach (var s in sessionsByRecency)
            {
                var cwd = (s.JumpTarget?.WorkingDirectory ?? "").TrimEnd('\\', '/');
                var key = cwd + "\n" + (s.Title ?? "");
                if (seenLogical.Add(key)) deduped.Add(s);
            }
            sessionsByRecency = deduped;
        }
        // id → 转录 mtime（UTC），给收起状态机做内容水位比对
        var mtimeById = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var x in withMtime) mtimeById[x.Session.Id] = x.Mtime!.Value;

        var assignedIds = new HashSet<string>();
        var desired = new List<(string Key, AgentSession? Session, RunningSessionInfo? Info)>();

        foreach (var r in runningProcesses)
        {
            AgentSession? session = null;

            // 1. cwd 命中（罕见，但 hook 写入的 session 会有正确 cwd）—— 精确身份匹配，
            //    是这个进程的真身。候选池 sessionsByRecency 已按启动 cutoff 收口过，命中
            //    的必然是近 10 分钟活跃的会话。
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

            // 2. 没 cwd 时退回到"最近被改写过的 .jsonl 对应的 scan 会话"。候选池已按启动
            //    cutoff 收口，这里挑到的必然是近 10 分钟活跃的，不会再把陈年历史会话翻出来
            //    凑够 Claude Desktop 那十几个 helper 进程的数。
            session ??= sessionsByRecency.FirstOrDefault(s => !assignedIds.Contains(s.Id));

            // 配不到近期真实会话的进程一律不产卡 —— 彻底去掉旧的"占位卡"分支。占位卡本意
            // 是给"进程刚起、transcript 还没扫到"的新会话兜底，但真实新会话几乎立刻就写
            // transcript、下一拍就成为真会话；而这个分支实际把 Claude Desktop 的一堆 helper
            // claude.exe（cwd 是 C:\WINDOWS\system32 / C:\Users\ludiw / 项目根等，根本不是用户
            // 对话）显示成了"WINDOWS/system32""Users/ludiw"这种根本没运行的幽灵卡（用户反复报的
            // 根因）。灵动岛只显示**有真实 transcript 的对话**——正在跑的对话必然有正在写的
            // transcript，不会被漏掉。
            if (session == null) continue;

            assignedIds.Add(session.Id);
            if (IsHiddenByDismiss(session.Id, session.Phase,
                    mtimeById.TryGetValue(session.Id, out var mt) ? mt : null)) continue;
            if (!PassesSourceFilter(session)) continue; // 来源筛选（全部/终端/客户端）
            desired.Add((session.Id, session, null));
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
                item.SetSyncWeb(SyncSessionToWeb);
                item.IsWebFeatured = WebSyncOn && _webSync.IsSelected(key);
                Sessions.Insert(i, item); // 前 i 项已就位，i <= Count，安全
            }
            else
            {
                if (found != i) Sessions.Move(found, i);
                if (session != null) Sessions[i].Update(session); else Sessions[i].Update(info!);
                Sessions[i].IsPinned = _pinned.Contains(key); // 图钉状态以 VM 的 _pinned 为准
                Sessions[i].SetSyncWeb(SyncSessionToWeb);
                Sessions[i].IsWebFeatured = WebSyncOn && _webSync.IsSelected(key);
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

    /// <summary>
    /// 会话活跃度的**固定**下限时刻，= 应用启动那一刻往前推 10 分钟，构造时算一次、之后不变。
    /// 只有 transcript 最后写入时间 ≥ 这个 cutoff 的会话才准上灵动岛。
    ///
    /// 这是用户明确设计的语义："每次启动只显示 10 分钟以内进行的对话，之后不再遵守 10 分钟"——
    /// 关键是 cutoff **锚定在启动时刻**、不随 now 滑动：
    ///   · 启动时：只有近 10 分钟活跃过的会话 mtime ≥ cutoff，能显示；一堆早已结束的历史
    ///     会话（vivado/journal/hello 等）mtime 远早于 cutoff，被挡在外面。
    ///   · 启动之后：任何新活动会把该会话 mtime 推到 now，必然 ≥ 这个固定 cutoff，自动显示 ——
    ///     等于"10 分钟规则只在启动那一下生效，之后新对话照常冒出来"。
    /// Claude Desktop 常驻十几个 helper claude.exe，不这么卡就会把陈年会话翻出来填坑。
    /// </summary>
    private readonly DateTime _activityCutoffUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);

    /// <summary>是否是 PlanUsageService 的 token 探针会话（`claude --print ping`）：标题（首条
    /// user 消息）恰好是 "ping"，或 transcript 落在专用探针目录 openisland-token-probe 下。
    /// 任一命中即排除——既滤掉未来隔离到专用目录的探针，也一并滤掉历史遗留在仓库目录里的
    /// ping（旧版本没设专用 cwd，它们混在真实项目目录里）。</summary>
    private static bool IsTokenProbeSession(AgentSession s)
    {
        if (string.Equals(s.Title?.Trim(), "ping", StringComparison.Ordinal)) return true;
        var tp = s.ClaudeMetadata?.TranscriptPath;
        return !string.IsNullOrEmpty(tp)
            && tp.Contains("openisland-token-probe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>造一条"收起"记录。wasActive=true（收起那一刻会话真的在 Running/需关注）才走
    /// 原来那套"水位追着写、停笔 DismissQuietGap 秒才算收尾"逻辑，避免中途误放行。
    /// wasActive=false（收起时已经 Idle/Completed，或转录早已不新鲜）直接 SawQuiet=true，
    /// baseline 用当下真实 mtime——之后转录只要再有一丁点新字节（哪怕几秒后）就立刻放回，
    /// 不用再空等 120 秒。
    ///
    /// 这是修复"清理任务后紧接着又聊了几句，会话却一直不回到灵动岛"这个 bug 的关键：以前
    /// 无论收起时是不是活跃，一律 Watermark=MinValue + SawQuiet=false，若用户在 120 秒安静期
    /// 耗尽之前就发了新消息（日常对话里几乎总是这样——清理任务的场景本来就是"看着像结束了"
    /// 才会去清，而清完立刻接着聊在活跃对话里是常态），新一轮的写入会被当成"上一轮还没写完"
    /// 而持续吞掉，水位跟着一直追、SawQuiet 永远等不到 true，卡片就再也回不来了。</summary>
    private DismissRecord MakeDismissRecord(bool wasActive, string? transcriptPath)
    {
        var mtimeNow = !string.IsNullOrEmpty(transcriptPath) ? TryGetMtime(transcriptPath!) : null;
        return new DismissRecord
        {
            SawQuiet = !wasActive,
            Watermark = wasActive ? DateTime.MinValue : (mtimeNow ?? DateTime.MinValue),
        };
    }

    /// <summary>用户点小叉号：记录收起，立刻刷新列表把卡片移出。</summary>
    private void DismissSession(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var s = _sessionManager.GetAllSessions().FirstOrDefault(x => x.Id == id);
        bool wasActive = s?.Phase is SessionPhase.Running or SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer;
        _dismissed[id] = MakeDismissRecord(wasActive, s?.ClaudeMetadata?.TranscriptPath);
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
    ///   · 其余收起的 session —— 停笔后继续藏；转录真有新内容落盘（新一轮活动）
    ///     或进入需关注，由既有 IsHiddenByDismiss（内容水位版）自动放回。
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

        // 按 id 建个索引，给第一个循环（Sessions 里是 IslandSessionItem 包装，拿不到
        // ClaudeMetadata）查真实转录路径用，好算 MakeDismissRecord 的 mtime 基线。
        var allById = _sessionManager.GetAllSessions()
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

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
            // 已在 _dismissed 里的不要覆盖其水位/进度（保持它在状态机里的既有状态）
            if (!_dismissed.ContainsKey(s.Id))
            {
                var tp = allById.TryGetValue(s.Id, out var full) ? full.ClaudeMetadata?.TranscriptPath : null;
                // 走到这里已经确认不是 Running/需关注，wasActive 恒为 false——直接跳过
                // 120 秒安静期，之后随便一点新内容就立刻放回。
                _dismissed[s.Id] = MakeDismissRecord(wasActive: false, tp);
            }
        }

        // 清理范围必须是**此刻所有已知的安静会话**，不只是可见列表：卡数 = 运行进程数，
        // 只清可见的 N 张，进程槽会立刻把第 N+1 名往后的陈年会话顶上来补位（实测：清完
        // 马上冒出从未显示过的旧卡），视觉上等于"没清干净"。全量记下水位后补位无从发生，
        // 之后谁的转录真有新内容谁再回来 —— 与单卡收起同一套状态机。
        foreach (var s in _sessionManager.GetAllSessions())
        {
            if (s.Tool != AgentTool.ClaudeCode || string.IsNullOrEmpty(s.Id)) continue;
            if (s.Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer) continue;
            if (_pinned.Contains(s.Id)) continue;
            // "正在活动"以转录新鲜度为准而非 phase —— 历史状态里可能残留进程同步伪造的
            // Running（清不掉就成了永远赶不走的卡）。2 分钟内有写入的才保留。
            var tp = s.ClaudeMetadata?.TranscriptPath;
            if (!string.IsNullOrEmpty(tp) && TryGetMtime(tp!) is { } m
                && DateTime.UtcNow - m <= TimeSpan.FromMinutes(2))
                continue;
            if (!_dismissed.ContainsKey(s.Id))
                _dismissed[s.Id] = MakeDismissRecord(wasActive: false, tp);
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

    /// <summary>
    /// 是否正在安装。改为 ObservableProperty 是为了让 XAML 能据此切换"安装/取消"按钮，
    /// 同时通过 NotifyCanExecuteChangedFor 联动两个命令的 CanExecute（安装中禁用"安装"、
    /// 仅安装中启用"取消"）。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSkillCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSkillInstallCommand))]
    private bool _isSkillInstalling;

    /// <summary>当前安装任务的取消源：UI"取消"按钮触发它 → 经服务的联动令牌杀进程树。</summary>
    private CancellationTokenSource? _skillInstallCts;

    /// <summary>"取消"按钮文案（双语）—— 本地化键在 Loc.cs，这里不便改它，按现有 IsEnglish 直出。</summary>
    public string SkillCancelLabel => Loc.Instance.IsEnglish ? "Cancel" : "取消";

    /// <summary>
    /// 解析输入 → 后台逐条跑 claude plugin 命令。安装过程中按钮已被 CanExecute 禁用；
    /// 万一仍被触发（如绑定时序边角），给出"安装中"提示而非静默 early-return。
    /// 进度回调来自后台线程，需经 Dispatcher 回 UI 线程；await 之后由 async 上下文
    /// 自动回到 UI 线程，结果状态直接赋值即可。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallSkill))]
    private async Task InstallSkill()
    {
        if (IsSkillInstalling)
        {
            SkillInstallStatus = Loc.Get("Skill_Installing");
            return;
        }

        var cmds = SkillInstallService.ParseCommands(SkillInstallInput);
        if (cmds.Count == 0)
        {
            SkillInstallStatus = Loc.Get("Skill_Invalid");
            return;
        }

        // 每次安装开新令牌；取消按钮持有它，结束时归还。
        var cts = new CancellationTokenSource();
        _skillInstallCts = cts;
        IsSkillInstalling = true;
        SkillInstallStatus = Loc.Get("Skill_Installing");
        try
        {
            var (ok, output) = await _skillInstall.RunAsync(cmds,
                p => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => SkillInstallStatus = p),
                cts.Token);
            SkillInstallStatus = ok
                ? Loc.Get("Skill_Done")
                : Loc.Format("Skill_Failed", output.Length <= 300 ? output : output[^300..]);
        }
        finally
        {
            // 无论成功/失败/取消，都复位状态并释放令牌 —— 保证按钮可再次点击。
            IsSkillInstalling = false;
            _skillInstallCts = null;
            cts.Dispose();
        }
    }

    /// <summary>"安装"按钮 CanExecute：安装进行中禁用，避免并发重入。</summary>
    private bool CanInstallSkill() => !IsSkillInstalling;

    /// <summary>
    /// "取消"按钮：触发取消令牌 —— 服务侧的联动令牌随即取消，杀掉当前 powershell 进程树。
    /// 状态文案与 finally 复位由 InstallSkill 的收尾统一处理（取消后服务返回 Ok=false，
    /// 输出含 "canceled"）。仅在安装中可用。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSkillInstalling))]
    private void CancelSkillInstall()
    {
        SkillInstallStatus = Loc.Instance.IsEnglish ? "Canceling…" : "正在取消…";
        try { _skillInstallCts?.Cancel(); } catch { /* 已释放则忽略 */ }
    }

    // ── 在线升级：检查 GitHub Release → 发现新版提示面板 → 下载 Setup.exe 静默安装重启 ──

    /// <summary>是否存在可安装的新版本（控制"发现新版"提示面板可见性 + 检查更新按钮小红点）。
    /// Whether an installable update exists (drives the panel visibility and the button badge).</summary>
    [ObservableProperty] private bool _updateAvailable;

    /// <summary>更新状态文案（"检查中…" / "已是最新 vX.Y.Z" / "下载中 xx%" / 错误等）。
    /// Update status text (checking / up-to-date / downloading / error).</summary>
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>最新版本号（X.Y.Z）。/ Latest version (X.Y.Z).</summary>
    [ObservableProperty] private string _latestVersion = "";

    /// <summary>最新版更新日志。/ Latest release notes.</summary>
    [ObservableProperty] private string _updateNotes = "";

    /// <summary>下载进度 0..1（进度条绑定）。/ Download progress 0..1 (bound to the progress bar).</summary>
    [ObservableProperty] private double _updateProgress;

    /// <summary>最新版 Setup .exe 下载地址（CheckForUpdateAsync 返回，InstallUpdate 用）。
    /// Latest Setup .exe URL (from CheckForUpdateAsync, consumed by InstallUpdate).</summary>
    private string? _setupUrl;

    /// <summary>是否正在下载/安装中（防重入 + 控制"立即更新"按钮 CanExecute）。
    /// Whether a download/install is in progress (re-entrancy guard + InstallUpdate CanExecute).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    /// <summary>检查更新菜单是否展开（命令栏按钮 + 面板的开关）。/ Whether the update panel is open.</summary>
    [ObservableProperty] private bool _updateMenuOpen;

    /// <summary>
    /// 后台静默检查发现新版 → 填面板字段并亮提示。来自后台线程，必须 marshal 回 UI 线程
    /// （写法同 OnSystemStatsUpdated）。
    /// Background silent check found an update; populate the panel on the UI thread.
    /// </summary>
    private void OnUpdateAvailable(object? sender, UpdateInfo info)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            UpdateAvailable = info.HasUpdate;
            LatestVersion = info.LatestVersion;
            UpdateNotes = info.Notes;
            _setupUrl = info.SetupUrl;
            UpdateStatus = "";
        });
    }

    /// <summary>
    /// 命令栏"检查更新"按钮：手动查一次。有新版 → 亮提示面板并填版本/日志；无 → 状态显示"已是最新"。
    /// Manual "check for updates": queries once, shows the panel on a hit or an up-to-date message otherwise.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdate()
    {
        UpdateMenuOpen = true;
        UpdateStatus = Loc.Get("Update_Checking");
        try
        {
            var info = await _update.CheckForUpdateAsync();
            UpdateAvailable = info.HasUpdate;
            LatestVersion = info.LatestVersion;
            UpdateNotes = info.Notes;
            _setupUrl = info.SetupUrl;
            UpdateStatus = info.HasUpdate ? "" : Loc.Format("Update_UpToDate", info.CurrentVersion);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CheckUpdate failed: {ex.Message}");
            UpdateStatus = Loc.Get("Update_CheckFailed");
        }
    }

    /// <summary>
    /// "立即更新"按钮：下载 Setup.exe（原生 + 镜像兜底）并静默安装、退出本进程让安装器接管。
    /// 进度经 IProgress 回 UI 线程更新进度条与"下载中 xx%"文案。安装成功后本进程会被关掉，
    /// 不会走到 finally（成功路径里安装器 1s 后 Shutdown）；失败/无 URL 才复位 IsUpdating。
    /// "Install now": downloads (native + mirror fallback) and silently installs, then exits.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (IsUpdating) return;
        if (string.IsNullOrWhiteSpace(_setupUrl))
        {
            UpdateStatus = Loc.Get("Update_CheckFailed");
            return;
        }

        IsUpdating = true;
        UpdateProgress = 0;
        UpdateStatus = Loc.Format("Update_Downloading", 0);
        // 进度回调来自后台线程，marshal 回 UI 线程更新进度条 + 文案。
        var progress = new Progress<double>(p =>
        {
            UpdateProgress = p;
            UpdateStatus = Loc.Format("Update_Downloading", (int)Math.Round(p * 100));
        });
        try
        {
            var ok = await _update.DownloadAndInstallAsync(_setupUrl, progress);
            // 成功路径：安装器已启动、本进程即将被 Shutdown，这里通常看不到；失败才提示。
            if (!ok) UpdateStatus = Loc.Get("Update_DownloadFailed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InstallUpdate failed: {ex.Message}");
            UpdateStatus = Loc.Get("Update_DownloadFailed");
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>"立即更新"CanExecute：下载/安装进行中禁用，避免并发重入。
    /// InstallUpdate CanExecute: disabled while a download/install is running.</summary>
    private bool CanInstallUpdate() => !IsUpdating;

    /// <summary>"稍后"按钮：收起提示面板（不再亮），下次启动/手动检查仍会再提示。
    /// "Later": collapse the panel; the next startup/manual check will surface it again.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable = false;
        UpdateMenuOpen = false;
    }

    /// <summary>
    /// 返回 true = 这条 session 当前应保持隐藏（被收起且还没"真有新活动"）。
    /// 按 **transcript 内容水位** 推进状态机（见 _dismissed 字段注释）——
    /// 故意不看 Running/Idle phase：phase 会被进程存活同步伪造（本次 bug 根因），
    /// 而转录文件的 mtime 只有真写入新内容才会前进。
    /// </summary>
    private bool IsHiddenByDismiss(string id, SessionPhase? phase, DateTime? transcriptMtimeUtc)
    {
        if (string.IsNullOrEmpty(id) || !_dismissed.TryGetValue(id, out var rec))
            return false;

        // 需关注 phase 压过收起 —— 阻塞性 prompt 必须冒出来（hook 写入，可信）
        if (phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer)
        {
            _dismissed.Remove(id);
            return false;
        }

        // 没有转录的占位卡：无从判断"新活动"，维持隐藏（进程消失后由剪枝回收）
        if (transcriptMtimeUtc is not { } mtime)
            return true;

        if (!rec.SawQuiet)
        {
            // 收起时这一轮可能还在写：水位跟着最新写入走，不算"新活动"
            if (mtime > rec.Watermark) rec.Watermark = mtime;
            // 停笔超过间隔 = 本轮收尾，之后水位再前进才是"新一轮"
            if (DateTime.UtcNow - rec.Watermark > DismissQuietGap)
                rec.SawQuiet = true;
            return true;
        }

        // 安静之后转录又有新字节 = 真·新活动 → 重新显示
        if (mtime > rec.Watermark)
        {
            _dismissed.Remove(id);
            return false;
        }

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
        _update.UpdateAvailable -= OnUpdateAvailable;
        _settings.Changed -= OnSettingsChangedForModels;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _greenStatusTimer.Stop();
        _greenStatusTimer.Dispose();
        _nowPlaying.NowPlayingChanged -= OnNowPlayingChanged;
        _nowPlaying.Dispose();
        _audioReactor.Dispose();
        _waveAmplitudeTimer.Stop();
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

    /// <summary>是否被"同步到网页"置顶（点状态圆点切换）。由 VM 按 WebSyncService.FeaturedSessionId 驱动，
    /// 圆点外圈显示橙色描边。</summary>
    [ObservableProperty] private bool _isWebFeatured;
    private Action<string>? _onSyncWeb;
    private readonly WorkspaceSettings? _settings;

    public string Id => _session?.Id ?? _runningInfo?.SessionId ?? "";

    /// <summary>自动标题（首条消息 / 运行目录），用户没手动改名时用它。</summary>
    private string AutoTitle => _session?.Title ?? GetRunningInfoTitle() ?? "Claude";

    /// <summary>卡片显示名：用户手动改过（settings 里按会话 id 存了）就用自定义名，否则回落自动标题。</summary>
    public string Title => _settings?.GetSessionTitle(Id) ?? AutoTitle;

    /// <summary>是否正处于"内联改名"编辑态（标题变成可编辑输入框）。</summary>
    [ObservableProperty] private bool _isEditingTitle;

    /// <summary>编辑态里输入框绑的文本。</summary>
    [ObservableProperty] private string _editableTitle = "";

    /// <summary>铅笔按钮：进入内联改名，预填当前显示名并全选。</summary>
    [RelayCommand]
    private void Rename()
    {
        EditableTitle = Title;
        IsEditingTitle = true;
    }

    /// <summary>提交改名：与自动标题相同（或空）→ 清除自定义名回落自动标题；不同 → 按会话 id
    /// 存进 settings.json 持久化（下次启动仍记得）。由 code-behind 在 Enter / 失焦时调用。</summary>
    public void CommitRename()
    {
        if (!IsEditingTitle) return;
        IsEditingTitle = false;
        var id = Id;
        if (string.IsNullOrEmpty(id)) return;
        var t = EditableTitle?.Trim() ?? "";
        _settings?.SetSessionTitle(id, (string.IsNullOrEmpty(t) || t == AutoTitle) ? null : t);
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>取消改名（Esc / 无变化失焦）：不改动，退出编辑态。</summary>
    public void CancelRename() => IsEditingTitle = false;
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
    ///
    /// CLI 会话额外追加 Claude Code 终端**自己**加的两个固定项："N+1. Type something." 和
    /// "N+2. Chat about this"（它们不在 tool_input 里，是终端 TUI 渲染时追加的）——用户要求
    /// 岛上的选项跟终端**原原本本一致**，编号也必须对齐，否则岛上点 3 会选中终端的第 3 项
    /// （而用户以为没有 3）。点这两项 = 注入对应数字，终端会进入"输入文字/聊天"态，用户去
    /// 终端继续打字，行为与直接在终端选一致。桌面端（Claude Desktop）没有这两项，不追加。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<QuestionOption> QuestionOptions
    {
        get
        {
            var input = _session?.PermissionRequest?.ToolInput;
            if (input == null || input.Count == 0) return System.Array.Empty<QuestionOption>();
            var opts = ParseAskUserQuestion(input)?.Options;
            if (opts == null || opts.Count == 0) return System.Array.Empty<QuestionOption>();

            var isDesktop = string.Equals(_session?.ClaudeMetadata?.Entrypoint, "claude-desktop",
                StringComparison.OrdinalIgnoreCase);
            if (isDesktop) return opts;

            var full = new System.Collections.Generic.List<QuestionOption>(opts.Count + 2);
            full.AddRange(opts);
            full.Add(new QuestionOption { Number = opts.Count + 1, Label = "Type something.", Description = "" });
            full.Add(new QuestionOption { Number = opts.Count + 2, Label = "Chat about this", Description = "" });
            return full;
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

    /// <summary>
    /// 这条会话当前是不是 ExitPlanMode（计划审阅），而非普通工具权限 / AskUserQuestion。
    /// 为 true 时 XAML 应显示完整的计划 Markdown 渲染 UI，而不是常规的 Allow once/Deny 单行详情。
    /// </summary>
    public bool IsPlanReview
        => string.Equals(_session?.PermissionRequest?.ToolName, "ExitPlanMode",
                          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ExitPlanMode 的 tool_input 通常长这样：{"plan": "完整的 markdown 文本..."}。
    /// 走 JsonElement 解析（与 ParseAskUserQuestion 同一套思路）：先把 Dictionary 重新序列化成
    /// JSON 再解析，取顶层 "plan" 字符串字段。解析失败或字段不存在一律返回空串，绝不抛异常
    /// 导致卡片渲染崩溃。
    /// </summary>
    private static string ExtractPlanMarkdown(System.Collections.Generic.IDictionary<string, object> toolInput)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(toolInput);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("plan", out var planElem)
                && planElem.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return planElem.GetString() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// ExitPlanMode 请求携带的完整计划 Markdown 原文，供下一步的 Markdown 渲染 UI 使用。
    /// 无 ToolInput / 解析失败 → 空字符串（不影响卡片其余部分照常渲染）。
    /// </summary>
    public string PlanMarkdown
    {
        get
        {
            var input = _session?.PermissionRequest?.ToolInput;
            if (input == null || input.Count == 0) return "";
            return ExtractPlanMarkdown(input);
        }
    }

    /// <summary>
    /// 这条会话当前是不是 Edit/MultiEdit/Write 的代码改动审阅（而非普通权限 / 问题 / 计划）。
    /// 为 true 时 XAML 应显示完整的 diff 渲染，Allow once/Always/Deny 按钮组照常复用不变——
    /// 这个功能只换"详情怎么展示"，批准通路跟普通工具权限完全同一套。
    /// </summary>
    public bool IsCodeReview
    {
        get
        {
            var tool = _session?.PermissionRequest?.ToolName;
            return string.Equals(tool, "Edit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool, "MultiEdit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool, "Write", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 由 <see cref="Services.DiffBuilder"/> 从 tool_input 构建的结构化 diff。IsCodeReview=false
    /// 或构建失败（文件读不到/解析失败等）时为 null——XAML 据此可以退回显示原始 PermissionFullDetail
    /// 文本块，代码审阅卡片不会因为一次解析失败就整体空白。
    /// </summary>
    // CodeReviewDiff 的缓存：按 PermissionRequest 实例缓存构建结果。XAML 里有 6 处绑定
    // （HasCodeReviewDiff、CodeReviewHeaderText、CodeReviewDiff.Hunks、CodeReviewTruncatedText…）
    // 每次刷新都会各读一次这个 getter，而 IslandSessionItem 每次 SessionsChanged 都
    // OnPropertyChanged("")（transcript 每次写入 + 2 秒定时器持续触发）。不缓存的话：
    //   (1) 每次刷新对同一份改动跑 6 遍 DiffBuilder.Build（6 次读盘 + 6 次 LCS），大文件时
    //       是 UI 线程上的持续 GC 压力与掉帧；
    //   (2) 6 次 Build 各自独立读盘，若文件在同一渲染趟里被外部改写（编辑器保存/终端先批准
    //       后 Claude 落盘），头部 "+N −M" 与实际渲染的 hunks 可能来自不同快照，出现自相矛盾
    //       的卡片。
    // 按 PermissionRequest 引用缓存 → 同一个请求只构建一次、一个快照；请求变了（新的待批准）
    // 引用就变，自动失效重建。
    private PermissionRequest? _codeReviewDiffKey;
    private Services.CodeReviewDiff? _codeReviewDiffCache;

    public Services.CodeReviewDiff? CodeReviewDiff
    {
        get
        {
            if (!IsCodeReview) return null;
            var req = _session?.PermissionRequest;
            if (req?.ToolInput == null) return null;
            if (!ReferenceEquals(req, _codeReviewDiffKey))
            {
                _codeReviewDiffKey = req;
                _codeReviewDiffCache = Services.DiffBuilder.Build(req.ToolName, req.ToolInput);
            }
            return _codeReviewDiffCache;
        }
    }

    /// <summary>
    /// IsCodeReview=true 且 diff 真的构建成功时才为 true。XAML 的两个互斥详情块（原始文本 /
    /// diff 渲染）都应该绑这个，而不是绑 IsCodeReview 本身——否则 tool_input 畸形、文件读不到
    /// 等导致 DiffBuilder.Build 返回 null 时，两块都会因为 IsCodeReview 仍是 true 而判定
    /// "该显示 diff"，但 diff 是空的，原始文本块又被同一个 IsCodeReview 挡住不显示，用户看到
    /// 的就是一个没有任何信息的空卡片。
    /// </summary>
    public bool HasCodeReviewDiff => IsCodeReview && CodeReviewDiff != null;

    /// <summary>diff 卡片头部："path  +12 −5"，新建文件时前面加"（新建）"。</summary>
    public string CodeReviewHeaderText
    {
        get
        {
            var d = CodeReviewDiff;
            if (d == null) return "";
            var newFileTag = d.IsNewFile ? "（新建） " : "";
            return $"{newFileTag}{d.FilePath}  +{d.Additions} −{d.Deletions}";
        }
    }

    /// <summary>截断提示，如"…还有约 37 行改动未显示"；没有截断时为空串（XAML 按空串折叠）。</summary>
    public string CodeReviewTruncatedText
    {
        get
        {
            var d = CodeReviewDiff;
            return d is { TruncatedLineCount: > 0 } ? $"…还有约 {d.TruncatedLineCount} 行改动未显示" : "";
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

    /// <summary>VM 接线"同步到网页"回调（卡片创建后调用）。</summary>
    public void SetSyncWeb(Action<string>? cb) => _onSyncWeb = cb;

    /// <summary>状态圆点按钮：把本会话置顶同步到网页（网页同步开启时圆点才可点；再点取消）。</summary>
    [RelayCommand]
    private void SyncWeb() => _onSyncWeb?.Invoke(Id);

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
        "session-nav-failed" => Loc.Get("Reply_SessionNavFailed"),
        "clipboard-failed" => Loc.Get("Reply_ClipboardFailed"),
        _ => Loc.Get("Reply_SendFailed")
    };


    [RelayCommand]
    private async Task JumpAsync()
    {
        OpenIsland.App.Services.TerminalJumpService.JumpDiag(
            $"[CLICK] JumpAsync fired: id={Id} title='{Title}' hasSession={_session != null} hasRunningInfo={_runningInfo != null} entrypoint={_session?.ClaudeMetadata?.Entrypoint ?? "(null)"}");
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
        OpenIsland.App.Services.TerminalJumpService.JumpDiag("[CLICK] JumpAsync: no session & no runningInfo cwd -> did nothing");
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

    // ── ExitPlanMode 计划审阅：选项按钮（按宿主动态生成）+ 反馈输入框 ──
    [ObservableProperty] private string _planFeedbackText = "";

    /// <summary>
    /// 计划审阅的选项列表 —— 按 entrypoint 镜像宿主真实呈现的选项（用户明确要求"宿主呈现
    /// 什么选项，岛上就给什么选项"，跟权限按钮/AskUserQuestion 同一条原则）：
    ///   · CLI 终端：Claude Code 的编号菜单 "1. Yes, and auto-accept edits / 2. Yes, and
    ///     manually approve edits / 3. No, keep planning"，Key = 注入的数字。
    ///   · Claude Desktop：Electron 弹窗的真实按钮（用户实测截图确认的文案）：
    ///     Accept / Accept and auto mode / Reject / Revise…，Key = UIA 匹配用的语义键
    ///     （见 TerminalJumpService.ClickPlanButtonInClaudeDesktop 的匹配表）。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<PlanOption> PlanOptions
        => IsDesktopSession
            ? new[]
            {
                new PlanOption("accept", "Accept"),
                new PlanOption("accept-auto", "Accept and auto mode"),
                new PlanOption("reject", "Reject"),
                new PlanOption("revise", "Revise…"),
            }
            : new[]
            {
                new PlanOption("1", "1. Yes, and auto-accept edits"),
                new PlanOption("2", "2. Yes, and manually approve edits"),
                new PlanOption("3", "3. No, keep planning"),
            };

    /// <summary>计划审阅选项按钮：Key 直接透传给 SessionManager（CLI = 注入的数字，
    /// desktop = UIA 按钮语义键），分流逻辑全在 RespondToPlanAsync 里。</summary>
    [RelayCommand]
    private async Task RespondPlanAsync(object? optionKey)
    {
        if (_sessionManager == null || _session == null) return;
        var key = optionKey?.ToString();
        if (string.IsNullOrEmpty(key)) return;
        await _sessionManager.RespondToPlanAsync(_session.Id, key);
    }

    /// <summary>
    /// 发送计划反馈：先在终端选 3 退出计划选项菜单，再把 PlanFeedbackText 当一条正常消息
    /// 粘贴发出去（全部逻辑在 SessionManager.RespondToPlanFeedbackAsync 里）。成功后清空
    /// 输入框；失败保留原文，让用户能重试而不用重新打字。
    /// </summary>
    [RelayCommand]
    private async Task SendPlanFeedbackAsync()
    {
        if (_sessionManager == null || _session == null) return;
        var text = PlanFeedbackText?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        var ok = await _sessionManager.RespondToPlanFeedbackAsync(_session.Id, text);
        if (ok) PlanFeedbackText = "";
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

/// <summary>
/// ExitPlanMode 计划审阅的单个选项按钮。Key 是行为语义键：CLI = 要注入终端的数字
/// （"1"/"2"/"3"），Claude Desktop = UIA 按钮匹配键（"accept"/"accept-auto"/"reject"/"revise"）。
/// Label 是按钮显示文案，严格镜像宿主真实呈现的文字。
/// </summary>
public sealed record PlanOption(string Key, string Label);
