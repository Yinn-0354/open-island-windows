using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIsland.App.Services;
using OpenIsland.Core.Models;

namespace OpenIsland.App.ViewModels;

/// <summary>
/// 主窗口/控制中心 ViewModel
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly WorkspaceSettings _workspaceSettings;

    [ObservableProperty]
    private ObservableCollection<AgentSessionViewModel> _sessions = new();

    [ObservableProperty]
    private AgentSessionViewModel? _selectedSession;

    [ObservableProperty]
    private bool _isBridgeConnected;

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _attentionCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>"sessions" / "overview" / "models" 三选一。控制顶部 Tab 显示哪页。</summary>
    [ObservableProperty]
    private string _activeTab = "sessions";

    /// <summary>"all" / "30d" / "7d" 时间窗口筛选。仅 Overview/Models 用。</summary>
    [ObservableProperty]
    private string _statsRange = "all";

    [ObservableProperty]
    private DashboardStats _dashboardStats = new();

    public bool IsTabSessions => ActiveTab == "sessions";
    public bool IsTabOverview => ActiveTab == "overview";
    public bool IsTabModels => ActiveTab == "models";
    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsTabSessions));
        OnPropertyChanged(nameof(IsTabOverview));
        OnPropertyChanged(nameof(IsTabModels));
    }

    public bool IsRangeAll => StatsRange == "all";
    public bool IsRange30d => StatsRange == "30d";
    public bool IsRange7d => StatsRange == "7d";
    partial void OnStatsRangeChanged(string value)
    {
        OnPropertyChanged(nameof(IsRangeAll));
        OnPropertyChanged(nameof(IsRange30d));
        OnPropertyChanged(nameof(IsRange7d));
        RefreshStats();
    }

    [RelayCommand]
    private void SelectTab(string tab) => ActiveTab = tab;

    [RelayCommand]
    private void SelectRange(string range) => StatsRange = range;

    public MainViewModel(SessionManager sessionManager, WorkspaceSettings workspaceSettings)
    {
        _sessionManager = sessionManager;
        _workspaceSettings = workspaceSettings;
        _sessionManager.SessionsChanged += OnSessionsChanged;
        _sessionManager.BridgeStatusChanged += OnBridgeStatusChanged;
        _workspaceSettings.Changed += (_, _) =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshSessions);

        RefreshSessions();
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshSessions);
    }

    private void OnBridgeStatusChanged(object? sender, bool connected)
    {
        IsBridgeConnected = connected;
    }

    private void RefreshSessions()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _sessionManager.GetAllSessions()
            : _sessionManager.GetAllSessions().Where(s =>
                s.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                s.Summary.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        Sessions.Clear();
        foreach (var session in filtered.OrderByDescending(s => s.UpdatedAt))
        {
            Sessions.Add(new AgentSessionViewModel(session, _sessionManager));
        }

        RunningCount = _sessionManager.GetRunningCount();
        AttentionCount = _sessionManager.GetAttentionCount();
        RefreshStats();
    }

    private void RefreshStats()
    {
        TimeSpan? window = StatsRange switch
        {
            "30d" => TimeSpan.FromDays(30),
            "7d" => TimeSpan.FromDays(7),
            _ => null
        };

        // 工作区筛选：只统计 cwd 落在用户选定目录下的 session（工作区为空时不过滤）
        var sessions = _sessionManager.GetAllSessions()
            .Where(s => _workspaceSettings.Matches(s.JumpTarget?.WorkingDirectory));
        DashboardStats = DashboardStats.Compute(sessions, window);
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshSessions();
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshSessions();
    }

    public void RefreshNow() => RefreshSessions();

    [RelayCommand]
    private void ClearCompleted()
    {
        _sessionManager.ClearCompletedSessions();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // 通过 App 主作用域 ServiceProvider 拿 transient SettingsWindow（已注入 WorkspaceSettings）
        var sp = ((App)System.Windows.Application.Current).ServiceProvider;
        var dlg = sp?.GetService(typeof(Views.SettingsWindow)) as Views.SettingsWindow;
        if (dlg == null) return;
        dlg.Owner = System.Windows.Application.Current.Windows.OfType<Views.MainWindow>().FirstOrDefault();
        dlg.ShowDialog();
        // SetWorkspaces 已经触发 Changed → RefreshSessions 自动跑
    }

    [RelayCommand]
    private void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }
}

