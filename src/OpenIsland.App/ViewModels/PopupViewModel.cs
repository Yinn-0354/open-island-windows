using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIsland.App.Services;
using OpenIsland.Core.Models;

namespace OpenIsland.App.ViewModels;

/// <summary>
/// 托盘弹窗 ViewModel
/// </summary>
public partial class PopupViewModel : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly PopupWindowService _popupService;

    [ObservableProperty]
    private ObservableCollection<PopupSessionItem> _sessions = new();

    [ObservableProperty]
    private bool _hasRunningSessions;

    [ObservableProperty]
    private bool _hasAttentionSessions;

    [ObservableProperty]
    private int _attentionCount;

    public PopupViewModel(SessionManager sessionManager, PopupWindowService popupService)
    {
        _sessionManager = sessionManager;
        _popupService = popupService;

        _sessionManager.SessionsChanged += OnSessionsChanged;
        RefreshSessions();
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshSessions);
    }

    private void RefreshSessions()
    {
        var allSessions = _sessionManager.GetAllSessions()
            .Where(s => s.Phase != SessionPhase.Completed || s.AttachmentState == SessionAttachmentState.Attached)
            .OrderBy(s => s.Phase == SessionPhase.Completed ? 1 : 0)
            .ThenByDescending(s => s.UpdatedAt)
            .Take(10)
            .ToList();

        Sessions.Clear();
        foreach (var session in allSessions)
        {
            Sessions.Add(new PopupSessionItem(session, _sessionManager, _popupService));
        }

        HasRunningSessions = Sessions.Any(s => s.Phase != SessionPhase.Completed);
        HasAttentionSessions = Sessions.Any(s => s.NeedsAttention);
        AttentionCount = Sessions.Count(s => s.NeedsAttention);
    }

    [RelayCommand]
    private void OpenControlCenter()
    {
        _popupService.OpenControlCenter();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _popupService.OpenSettings();
    }

    [RelayCommand]
    private void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }
}

/// <summary>
/// 弹窗中的会话项
/// </summary>
public partial class PopupSessionItem : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly PopupWindowService _popupService;
    private readonly AgentSession _session;

    public string Id => _session.Id;
    public string Title => _session.Title;
    public string Summary => _session.Summary;
    public AgentTool Tool => _session.Tool;
    public SessionPhase Phase => _session.Phase;
    public bool NeedsAttention => _session.NeedsAttention;

    public string ToolIcon => GetToolIcon(_session.Tool);
    public string StatusIndicator => GetStatusIndicator(_session.Phase, _session.NeedsAttention);
    public bool ShowApproveButton => _session.Phase == SessionPhase.WaitingForApproval;
    public bool ShowAnswerButton => _session.Phase == SessionPhase.WaitingForAnswer;

    public PopupSessionItem(AgentSession session, SessionManager sessionManager, PopupWindowService popupService)
    {
        _session = session;
        _sessionManager = sessionManager;
        _popupService = popupService;
    }

    [RelayCommand]
    private async Task ClickAsync()
    {
        if (_session.NeedsAttention)
        {
            // 如果需要注意，打开控制中心
            _popupService.OpenControlCenter(_session.Id);
        }
        else
        {
            // 否则跳转到终端
            await _sessionManager.JumpToSessionAsync(_session.Id);
        }
        _popupService.ClosePopup();
    }

    [RelayCommand]
    private void Approve()
    {
        _sessionManager.ResolvePermission(_session.Id, approved: true);
    }

    [RelayCommand]
    private void Deny()
    {
        _sessionManager.ResolvePermission(_session.Id, approved: false);
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

    private static string GetStatusIndicator(SessionPhase phase, bool needsAttention)
    {
        if (needsAttention) return "🔴";
        return phase switch
        {
            // 思考中 = 蓝；一轮完毕等输入 = 绿
            // Thinking = blue; turn ended, awaiting input = green
            SessionPhase.Running => "🔵",
            SessionPhase.Idle => "🟢",
            SessionPhase.Completed => "⚪",
            _ => "⚪"
        };
    }
}
