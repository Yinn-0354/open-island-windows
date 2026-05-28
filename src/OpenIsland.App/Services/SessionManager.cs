using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using OpenIsland.Core;
using OpenIsland.Core.Bridge;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;
using OpenIsland.Core.Registry;

namespace OpenIsland.App.Services;

/// <summary>
/// 权限模式切换的目标种类（岛上卡片三个图标按钮各对应一个）。
/// 注意：Claude Code 实际只支持 Shift+Tab *循环*，没有"直接设为某模式"的按键，
/// 所以这个枚举目前仅用于日志/语义标注，三种都触发同一个"发一次 Shift+Tab"动作。
/// Best-effort：精确落到指定模式不保证（见 SendModeSwitchAsync 注释）。
/// </summary>
public enum ModeKind
{
    AcceptEdits,
    Auto,
    Plan
}

/// <summary>
/// 会话管理服务 - 桥接核心库与UI
/// </summary>
public class SessionManager : IDisposable
{
    private readonly BridgeServer _bridgeServer;
    private readonly SessionRegistry _registry;
    private readonly TerminalJumpService _terminalJumpService;
    private readonly ClaudeTranscriptDiscovery _claudeDiscovery;
    private readonly ClaudeTranscriptWatcher _claudeWatcher;
    private readonly ProcessMonitorService _processMonitor;
    private SessionState _state = new();
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastHeartbeat = new();
    /// <summary>
    /// 等待用户审批的 hook 子进程 client ID 表（sessionId → BridgeServer client ID）。
    /// hook 子进程发 PreToolUse 后阻塞读 pipe 等 directive；ResolvePermission 时按 sessionId
    /// 找到 client，经 BridgeServer.SendToClientAsync 回包 HookDirectiveMessage。
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pendingHookClients = new();
    private readonly Timer _cleanupTimer;
    // 安全网：FileSystemWatcher 内部缓冲在深目录树 + 大量 I/O 下会溢出（OnError），
    // 丢失的事件由低频补扫覆盖。30s 间隔足够低，不会和 watcher 抢 I/O。
    private readonly Timer _safetyScanTimer;
    private readonly Timer _processSyncTimer;
    private int _isScanning; // 0 = idle, 1 = scanning

    /// <summary>
    /// 任务完成防抖：session 进 Idle 后挂个 3s 定时器，期间再翻 Running 就取消，
    /// 防止 Claude 多步响应中途瞬间 end_turn 把 watcher 骗成"完成"，导致 beep / 绿灯
    /// 提早响。3s = "稳定 idle"门槛，比单次 API 中段间隔（≤1s）大但比真完成停顿小。
    /// </summary>
    private readonly Dictionary<string, System.Timers.Timer> _completionDebounce = new();
    private readonly object _completionDebounceLock = new();
    private const int CompletionDebounceMs = 3000;

    public event EventHandler? SessionsChanged;
    public event EventHandler<bool>? BridgeStatusChanged;
    public event EventHandler<AgentSession>? TaskCompleted;
    public event EventHandler<AgentSession>? AttentionRequired;