/// <summary>
/// 单个会话的ViewModel
/// </summary>
public partial class AgentSessionViewModel : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly AgentSession _session;

    public string Id => _session.Id;
    public string Title => _session.Title;
    public AgentTool Tool => _session.Tool;
    public SessionPhase Phase => _session.Phase;
    public string Summary => _session.Summary;
    public DateTime UpdatedAt => _session.UpdatedAt;

    public bool NeedsAttention => _session.NeedsAttention;
    public bool HasPermissionRequest => _session.PermissionRequest != null;
    public bool HasQuestion => _session.QuestionPrompt != null;
    public bool ShowApproveButton => HasPermissionRequest;

    public string ToolIcon => GetToolIcon(_session.Tool);
    public string PhaseText => GetPhaseText(_session.Phase);
    public string StatusColor => GetStatusColor(_session.Phase);

    // Token用量和费用信息（仅Claude Code）
    public bool HasTokenInfo => _session.Tool == AgentTool.ClaudeCode && _session.ClaudeMetadata != null && _session.ClaudeMetadata.TotalTokens > 0;
    public string TokenInfo => _session.ClaudeMetadata != null
        ? $"Tokens: {_session.ClaudeMetadata.TotalTokens:N0} (输入: {_session.ClaudeMetadata.InputTokens:N0}, 输出: {_session.ClaudeMetadata.OutputTokens:N0}, 缓存: {_session.ClaudeMetadata.CacheReadTokens + _session.ClaudeMetadata.CacheCreationTokens:N0})"
        : "";
    public string CostInfo => _session.ClaudeMetadata?.TotalCost > 0 ? $"Cost: ${_session.ClaudeMetadata.TotalCost:F4}" : "";

    public AgentSessionViewModel(AgentSession session, SessionManager sessionManager)
    {
        _session = session;
        _sessionManager = sessionManager;
    }

    [RelayCommand]
    private void ApprovePermission()
    {
        if (_session.PermissionRequest != null)
        {
            _sessionManager.ResolvePermission(_session.Id, approved: true);
        }
    }

    [RelayCommand]
    private void DenyPermission()
    {
        if (_session.PermissionRequest != null)
        {
            _sessionManager.ResolvePermission(_session.Id, approved: false);
        }
    }

    [RelayCommand]
    private void AnswerQuestion()
    {
        // 显示回答对话框
    }

    [RelayCommand]
    private async Task JumpToTerminalAsync()
    {
        await _sessionManager.JumpToSessionAsync(_session.Id);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _sessionManager.DismissSession(_session.Id);
    }

    private static string GetToolIcon(AgentTool tool)
    {
        return tool switch
        {
            AgentTool.ClaudeCode => "🟣",
            AgentTool.Codex or AgentTool.CodexApp => "⚫",
            AgentTool.Cursor => "⚪",
            AgentTool.GeminiCLI => "🔵",
            AgentTool.KimiCLI => "🟡",
            AgentTool.OpenCode => "🟢",
            _ => "🤖"
        };
    }

    private static string GetPhaseText(SessionPhase phase)
    {
        return phase switch
        {
            SessionPhase.Running => "运行中",
            SessionPhase.WaitingForApproval => "等待审批",
            SessionPhase.WaitingForAnswer => "等待回答",
            SessionPhase.Completed => "已完成",
            SessionPhase.Idle => "空闲",
            _ => "未知"
        };
    }

    private static string GetStatusColor(SessionPhase phase)
    {
        return phase switch
        {
            // Claude 在思考/工作中 = 蓝；一轮回复完毕等用户输入 = 绿
            // Claude is thinking/working = blue; turn ended, awaiting user = green
            SessionPhase.Running => "#2196F3",
            SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer => "#FF9800",
            SessionPhase.Completed => "#9E9E9E",
            SessionPhase.Idle => "#4CAF50",
            _ => "#757575"
        };
    }
}
