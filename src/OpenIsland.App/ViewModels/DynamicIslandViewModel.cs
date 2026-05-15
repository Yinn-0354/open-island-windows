using System.Collections.ObjectModel;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIsland.App.Services;
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

public partial class DynamicIslandViewModel : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly PopupWindowService _popupService;
    private readonly System.Timers.Timer _greenStatusTimer;
    private bool _justCompletedTask = false;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<IslandSessionItem> _sessions = new();
    [ObservableProperty] private int _runningCount;
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

    public DynamicIslandViewModel(SessionManager sessionManager, PopupWindowService popupService)
    {
        _sessionManager = sessionManager;
        _popupService = popupService;
        _sessionManager.SessionsChanged += OnSessionsChanged;
        _sessionManager.TaskCompleted += OnTaskCompleted;

        // 绿色状态计时器：3秒后恢复
        _greenStatusTimer = new System.Timers.Timer(3000);
        _greenStatusTimer.Elapsed += (_, _) =>
        {
            _justCompletedTask = false;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateStatusColor);
        };
        _greenStatusTimer.AutoReset = false;

        RefreshSessions();
    }

    private void OnTaskCompleted(object? sender, AgentSession session)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _justCompletedTask = true;
            UpdateStatusColor();
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
            return;
        }

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
        System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshSessions);
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
        var active = new List<IslandSessionItem>();

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

            // 2. 没 cwd 时退回到"最近被改写过的 .jsonl 对应的 scan 会话"——
            //    有真实首条用户消息标题，跟 cc-switch 一样
            session ??= sessionsByRecency.FirstOrDefault(s => !assignedIds.Contains(s.Id));

            if (session != null)
            {
                assignedIds.Add(session.Id);
                active.Add(new IslandSessionItem(session, _sessionManager));
            }
            else
            {
                // 进程刚起来、scan 还没扫到 ——退到 RunningSessionInfo 显示
                active.Add(new IslandSessionItem(r, _sessionManager));
            }
        }

        Sessions.Clear();
        // 不再硬限 6 条 —— XAML 里的 ScrollViewer 用 MaxHeight 控制可视高度，剩下的滚动可见。
        // No more hard cap at 6 — the XAML ScrollViewer caps visible height; overflow scrolls.
        foreach (var item in active.Take(20))
            Sessions.Add(item);

        RunningCount = runningProcesses.Count;
        AttentionCount = _sessionManager.GetAttentionCount();
        HasAttention = AttentionCount > 0;
        HasAnySessions = Sessions.Count > 0;

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
}

public partial class IslandSessionItem : ObservableObject
{
    private readonly AgentSession? _session;
    private readonly RunningSessionInfo? _runningInfo;
    private readonly SessionManager? _sessionManager;

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
    /// 第二按钮文案。SuggestedAlwaysAllow 推到时用 "Yes, don't ask again for {scope}"，
    /// 推不到（hook payload 缺 tool_name 等极端情况）兜底成纯 "2. Yes, don't ask again"，
    /// 不让按钮因为没规则就消失 —— 用户体验上 1/2/3 三键应该恒定可见。
    /// </summary>
    public string AlwaysButtonLabel
        => _session?.PermissionRequest?.SuggestedAlwaysAllow?.ToButtonLabel()
           ?? "2. Yes, don't ask again";

    /// <summary>
    /// 第二按钮的显隐：只跟 phase 走。原来还查 SuggestedAlwaysAllow != null，
    /// 但那条规则可空（见 BuildAllowRule 的 toolName 为空分支），结果 ToolSearch 这种
    /// hook 偶尔没拿到 tool_name 就少一个按钮。
    /// </summary>
    public bool ShowAlwaysButton
        => _session?.Phase == SessionPhase.WaitingForApproval;

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
    /// AskUserQuestion 的 tool_input 通常长这样（Claude Code 实际格式）：
    /// {"questions":[{"question":"...","header":"...","options":[{"label":"...","description":"..."},...],"multiSelect":false}]}
    /// 走 JsonElement 解析比 Dictionary&lt;string,object&gt; 强壮（嵌套数组/对象）。
    /// </summary>
    private static string FormatAskUserQuestion(System.Collections.Generic.IDictionary<string, object> toolInput)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(toolInput);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("questions", out var qs)
                || qs.ValueKind != System.Text.Json.JsonValueKind.Array
                || qs.GetArrayLength() == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.TryGetProperty("question", out var question))
                {
                    sb.Append("Q: ").AppendLine(question.GetString() ?? "");
                }
                if (q.TryGetProperty("options", out var opts) && opts.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int i = 1;
                    foreach (var opt in opts.EnumerateArray())
                    {
                        var label = opt.TryGetProperty("label", out var l) ? l.GetString() : "";
                        var desc = opt.TryGetProperty("description", out var d) ? d.GetString() : "";
                        sb.Append(i++).Append(". ").AppendLine(label ?? "");
                        if (!string.IsNullOrEmpty(desc))
                            sb.Append("   ").AppendLine(desc);
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return "";
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

    public IslandSessionItem(AgentSession session, SessionManager sessionManager)
    {
        _session = session;
        _sessionManager = sessionManager;
    }

    public IslandSessionItem(RunningSessionInfo runningInfo, SessionManager sessionManager)
    {
        _runningInfo = runningInfo;
        _sessionManager = sessionManager;
    }

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
    /// 1./2./3. 按钮：把数字 + Enter 物理注入进 claude.exe 所在终端窗口。等同于用户
    /// 直接在 Claude 终端键入数字 —— 终端 prompt 解析、tool 跑（或拒），同时本地清岛卡。
    /// </summary>
    [RelayCommand]
    private async Task RespondYesAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondInTerminalAsync(_session.Id, '1');
    }

    [RelayCommand]
    private async Task RespondAlwaysAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondInTerminalAsync(_session.Id, '2');
    }

    [RelayCommand]
    private async Task RespondNoAsync()
    {
        if (_sessionManager != null && _session != null)
            await _sessionManager.RespondInTerminalAsync(_session.Id, '3');
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