    public SessionManager(BridgeServer bridgeServer, SessionRegistry registry, TerminalJumpService terminalJumpService,
        ProcessMonitorService processMonitor, ClaudeTranscriptDiscovery claudeDiscovery, ClaudeTranscriptWatcher claudeWatcher)
    {
        _bridgeServer = bridgeServer;
        _registry = registry;
        _terminalJumpService = terminalJumpService;
        _processMonitor = processMonitor;
        _claudeWatcher = claudeWatcher;
        // 与 ClaudeTranscriptWatcher 共享同一个 discovery 实例，复用其增量解析缓存
        _claudeDiscovery = claudeDiscovery;

        _bridgeServer.MessageReceived += OnBridgeMessageReceived;
        _bridgeServer.ClientConnected += OnClientConnected;
        _bridgeServer.ClientDisconnected += OnClientDisconnected;
        _processMonitor.RunningSessionsChanged += OnRunningSessionsChanged;

        // Claude 会话现在由 watcher（FileSystemWatcher）驱动，事件直接送进 DispatchEventAsync
        _claudeWatcher.EventEmitted += OnClaudeWatcherEvent;

        // 每分钟清理一次过期会话
        _cleanupTimer = new Timer(_ => CleanupStaleSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        // 30 秒一次的低频补扫，对抗 FileSystemWatcher 缓冲溢出（深目录大量 I/O）丢失的事件
        _safetyScanTimer = new Timer(_ => _ = ScanClaudeSessionsAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // 每2秒同步进程状态到会话状态
        _processSyncTimer = new Timer(_ => SyncProcessStatus(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // 加载持久化会话
        _ = LoadPersistedSessionsAsync();
    }

    private void OnClaudeWatcherEvent(object? sender, AgentEvent e)
    {
        _ = DispatchEventAsync(e);
    }

    /// <summary>
    /// 进程状态变化时更新
    /// </summary>
    private void OnRunningSessionsChanged(object? sender, EventArgs e)
    {
        NotifySessionsChanged();
    }

    /// <summary>
    /// 同步进程状态到会话状态
    /// </summary>
    private void SyncProcessStatus()
    {
        try
        {
            lock (_stateLock)
            {
                var runningProcesses = _processMonitor.GetRunningSessions();
                var updated = false;

                // 更新所有会话的 IsProcessAlive 状态
                foreach (var session in _state.Sessions)
                {
                    if (session.Tool != AgentTool.ClaudeCode) continue;

                    // 检查会话对应的进程是否在运行
                    var isRunning = IsSessionProcessRunning(session, runningProcesses);

                    if (session.IsProcessAlive != isRunning)
                    {
                        // 更新会话状态
                        var updatedSession = session with { IsProcessAlive = isRunning };
                        if (isRunning)
                        {
                            updatedSession = updatedSession with
                            {
                                Phase = SessionPhase.Running,
                                UpdatedAt = DateTime.UtcNow
                            };
                        }

                        // 通过事件更新会话状态
                        var sessions = _state.Sessions.ToList();
                        var index = sessions.FindIndex(s => s.Id == updatedSession.Id);
                        if (index >= 0)
                        {
                            sessions[index] = updatedSession;
                        }
                        _state = new SessionState(sessions);
                        updated = true;
                    }
                }

                if (updated)
                {
                    NotifySessionsChanged();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncProcessStatus error: {ex.Message}");
        }
    }

    private bool IsSessionProcessRunning(AgentSession session, IReadOnlyCollection<RunningSessionInfo> runningProcesses)
    {
        // 通过 JumpTarget 的工作目录匹配（最可靠）
        if (!string.IsNullOrEmpty(session.JumpTarget?.WorkingDirectory))
        {
            var sessionDir = session.JumpTarget.WorkingDirectory;
            var projectName = Path.GetFileName(sessionDir);
            if (runningProcesses.Any(r =>
                r.WorkingDirectory?.EndsWith(projectName) == true ||
                r.ProjectName == projectName ||
                r.WorkingDirectory == sessionDir))
            {
                return true;
            }
        }

        // 通过 ClaudeMetadata 的 TranscriptPath 匹配
        if (!string.IsNullOrEmpty(session.ClaudeMetadata?.TranscriptPath))
        {
            var sessionDir = Path.GetDirectoryName(session.ClaudeMetadata.TranscriptPath);
            if (!string.IsNullOrEmpty(sessionDir))
            {
                var projectName = Path.GetFileName(sessionDir);
                if (runningProcesses.Any(r =>
                    r.WorkingDirectory?.EndsWith(projectName) == true ||
                    r.ProjectName == projectName))
                {
                    return true;
                }
            }
        }

        // 通过会话ID匹配
        if (runningProcesses.Any(r => r.SessionId == session.Id))
        {
            return true;
        }

        // 通过标题匹配（作为后备方案）
        if (!string.IsNullOrEmpty(session.Title))
        {
            var title = session.Title;
            if (runningProcesses.Any(r =>
                r.ProjectName?.Contains(title) == true ||
                title.Contains(r.ProjectName ?? "")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 扫描Claude Code会话文件
    /// </summary>
    private async Task ScanClaudeSessionsAsync()
    {
        // Prevent overlapping scans
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
            return;

        try
        {
            var sessions = await _claudeDiscovery.ScanSessionsAsync();
            var updatedSessions = new List<AgentSession>();

            lock (_stateLock)
            {
                foreach (var sessionInfo in sessions)
                {
                    // 转换为AgentSession
                    var agentSession = sessionInfo.ToAgentSession();

                    // 检查是否已存在（通过SourcePath匹配）
                    var existingSession = _state.Sessions
                        .FirstOrDefault(s => s.ClaudeMetadata?.TranscriptPath == sessionInfo.SourcePath);

                    if (existingSession != null)
                    {
                        // 更新现有会话的元数据
                        // 注意：ApplyClaudeMetadataUpdated 在 SessionState 里是整个 *替换*
                        // ClaudeMetadata 而不是合并，所以这里 *必须* 把所有需要保留的字段
                        // 都填齐 —— 漏一个就会被清成默认值，下游依赖该字段的逻辑全废。
                        // 历史 bug：之前漏了 Entrypoint，扫描 tick（5s 一次）触发后桌面端 session
                        // 的 "claude-desktop" 标记被清成 null，点会话卡片直接走错路径。
                        var metadata = new ClaudeMetadata
                        {
                            TranscriptPath = sessionInfo.SourcePath,
                            Model = sessionInfo.Model,
                            Entrypoint = sessionInfo.Entrypoint,
                            InputTokens = sessionInfo.Usage.InputTokens,
                            OutputTokens = sessionInfo.Usage.OutputTokens,
                            CacheReadTokens = sessionInfo.Usage.CacheReadTokens,
                            CacheCreationTokens = sessionInfo.Usage.CacheCreationTokens,
                            TotalCost = sessionInfo.TotalCost
                        };

                        _state = _state.Apply(new ClaudeSessionMetadataUpdated
                        {
                            SessionId = existingSession.Id,
                            ClaudeMetadata = metadata
                        });

                        // 同时更新摘要和标题——只有当扫描结果是"真正的"标题
                        // (customTitle 或首条用户消息) 才传 Title，否则保留现有标题
                        // 避免用项目名后备覆盖更有信息量的标题
                        _state = _state.Apply(new SessionActivityUpdated
                        {
                            SessionId = existingSession.Id,
                            Summary = $"{sessionInfo.Summary} | Tokens: {sessionInfo.Usage.TotalTokens:N0} | Cost: ${sessionInfo.TotalCost:F4}",
                            Phase = existingSession.Phase,
                            Title = sessionInfo.HasRealTitle ? sessionInfo.Title : null
                        });

                        // 之前缺失 JumpTarget 时（旧版本扫描创建的会话），用扫描发现的 cwd 补上
                        if (existingSession.JumpTarget == null)
                        {
                            var jumpTarget = sessionInfo.BuildJumpTarget();
                            if (jumpTarget != null)
                            {
                                _state = _state.Apply(new JumpTargetUpdated
                                {
                                    SessionId = existingSession.Id,
                                    JumpTarget = jumpTarget
                                });
                            }
                        }

                        // 持久化用最新状态（apply 之后），避免把扫描更新前的旧 title 写回磁盘
                        if (_state.SessionsById.TryGetValue(existingSession.Id, out var refreshed))
                            updatedSessions.Add(refreshed);
                        else
                            updatedSessions.Add(existingSession);
                    }
                    else
                    {
                        // 新建会话 —— Entrypoint 必填（cli / claude-desktop），
                        // 否则 JumpToSessionAsync 的路由分流走不到桌面端激活。
                        var metadata = new ClaudeMetadata
                        {
                            TranscriptPath = sessionInfo.SourcePath,
                            Model = sessionInfo.Model,
                            Entrypoint = sessionInfo.Entrypoint,
                            InputTokens = sessionInfo.Usage.InputTokens,
                            OutputTokens = sessionInfo.Usage.OutputTokens,
                            CacheReadTokens = sessionInfo.Usage.CacheReadTokens,
                            CacheCreationTokens = sessionInfo.Usage.CacheCreationTokens,
                            TotalCost = sessionInfo.TotalCost
                        };

                        _state = _state.Apply(new SessionStarted
                        {
                            SessionId = agentSession.Id,
                            Title = agentSession.Title,
                            Tool = AgentTool.ClaudeCode,
                            InitialPhase = agentSession.Phase,
                            Summary = $"{sessionInfo.Summary} | Tokens: {sessionInfo.Usage.TotalTokens:N0} | Cost: ${sessionInfo.TotalCost:F4}",
                            Timestamp = agentSession.UpdatedAt,
                            JumpTarget = sessionInfo.BuildJumpTarget(),
                            ClaudeMetadata = metadata
                        });
                        if (_state.SessionsById.TryGetValue(agentSession.Id, out var freshlyCreated))
                            updatedSessions.Add(freshlyCreated);
                        else
                            updatedSessions.Add(agentSession);
                    }
                }
            }

            // 持久化更新的会话
            foreach (var session in updatedSessions)
            {
                await _registry.UpsertAsync(session);
            }

            if (updatedSessions.Count > 0)
            {
                NotifySessionsChanged();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to scan Claude sessions: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isScanning, 0);
        }
    }

    private async Task LoadPersistedSessionsAsync()
    {
        var records = await _registry.LoadAsync();
        foreach (var record in records.Where(r => r.ShouldRestoreToLiveState))
        {
            var session = record.ToRestorableSession();
            _state = _state.Apply(new SessionStarted
            {
                SessionId = session.Id,
                Title = session.Title,
                Tool = session.Tool,
                Origin = session.Origin,
                InitialPhase = session.Phase,
                Summary = session.Summary,
                Timestamp = session.UpdatedAt
            });
        }
        NotifySessionsChanged();
    }

    private void OnBridgeMessageReceived(object? sender, BridgeMessageReceivedEventArgs e)
    {
        if (e.Message is ProcessHookMessage hookMessage)
        {
            _ = ProcessHookEventAsync(hookMessage.Source, hookMessage.EventData, e.ClientId);
        }
    }

    private void OnClientConnected(object? sender, ClientConnectedEventArgs e)
    {
        if (e.ClientType == "app")
        {
            BridgeStatusChanged?.Invoke(this, true);
        }
    }

    private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
    {
        BridgeStatusChanged?.Invoke(this, _bridgeServer.GetConnectedClients().Count > 0);
        // B 模式下 hook 是 fire-and-forget：发完事件就 disconnect，不能把 disconnect 当
        // "用户拒绝"信号 —— 否则岛上的橙卡会在出现的瞬间被这里清掉。橙卡靠 PostToolUse
        // (用户终端按 1/2 后) 或后续 phase 转换 (用户按 3 后 transcript watcher 看到 assistant
        // 继续说话) 来 resolve。
    }

    private async Task ProcessHookEventAsync(string source, System.Text.Json.JsonElement eventData, string? clientId = null)
    {
        AgentEvent? agentEvent = null;

        try
        {
            // Claude 一族（claude / qoder / qwen / factory / codebuddy / kimi）大部分事件已经
            // 改由 ClaudeTranscriptWatcher（监听 ~/.claude/projects/**/*.jsonl）驱动，**仅**
            // PreToolUse 这一种事件保留 hook 通道：transcript 是事后流，物理上做不到"事前拦截"，
            // 而权限审批必须同步阻塞 Claude 的 tool 调用直到用户决定。
            // 其它 hook 事件（Stop / SessionStart / UserPromptSubmit / PostToolUse 等）即便被
            // 老安装漏在 settings.json 里也会到这里，统一忽略不处理。
            switch (source.ToLowerInvariant())
            {
                case "claude":
                case "qoder":
                case "qwen":
                case "factory":
                case "codebuddy":
                case "kimi":
                    var claudePayload = System.Text.Json.JsonSerializer.Deserialize<ClaudeHookPayload>(eventData);
                    // 归一化 event name（删 underscore + lowercase）以兼容 Claude Code 实际的 PascalCase
                    var eventName = claudePayload?.HookEventName?.Replace("_", "").ToLowerInvariant();

                    // PostToolUse 是"终端镜像反馈"信号 —— 用户在 Claude 终端按 1/2 同意后 tool 跑完触发，
                    // 此时灵动岛上原 PreToolUse 拉起的橙色卡片仍在等用户点（hook 还没收到 directive）。
                    // 见到 PostToolUse 就把卡片当用户已同意 resolve 掉，UI 立刻清。
                    if (eventName == "posttooluse" && claudePayload != null
                        && !string.IsNullOrEmpty(claudePayload.SessionId))
                    {
                        bool isWaiting;
                        lock (_stateLock)
                        {
                            isWaiting = _state.SessionsById.TryGetValue(claudePayload.SessionId, out var s)
                                        && s.Phase == SessionPhase.WaitingForApproval;
                        }
                        if (isWaiting)
                        {
                            // 不持久化 always-allow 规则：用户在终端 1/2 中只按了 1 还是 2 我们不知道，
                            // 保守不写规则，用户想"一直允许"还得在岛上点 2 按钮。
                            ResolvePermission(claudePayload.SessionId, approved: true, alwaysAllowRule: null);
                        }
                        return; // PostToolUse 不再继续走下面的 PreToolUse 分支
                    }

                    // Stop hook：Claude Code 在每轮 assistant 真正 end_turn / stop_sequence
                    // 之后触发，是权威的"任务完成"信号。绕过 3s 防抖立即发 TaskCompleted ——
                    // 不靠 watcher 凭 stop_reason 推 Idle 那条路（中段纯文本 end_turn 会误判）。
                    if (eventName == "stop" && claudePayload != null
                        && !string.IsNullOrEmpty(claudePayload.SessionId))
                    {
                        // 同步 SessionState：phase=Idle、清 PermissionRequest（如果还挂着）
                        AgentSession? completed;
                        lock (_stateLock)
                        {
                            if (_state.SessionsById.TryGetValue(claudePayload.SessionId, out var s))
                            {
                                var newSession = s with
                                {
                                    Phase = SessionPhase.Idle,
                                    PendingPermissions = System.Collections.Immutable.ImmutableList<PermissionRequest>.Empty,
                                    Summary = "Ready for input",
                                    UpdatedAt = DateTime.UtcNow
                                };
                                _state = new SessionState(_state.SessionsById.Values
                                    .Where(x => x.Id != s.Id).Append(newSession));
                                completed = newSession;
                            }
                            else completed = null;
                        }
                        if (completed != null)
                        {
                            CancelCompletionFire(claudePayload.SessionId); // 取消 watcher 排队的"试探性" Idle
                            TaskCompleted?.Invoke(this, completed);
                            NotifySessionsChanged();
                        }
                        return;
                    }

                    // 只接受 PreToolUse / PermissionRequest；transcript watcher 负责其它一切
                    if (eventName == "pretooluse" || eventName == "permissionrequest")
                    {
                        agentEvent = claudePayload?.ToAgentEvent(source);

                        // PermissionRequested 在 Apply 时若 session 还不在 state 里会被静默丢弃，
                        // 而 SessionStart hook 已经从 settings.json 移除（只留 PreToolUse），所以
                        // 第一次 fetch 来得快于 5s scan timer 时 session 没注册 → UI 不弹。
                        // 这里按 hook payload 合成一个 SessionStarted 占位，transcript watcher
                        // 之后扫到会用 project 名等真信息覆盖标题。
                        if (agentEvent is PermissionRequested perm && claudePayload != null)
                        {
                            bool needsPlaceholder;
                            lock (_stateLock)
                            {
                                needsPlaceholder = !_state.SessionsById.ContainsKey(perm.SessionId);
                            }
                            if (needsPlaceholder)
                            {
                                var synthetic = new SessionStarted
                                {
                                    SessionId = perm.SessionId,
                                    Title = !string.IsNullOrEmpty(claudePayload.WorkingDirectory)
                                        ? System.IO.Path.GetFileName(claudePayload.WorkingDirectory.TrimEnd('\\', '/'))
                                        : "Claude Session",
                                    Tool = source.ToLowerInvariant() switch
                                    {
                                        "qoder" => AgentTool.Qoder,
                                        "qwen" => AgentTool.QwenCode,
                                        "factory" => AgentTool.Factory,
                                        "codebuddy" => AgentTool.CodeBuddy,
                                        "kimi" => AgentTool.KimiCLI,
                                        _ => AgentTool.ClaudeCode
                                    },
                                    Origin = SessionOrigin.Local,
                                    InitialPhase = SessionPhase.Running,
                                    Summary = "Session discovered via permission request",
                                    Timestamp = DateTime.UtcNow,
                                    JumpTarget = !string.IsNullOrEmpty(claudePayload.TerminalApp) || !string.IsNullOrEmpty(claudePayload.WorkingDirectory)
                                        ? new JumpTarget
                                        {
                                            TerminalApp = claudePayload.TerminalApp,
                                            TerminalSessionId = claudePayload.TerminalSessionId,
                                            WorkingDirectory = claudePayload.WorkingDirectory
                                        }
                                        : null,
                                    ClaudeMetadata = !string.IsNullOrEmpty(claudePayload.TranscriptPath) || !string.IsNullOrEmpty(claudePayload.Model)
                                        ? new ClaudeMetadata
                                        {
                                            TranscriptPath = claudePayload.TranscriptPath,
                                            Model = claudePayload.Model
                                        }
                                        : null
                                };
                                await DispatchEventAsync(synthetic);
                            }
                        }
                    }
                    break;

                case "codex":
                    var codexPayload = System.Text.Json.JsonSerializer.Deserialize<CodexHookPayload>(eventData);
                    agentEvent = codexPayload?.ToAgentEvent();
                    break;

                case "cursor":
                    var cursorPayload = System.Text.Json.JsonSerializer.Deserialize<CursorHookPayload>(eventData);
                    agentEvent = cursorPayload?.ToAgentEvent();
                    break;

                case "gemini":
                    var geminiPayload = System.Text.Json.JsonSerializer.Deserialize<GeminiHookPayload>(eventData);
                    agentEvent = geminiPayload?.ToAgentEvent();
                    break;
            }
        }
        catch (Exception ex)
        {
            // 解析失败，记录日志但不中断
            System.Diagnostics.Debug.WriteLine($"Failed to parse hook event: {ex.Message}");
        }

        if (agentEvent != null)
        {
            // B 模式下 hook 是 fire-and-forget，没有 client 在等 directive，所以这里不
            // 再往 _pendingHookClients 注册（注册了反而会被 OnClientDisconnected 误清）。
            // 字典本身保留 —— ResolvePermission 想发 directive 时找不到 entry 自然跳过。
            await DispatchEventAsync(agentEvent);
        }
    }

    private async Task DispatchEventAsync(AgentEvent @event)
    {
        AgentSession? sessionToPersist = null;
        AgentSession? completedSession = null;
        AgentSession? attentionSession = null;

        lock (_stateLock)
        {
            // 在 Apply 之前抓 prev phase，用于"仅 非Idle→Idle 转换才触发 TaskCompleted"。
            // Watcher 现在每次 transcript 变更都 emit 当前 phase，没有这个去重 beep 会响多次。
            // Capture prev phase before Apply so we only fire TaskCompleted on a genuine
            // non-Idle → Idle transition (watcher emits the phase on every transcript change).
            SessionPhase? prevPhase = null;
            if (@event is SessionActivityUpdated activityProbe
                && _state.SessionsById.TryGetValue(activityProbe.SessionId, out var prevSnap))
            {
                prevPhase = prevSnap.Phase;
            }

            _state = _state.Apply(@event);

            // 准备持久化
            if (@event is SessionStarted started)
            {
                if (_state.SessionsById.TryGetValue(started.SessionId, out var session))
                {
                    sessionToPersist = session;
                }
            }
            else if (@event is SessionCompleted completed)
            {
                if (_state.SessionsById.TryGetValue(completed.SessionId, out var session))
                {
                    sessionToPersist = session;
                    completedSession = session;
                }
            }
            else if (@event is SessionActivityUpdated updated)
            {
                if (_state.SessionsById.TryGetValue(updated.SessionId, out var session))
                {
                    // 进 Idle：挂防抖定时器（不立刻 fire TaskCompleted），3s 内若没翻 Running
                    // 才真发"完成"通知。Claude 多步响应中段偶尔会瞬间 end_turn 又立即续 Running，
                    // 没有防抖会被误判成"已完成" → 提早绿灯/beep。
                    if (updated.Phase == SessionPhase.Idle && prevPhase != SessionPhase.Idle)
                    {
                        ScheduleCompletionFire(updated.SessionId);
                    }
                    // 离开 Idle：取消挂起的"完成"通知
                    else if (updated.Phase != SessionPhase.Idle && prevPhase == SessionPhase.Idle)
                    {
                        CancelCompletionFire(updated.SessionId);
                    }

                    if (!string.IsNullOrWhiteSpace(updated.Title))
                        sessionToPersist = session;
                }
            }
            else if (@event is PermissionRequested perm)
            {
                // 权限请求/问题需要用户立即关注，单独触发提示音
                if (_state.SessionsById.TryGetValue(perm.SessionId, out var session))
                    attentionSession = session;
            }
            else if (@event is QuestionAsked asked)
            {
                if (_state.SessionsById.TryGetValue(asked.SessionId, out var session))
                    attentionSession = session;
            }

            // 更新心跳
            var sessionId = GetSessionIdFromEvent(@event);
            if (sessionId != null)
            {
                _lastHeartbeat[sessionId] = DateTime.UtcNow;
            }
        }

        // 在锁外执行异步持久化
        if (sessionToPersist != null)
        {
            await _registry.UpsertAsync(sessionToPersist);
        }

        // 触发任务完成通知
        if (completedSession != null)
        {
            TaskCompleted?.Invoke(this, completedSession);
        }

        // 触发"需关注"通知（独立于任务完成，避免污染 Dynamic Island 的"刚完成"绿灯逻辑）
        if (attentionSession != null)
        {
            AttentionRequired?.Invoke(this, attentionSession);
        }

        NotifySessionsChanged();
    }

    private string? GetSessionIdFromEvent(AgentEvent @event)
    {
        return @event switch
        {
            SessionStarted s => s.SessionId,
            SessionActivityUpdated s => s.SessionId,
            PermissionRequested s => s.SessionId,
            QuestionAsked s => s.SessionId,
            SessionCompleted s => s.SessionId,
            JumpTargetUpdated s => s.SessionId,
            SessionMetadataUpdated s => s.SessionId,
            ClaudeSessionMetadataUpdated s => s.SessionId,
            CursorSessionMetadataUpdated s => s.SessionId,
            GeminiSessionMetadataUpdated s => s.SessionId,
            OpenCodeSessionMetadataUpdated s => s.SessionId,
            ActionableStateResolved s => s.SessionId,
            _ => null
        };
    }

    public IReadOnlyCollection<AgentSession> GetAllSessions()
    {
        lock (_stateLock)
        {
            return _state.Sessions.ToList();
        }
    }

    public AgentSession? GetSession(string sessionId)
    {
        lock (_stateLock)
        {
            _state.SessionsById.TryGetValue(sessionId, out var session);
            return session;
        }
    }

    public int GetRunningCount()
    {
        // 返回真正运行中的进程数量
        return _processMonitor.GetRunningCount();
    }

    public int GetAttentionCount()
    {
        lock (_stateLock)
        {
            return _state.AttentionCount;
        }
    }

    public void ResolvePermission(string sessionId, bool approved)
        => ResolvePermission(sessionId, approved, null);

    /// <summary>
    /// 解决一个权限请求。alwaysAllowRule 非空时把规则持久化到 ~/.claude/settings.json
    /// 的 permissions.allow 列表 —— 下次 hook 子进程 fast-path 自己放行，不再弹 UI。
    /// </summary>
    public void ResolvePermission(string sessionId, bool approved, AllowRule? alwaysAllowRule)
    {
        lock (_stateLock)
        {
            _state = _state.ResolvePermission(sessionId, approved);
        }

        // "一直允许"：把规则写入 settings.json
        if (approved && alwaysAllowRule != null)
        {
            try
            {
                AppendAllowRuleToSettings(alwaysAllowRule);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppendAllowRuleToSettings failed: {ex.Message}");
            }
        }

        // 把 directive 经 BridgeServer 回包给等待中的 hook 子进程
        if (_pendingHookClients.TryRemove(sessionId, out var clientId))
        {
            var directive = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                permission_request = new { approve = approved }
            });
            _ = _bridgeServer.SendToClientAsync(clientId, new HookDirectiveMessage
            {
                SessionId = sessionId,
                Directive = directive
            });
        }

        NotifySessionsChanged();
    }

    /// <summary>
    /// 把"一直允许"规则 append 到 ~/.claude/settings.json 的 permissions.allow 数组。
    /// 不存在 permissions / allow 字段时创建。已存在同字符串规则时跳过，避免重复堆积。
    /// </summary>
    // 串行化对 ~/.claude/settings.json 的所有读-改-写：本方法（权限"一直允许"）与
    // WriteClaudeProviderEnv（模型 env）可能在不同线程并发（后者经 Task.Run），
    // 无锁会相互覆盖、甚至写到一半损坏文件。两个写入者共用这把锁。
    private static readonly object _claudeSettingsFileLock = new();

    private static void AppendAllowRuleToSettings(AllowRule rule)
    {
        lock (_claudeSettingsFileLock)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var settingsPath = Path.Combine(userProfile, ".claude", "settings.json");
            if (!File.Exists(settingsPath))
            {
                // 没 settings 也不报错——下次 Claude Code 启动时会自己生成
                return;
            }

            var text = File.ReadAllText(settingsPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return;

            if (node["permissions"] is not System.Text.Json.Nodes.JsonObject perm)
            {
                perm = new System.Text.Json.Nodes.JsonObject();
                node["permissions"] = perm;
            }
            if (perm["allow"] is not System.Text.Json.Nodes.JsonArray allowArr)
            {
                allowArr = new System.Text.Json.Nodes.JsonArray();
                perm["allow"] = allowArr;
            }

            var ruleStr = rule.ToSettingString();
            foreach (var existing in allowArr)
            {
                if (existing?.GetValue<string>() == ruleStr) return; // 已经在了
            }
            allowArr.Add(ruleStr);

            var json = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            // 原子写：tmp + replace，避免写到一半崩溃损坏文件（与 WriteClaudeProviderEnv 一致）。
            var tmp = settingsPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            if (File.Exists(settingsPath)) File.Replace(tmp, settingsPath, null);
            else File.Move(tmp, settingsPath);
        }
    }

    public void AnswerQuestion(string sessionId, string answer)
    {
        lock (_stateLock)
        {
            _state = _state.AnswerQuestion(sessionId, answer);
        }
        NotifySessionsChanged();
    }

    public void DismissSession(string sessionId)
    {
        lock (_stateLock)
        {
            _state = _state.DismissSession(sessionId);
        }
        _ = _registry.RemoveAsync(sessionId);
        NotifySessionsChanged();
    }

    public void ClearCompletedSessions()
    {
        lock (_stateLock)
        {
            _state = _state.RemoveInvisibleSessions();
        }
        NotifySessionsChanged();
    }

    /// <summary>
    /// 把岛上的 1/2/3 按钮点击转成 Claude 终端的真实按键 —— B 模式下 hook 已 fire-and-forget
    /// 退出，唯一能让 Claude 看到选择的办法就是把数字键 + Enter 物理注入进 claude.exe 所在终端
    /// 窗口。注入后立即 ResolvePermission(approved=true if digit∈{1,2}) 让岛上橙卡马上消失，
    /// 不等 PostToolUse 回。
    /// </summary>
    private void ScheduleCompletionFire(string sessionId)
    {
        lock (_completionDebounceLock)
        {
            if (_completionDebounce.TryGetValue(sessionId, out var existing))
            {
                existing.Stop();
                existing.Dispose();
            }
            var timer = new System.Timers.Timer(CompletionDebounceMs) { AutoReset = false };
            timer.Elapsed += (_, _) =>
            {
                lock (_completionDebounceLock)
                {
                    if (_completionDebounce.TryGetValue(sessionId, out var t) && ReferenceEquals(t, timer))
                    {
                        _completionDebounce.Remove(sessionId);
                    }
                    else
                    {
                        // 期间被替换或取消了 —— 不发通知
                        timer.Dispose();
                        return;
                    }
                }
                // 再确认 session 仍处于 Idle（防中间又 Running 的极端竞态）
                AgentSession? snapshot;
                lock (_stateLock)
                {
                    _state.SessionsById.TryGetValue(sessionId, out snapshot);
                }
                if (snapshot != null && snapshot.Phase == SessionPhase.Idle)
                {
                    TaskCompleted?.Invoke(this, snapshot);
                }
                timer.Dispose();
            };
            _completionDebounce[sessionId] = timer;
            timer.Start();
        }
    }

    private void CancelCompletionFire(string sessionId)
    {
        lock (_completionDebounceLock)
        {
            if (_completionDebounce.TryGetValue(sessionId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                _completionDebounce.Remove(sessionId);
            }
        }
    }

    /// <summary>
    /// 岛上 1/2/3 → 权限响应总入口。按 entrypoint 分流：
    ///   - cli / 缺失   → 注入终端按键（<see cref="RespondInTerminalAsync"/>）
    ///   - claude-desktop → UIA 点 Claude Desktop 权限弹窗按钮（Electron 没有终端可注入，
    ///     子 claude.exe 跑了 hook 把请求镜像到岛，但"允许/拒绝"在桌面端窗口里）
    /// </summary>
    public async Task<bool> RespondToPermissionAsync(string sessionId, char digit)
    {
        if (digit is not ('1' or '2' or '3')) return false;
        var session = GetSession(sessionId);
        if (session == null) return false;

        var entrypoint = session.ClaudeMetadata?.Entrypoint;
        if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
        {
            // UIA enumeration 必须挪线程池 —— WPF STA UI 线程上跑会阻塞消息泵导致 COM
            // 调用静默失败（与 JumpToSessionAsync 桌面分支同一个坑）。
            var clicked = await Task.Run(
                () => _terminalJumpService.RespondInClaudeDesktop(session.Title, digit));
            if (clicked)
            {
                // 点到按钮才清岛上橙卡；没点到就保留 —— 用户可重试或直接去 Claude Desktop 点。
                ResolvePermission(sessionId, approved: digit != '3', alwaysAllowRule: null);
            }
            return clicked;
        }

        // CLI / 缺失 entrypoint：原终端按键注入路径（保持不变）
        return await RespondInTerminalAsync(sessionId, digit);
    }

    public async Task<bool> RespondInTerminalAsync(string sessionId, char digit)
    {
        if (digit is not ('1' or '2' or '3')) return false;
        var session = GetSession(sessionId);
        if (session == null) return false;

        var claudePid = ResolveClaudeProcessId(session);
        if (claudePid is not int pid) return false;

        var sent = await _terminalJumpService.SendKeysToTerminalAsync(pid, $"{digit}\r");
        if (!sent) return false;

        // 立刻清岛上橙卡。1/2 视作 approved（"2" 不再持久化规则 —— 用户已经在 Claude 终端的
        // "Yes, don't ask again" 上按了，Claude Code 自己会写它的 settings；岛这边写一遍反而冲突）
        var approved = digit != '3';
        ResolvePermission(sessionId, approved, alwaysAllowRule: null);
        return true;
    }

    /// <summary>
    /// 岛上 AskUserQuestion 选项按钮（或 Skip）→ 真实回答总入口。按 entrypoint 分流，
    /// 镜像 <see cref="RespondToPermissionAsync"/> 的结构：
    ///   - claude-desktop → UIA 点 Claude Desktop AskUserQuestion 弹窗里对应的选项行
    ///     （+ 必要时点 Submit）；Skip → 点 Skip 按钮。
    ///   - cli / 缺失     → 复用终端注入：聚焦该会话终端发 "{optionNumber}\r"
    ///     （Claude Code 的 AskUserQuestion 是编号列表，输入数字 + Enter 即选中）；
    ///     Skip → best-effort 发 Esc（Claude Code 无专门"跳过键"，Esc 取消当前 prompt）。
    /// 成功后 ResolvePermission(approved:true) 清岛上卡片，与权限路径一致。
    /// </summary>
    public async Task<bool> AnswerQuestionAsync(string sessionId, int optionNumber, bool skip = false)
    {
        if (!skip && optionNumber < 1) return false;
        var session = GetSession(sessionId);
        if (session == null) return false;

        var entrypoint = session.ClaudeMetadata?.Entrypoint;
        if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
        {
            // 取选项 label 让 UIA 能按"名字含 label 或 末尾是编号"匹配选项行。
            var label = "";
            if (!skip)
            {
                var input = session.PermissionRequest?.ToolInput;
                if (input != null && input.Count > 0)
                {
                    label = ExtractQuestionOptionLabel(input, optionNumber);
                }
            }

            // UIA 必须挪线程池（与 RespondToPermissionAsync 桌面分支同一个坑：
            // WPF STA UI 线程上跑会阻塞消息泵导致 COM 静默失败）。
            var clicked = await Task.Run(() =>
                _terminalJumpService.AnswerQuestionInClaudeDesktop(
                    session.Title, optionNumber, label, skip));
            if (clicked)
            {
                // 点到了就清岛卡（视作已回答；Skip 也算"已处理"，卡片该消失）。
                ResolvePermission(sessionId, approved: true, alwaysAllowRule: null);
            }
            return clicked;
        }

        // CLI / 缺失 entrypoint：终端注入路径。
        var claudePid = ResolveClaudeProcessId(session);
        if (claudePid is not int pid) return false;

        // Skip：Claude Code 没有专门的"跳过 AskUserQuestion"按键。Esc 取消当前
        // 交互 prompt 是最接近的 best-effort（SendKeysToTerminalAsync 目前只认
        // 数字 / 回车，所以 Skip 这里发回车兜底——等价于"不选直接确认"，实测
        // Claude Code 多会落到第一个选项；真正的 Esc 注入是已知后续项，
        // 与桌面端 Skip 一样标注 best-effort）。
        var keys = skip ? "\r" : $"{optionNumber}\r";
        var sent = await _terminalJumpService.SendKeysToTerminalAsync(pid, keys);
        if (!sent) return false;

        // 立刻清岛上卡片（已回答）。不持久化任何规则 —— AskUserQuestion 不是权限。
        ResolvePermission(sessionId, approved: true, alwaysAllowRule: null);
        return true;
    }

    /// <summary>
    /// 从 AskUserQuestion 的 tool_input 取第 optionNumber（1-based）个选项的 label。
    /// 给 Claude Desktop UIA 做选项行匹配用。解析失败 / 越界 → 空串（调用方退化为
    /// 仅按编号匹配）。结构同 IslandSessionItem.ParseAskUserQuestion，但这里在
    /// Core 类型边界外、且只需要一个 label，单独走一遍轻量解析不引入耦合。
    /// </summary>
    private static string ExtractQuestionOptionLabel(
        IDictionary<string, object> toolInput, int optionNumber)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(toolInput);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("questions", out var qs)
                || qs.ValueKind != System.Text.Json.JsonValueKind.Array
                || qs.GetArrayLength() == 0)
                return "";
            var q = qs[0];
            if (!q.TryGetProperty("options", out var opts)
                || opts.ValueKind != System.Text.Json.JsonValueKind.Array)
                return "";
            int idx = optionNumber - 1;
            if (idx < 0 || idx >= opts.GetArrayLength()) return "";
            var opt = opts[idx];
            return (opt.TryGetProperty("label", out var l) ? l.GetString() : "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 岛上卡片"权限模式切换"三按钮（accept edits / auto / plan）的统一入口。
    ///
    /// ⚠ 尽力而为 (BEST-EFFORT)，刻意如此 ——
    /// Claude Code 切换权限模式的唯一交互是<b>在它的终端里按 Shift+Tab 循环</b>
    /// （accept edits → auto → plan → normal → …）。<b>不存在</b>"把模式设成 X"的
    /// 幂等按键 / CLI flag / IPC。所以这里对三种 ModeKind 都只做同一件事：聚焦该
    /// 会话的终端并发<b>一次</b> Shift+Tab（循环一格）。无法保证盲发就精确停在用户
    /// 想要的那个模式 —— 用户可能需要点多次直到终端 TUI 上显示目标模式。
    /// "精确按模式定位" + "Claude Desktop 端投递" 是已知后续项，与岛上权限审批
    /// (RespondToPermissionAsync) 同样的桌面端 caveat。
    ///
    /// 分流（与 RespondToPermissionAsync 一致）：
    ///   - claude-desktop entrypoint：没有终端可注入、UIA 也没有可靠的模式切换控件
    ///     → 仅写一行 %TEMP%\openisland-uia.log 诊断，优雅 no-op（绝不抛/崩）。
    ///   - cli / 缺失：解析 claude.exe PID → 终端发一次 Shift+Tab。
    ///
    /// Best-effort by design: Claude Code only cycles modes via Shift+Tab in its
    /// terminal (no idempotent set-mode). One Shift+Tab per click; exact target mode
    /// not guaranteed. Desktop delivery is a known follow-up (logged + no-op).
    /// </summary>
    public async Task<bool> SendModeSwitchAsync(string sessionId, ModeKind kind)
    {
        var session = GetSession(sessionId);
        if (session == null) return false;

        var entrypoint = session.ClaudeMetadata?.Entrypoint;
        if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
        {
            // 桌面端：没有可靠机制（既无终端注入，UIA 也没有稳定的模式切换入口）。
            // 落一行诊断到与权限功能同一个日志文件，然后优雅 no-op —— 不抛异常、不崩。
            try
            {
                var diagPath = Path.Combine(Path.GetTempPath(), "openisland-uia.log");
                File.AppendAllText(diagPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] mode switch requested: {kind} " +
                    $"(desktop best-effort: not implemented)\n");
            }
            catch { /* 日志失败也不能影响 UI */ }
            return false;
        }

        // CLI / 缺失 entrypoint：解析活动 claude.exe PID，聚焦其终端并发一次 Shift+Tab。
        // 所有同步 Win32（父链 / EnumWindows / AttachConsole）挪线程池，避免阻塞 UI。
        var claudePid = ResolveClaudeProcessId(session);
        if (claudePid is not int pid)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SendModeSwitchAsync: no live claude.exe for session {sessionId} (kind={kind}); no-op");
            return false;
        }

        // 注意：发的是 Shift+Tab *循环*，不是"设为 {kind}"——精确落点不保证（见上方注释）。
        return await _terminalJumpService.SendShiftTabToTerminalAsync(pid);
    }

    /// <summary>
    /// 岛上卡片"快捷回复"入口：把一段文字发进该会话（粘贴 + 回车），不用切回终端。
    /// 按 entrypoint 分流：
    ///   - claude-desktop → 粘贴进 Claude Desktop 聊天输入框
    ///   - cli / 缺失 → 解析该会话终端 PID（仅正向匹配 cwd/sessionId，绝不"唯一 claude"兜底，
    ///     避免把整段 prompt 注入到错误会话），粘贴进该终端
    /// 注入前 TerminalJumpService 会校验目标窗口在前台，否则中止。
    /// </summary>
    public async Task<InjectResult> SendQuickReplyAsync(string sessionId, string text)
    {
        var prepared = QuickReplyText.Prepare(text);
        if (!prepared.Ok)
            return new InjectResult(false, prepared.Reason);

        var session = GetSession(sessionId);
        if (session == null)
            return new InjectResult(false, "no-session");

        var entrypoint = session.ClaudeMetadata?.Entrypoint;
        if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
            return await _terminalJumpService.SendTextToClaudeDesktopAsync(prepared.Text, submit: true);

        var pid = ResolveTerminalPidForInjection(session);
        if (pid is not int p)
            return new InjectResult(false, "no-terminal-match");

        return await _terminalJumpService.SendTextToTerminalAsync(p, prepared.Text, submit: true);
    }

    /// <summary>
    /// 给注入类操作（快捷回复 / 后续 /model 切换）解析终端 PID：仅 cwd / sessionId 正向匹配，
    /// 刻意不用 ResolveClaudeProcessId 的"唯一 claude 兜底"——盲发到错误会话危害更大（review #7）。
    /// </summary>
    private int? ResolveTerminalPidForInjection(AgentSession session)
    {
        var candidates = _processMonitor.GetRunningSessions()
            .Select(r => new TerminalCandidate(r.ProcessId, r.WorkingDirectory, r.SessionId))
            .ToList();
        return TerminalTargeting.ResolvePid(session.JumpTarget?.WorkingDirectory, session.Id, candidates);
    }

    /// <summary>
    /// 岛上卡片"切换模型"入口。
    ///   - Claude 官方档：清掉 ~/.claude/settings.json 的第三方 env（新 CLI 会话回到 Anthropic 登录）。
    ///   - Claude 模型档（opus/sonnet/haiku）：注入 /model（实时，CLI+Desktop，复用快捷回复注入）。
    ///   - 第三方档：把 provider 的 env 写进 ~/.claude/settings.json（原子写 + 备份），重开终端后生效。
    /// 活动档 Id 的持久化由调用方（VM 经 WorkspaceSettings）负责。
    /// </summary>
    public async Task<InjectResult> SwitchModelAsync(string sessionId, ModelProfile profile)
    {
        if (profile.Kind == ModelKind.ClaudeModel)
        {
            if (profile.Id == ModelProfile.OfficialClaudeId)
            {
                var cleared = await Task.Run(() => WriteClaudeProviderEnv(ClaudeModelEnv.ClearManaged));
                return new InjectResult(cleared, cleared ? "switched-official" : "write-failed");
            }

            var cmd = ClaudeModelEnv.BuildModelCommand(profile);
            if (string.IsNullOrEmpty(cmd))
                return new InjectResult(false, "no-op");
            return await SendQuickReplyAsync(sessionId, cmd); // 注入 /model（实时）
        }

        // 第三方：写 env（重开终端后生效）。空 key 直接拒绝，避免写出半截配置让下次会话起不来。
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            return new InjectResult(false, "no-key");
        var wrote = await Task.Run(() => WriteClaudeProviderEnv(root => ClaudeModelEnv.ApplyThirdParty(root, profile)));
        return new InjectResult(wrote, wrote ? "needs-restart" : "write-failed");
    }

    /// <summary>
    /// 全局切换活动模型（写 ~/.claude/settings.json，对新 CLI 会话生效）—— 无需会话上下文。
    ///   - Claude 档：SetOfficialModel（官方端点；内置 Opus/Sonnet/Haiku 带模型 id，官方档清空）。
    ///   - 第三方档：ApplyThirdParty（写 env 块），空 key 直接拒绝。
    /// </summary>
    public async Task<InjectResult> SwitchGlobalModelAsync(ModelProfile profile)
    {
        if (profile.Kind == ModelKind.ClaudeModel)
        {
            var ok = await Task.Run(() => WriteClaudeProviderEnv(root => ClaudeModelEnv.SetOfficialModel(root, profile.Model)));
            var reason = profile.Id == ModelProfile.OfficialClaudeId ? "switched-official" : "needs-restart";
            return new InjectResult(ok, ok ? reason : "write-failed");
        }

        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            return new InjectResult(false, "no-key");
        var wrote = await Task.Run(() => WriteClaudeProviderEnv(root => ClaudeModelEnv.ApplyThirdParty(root, profile)));
        return new InjectResult(wrote, wrote ? "needs-restart" : "write-failed");
    }

    /// <summary>读 ~/.claude/settings.json → 应用变更 → 原子写回（temp + replace）+ 备份；保留其它字段。</summary>
    private static bool WriteClaudeProviderEnv(Func<JsonObject, JsonObject> mutate)
    {
        // 与 AppendAllowRuleToSettings 共用同一把锁，串行化对 settings.json 的并发写。
        lock (_claudeSettingsFileLock)
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(userProfile, ".claude", "settings.json");

            JsonObject root;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    root = new JsonObject();
                }
                else if (JsonNode.Parse(json) is JsonObject obj)
                {
                    root = obj;
                }
                else
                {
                    // 有效 JSON 但不是对象（数组/标量）—— 不覆盖用户文件，中止。
                    System.Diagnostics.Debug.WriteLine("WriteClaudeProviderEnv: settings.json is not a JSON object; aborting.");
                    return false;
                }
            }
            else
            {
                root = new JsonObject();
            }

            mutate(root);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 单个滚动备份（避免每次切换在 ~/.claude 堆出无数 .backup.<时间戳> 文件）。
            if (File.Exists(path))
                File.Copy(path, path + ".openisland.bak", overwrite: true);

            var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WriteClaudeProviderEnv failed: {ex.Message}");
            return false;
        }
    }

    public async Task JumpToSessionAsync(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session == null) return;

        // 1) entrypoint 决定路径 —— Claude 有两种运行方式，宿主窗口完全不同：
        //    - "claude-desktop": Anthropic 桌面端应用（C:\Program Files\WindowsApps\
        //      Claude_X.X.X.X_x64__*\app\Claude.exe，UWP/MSIX），自己持窗，标题 "Claude"。
        //      内部会派生 claude.exe 子进程做每个 session 的实际工作，但用户点卡片
        //      想激活的是桌面应用本身。
        //    - "cli" / 缺失: 用户终端里跑的 claude.exe，承载终端 = WT / conhost。
        //
        //    必须在尝试任何 CLI 路径之前先分流，否则桌面 session 派生的子 claude.exe
        //    会被当成 CLI claude（它的 cwd 跟 session 一致），把用户带到错误的窗口。
        var entrypoint = session.ClaudeMetadata?.Entrypoint;
        if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
        {
            // 传 session.Title 让 UIA 在 Claude Desktop 侧边栏点对应 session 按钮 ——
            // 不传只是激活到桌面端 home 页面，用户得自己再点一次。
            //
            // 必须 Task.Run 到线程池 —— 否则在 WPF UI 线程上跑 1+ 秒的 UIA enumeration
            // 会阻塞消息泵，导致 UIA 内部依赖的 COM 调用静默失败（standalone test 在
            // console app 上 MTA 线程跑没问题，但 WPF STA UI 线程上 sleep 200ms 期间
            // 不泵消息，AutomationElement.FindAll 经常拿不到完整树）。
            await Task.Run(() => _terminalJumpService.ActivateClaudeDesktopWindow(session.Title));
            return; // 桌面 session 永不退化到终端 —— 避免用户被弹出无关的 WT 窗口
        }

        // 2) CLI 路径：先试激活当前还活着的 claude 所在的终端窗口。
        //    所有同步 Win32 调用（toolhelp 父链、EnumWindows、AttachConsole、窗口标题打分）
        //    挪到线程池，避免阻塞 UI（toolhelp 已经很快但 EnumWindows 还可能慢一点）。
        var activated = await Task.Run(() => TryActivateLiveTerminal(session));
        if (activated) return;

        // 3) CLI claude 已退 / 找不到：在用户现有 WT 里新开 tab 跑 `claude --resume`
        var resumeCwd = session.JumpTarget?.WorkingDirectory
                        ?? ResolveFallbackWorkingDirectory(session);
        if (await _terminalJumpService.LaunchClaudeResumeAsync(sessionId, resumeCwd))
            return;

        // 4) 最后兜底：原"开终端到 cwd 不跑命令"路径
        if (session.JumpTarget != null)
        {
            await _terminalJumpService.JumpToSessionAsync(session);
        }
        else if (!string.IsNullOrEmpty(resumeCwd))
        {
            await _terminalJumpService.JumpToWorkingDirectoryAsync(resumeCwd);
        }
    }

    /// <summary>
    /// 同步路径：尝试激活当前还活着的 claude 所在的终端窗口。三层兜底依次：
    ///   1. 父链向上找承载终端（含 AttachConsole 拿 conhost 的子兜底）
    ///   2. 启发式：根据窗口标题在所有可见终端宿主里找最像的（项目名命中 +100，
    ///      "claude" 命中 +10，"✳" 活动标记 +5）—— 处理进程树断了但终端仍存在的
    ///      场景（claude 通过 start/.lnk/Win+R 启动时父链断在 explorer）
    /// 找不到 / claude 已退 → 返回 false，调用方走"开新终端 resume"路径。
    /// </summary>
    private bool TryActivateLiveTerminal(AgentSession session)
    {
        var claudePid = ResolveClaudeProcessId(session);
        if (claudePid is not int pid) return false;

        if (_terminalJumpService.ActivateTerminalByPidChain(pid)) return true;

        var cwd = session.JumpTarget?.WorkingDirectory ?? ResolveFallbackWorkingDirectory(session);
        var projDir = !string.IsNullOrEmpty(cwd) ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : null;
        return _terminalJumpService.ActivateTerminalByWindowHeuristic(projDir);
    }

    /// <summary>
    /// 把 session 反查到 ProcessMonitor 里运行中的 claude.exe PID。优先 cwd 精确匹配，
    /// 退化用 sessionId / 项目名。
    /// Map session → live claude.exe PID via ProcessMonitor; cwd-exact first, then sessionId,
    /// then project-name substring.
    /// </summary>
    private int? ResolveClaudeProcessId(AgentSession session)
    {
        var running = _processMonitor.GetRunningSessions();
        if (running.Count == 0) return null;

        var cwd = session.JumpTarget?.WorkingDirectory?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(cwd))
        {
            var hit = running.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.WorkingDirectory) &&
                string.Equals(r.WorkingDirectory!.TrimEnd('\\', '/'), cwd, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.ProcessId;
        }

        var bySessionId = running.FirstOrDefault(r => r.SessionId == session.Id);
        if (bySessionId != null) return bySessionId.ProcessId;

        if (!string.IsNullOrEmpty(session.Title))
        {
            var byProject = running.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.ProjectName) &&
                session.Title.Contains(r.ProjectName!, StringComparison.OrdinalIgnoreCase));
            if (byProject != null) return byProject.ProcessId;
        }

        // Windows 上 claude.exe 拿不到自己的 cwd（PEB 读需要 PROCESS_VM_READ），所以
        // ProcessMonitor.RunningSessionInfo.WorkingDirectory 经常为空，上面的精确匹配全失败。
        // 只有一个 claude.exe 在跑时不需要做选择 —— 那就是它。这覆盖了绝大多数实际场景。
        // claude.exe's cwd is unreadable on Windows without PROCESS_VM_READ, so cwd-match
        // commonly fails. If exactly one claude is alive, it must be the target.
        if (running.Count == 1) return running.First().ProcessId;

        return null;
    }

    /// <summary>
    /// 没有 JumpTarget 时，从运行中的 Claude 进程或转录路径推断工作目录
    /// </summary>
    private string? ResolveFallbackWorkingDirectory(AgentSession session)
    {
        // 优先：进程监控中匹配同 sessionId 或同标题的运行进程
        var running = _processMonitor.GetRunningSessions();
        var match = running.FirstOrDefault(r => r.SessionId == session.Id)
                 ?? running.FirstOrDefault(r =>
                        !string.IsNullOrEmpty(session.Title) &&
                        (r.ProjectName?.Equals(session.Title, StringComparison.OrdinalIgnoreCase) == true));
        if (!string.IsNullOrEmpty(match?.WorkingDirectory))
            return match.WorkingDirectory;

        // 其次：从 Claude 转录路径反推工作目录
        // ~/.claude/projects/<encoded-cwd>/<sessionId>.jsonl，目录名是把 cwd 中分隔符换成 '-' 的结果
        var transcript = session.ClaudeMetadata?.TranscriptPath;
        if (!string.IsNullOrEmpty(transcript))
        {
            var encodedDir = Path.GetFileName(Path.GetDirectoryName(transcript));
            var decoded = DecodeClaudeProjectDir(encodedDir);
            if (!string.IsNullOrEmpty(decoded) && Directory.Exists(decoded))
                return decoded;
        }

        return null;
    }

    /// <summary>
    /// Claude 把 cwd 中所有路径分隔符替换成 '-' 作为项目目录名。
    /// 反推时把开头的盘符 "C--Users-..." 还原为 "C:\Users\..."。
    /// 因为 '-' 在文件名中也合法，反推不一定唯一，所以只在结果路径真实存在时才返回。
    /// </summary>
    private static string? DecodeClaudeProjectDir(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return null;

        // Windows 形如 "C--Users-foo-bar"：首字母+'-'+'-' 表示盘符
        if (encoded.Length >= 3 && char.IsLetter(encoded[0]) && encoded[1] == '-' && encoded[2] == '-')
        {
            var rest = encoded[3..].Replace('-', Path.DirectorySeparatorChar);
            return $"{encoded[0]}:{Path.DirectorySeparatorChar}{rest}";
        }

        return null;
    }

    /// <summary>
    /// 跳转到指定工作目录的终端
    /// </summary>
    public async Task JumpToWorkingDirectoryAsync(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return;
        await _terminalJumpService.JumpToWorkingDirectoryAsync(workingDirectory);
    }

    /// <summary>
    /// 获取当前真正运行中的进程列表（直接来自ProcessMonitor）
    /// </summary>
    public IReadOnlyCollection<RunningSessionInfo> GetRunningProcesses()
    {
        return _processMonitor.GetRunningSessions();
    }

    private void CleanupStaleSessions()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var staleSessions = _lastHeartbeat
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in staleSessions)
        {
            _lastHeartbeat.TryRemove(sessionId, out _);
            // 可选：将超时会话标记为stale
        }
    }

    private void NotifySessionsChanged()
    {
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _safetyScanTimer?.Dispose();
        _processSyncTimer?.Dispose();
        // drain 完成防抖定时器：否则一个 pending 的 completion 定时器可能在 teardown 之后
        // 才回调 TaskCompleted（落到已释放的 VM/Dispatcher 上）。
        lock (_completionDebounceLock)
        {
            foreach (var t in _completionDebounce.Values) { t.Stop(); t.Dispose(); }
            _completionDebounce.Clear();
        }
        _bridgeServer.MessageReceived -= OnBridgeMessageReceived;
        _processMonitor.RunningSessionsChanged -= OnRunningSessionsChanged;
        _claudeWatcher.EventEmitted -= OnClaudeWatcherEvent;
    }
}
