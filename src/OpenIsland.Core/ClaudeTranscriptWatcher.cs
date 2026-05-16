using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using OpenIsland.Core.Models;

namespace OpenIsland.Core;

/// <summary>
/// 基于 FileSystemWatcher 的 Claude Code transcript 监听器
///
/// 取代 SessionManager 里 5s 的 _scanTimer 与 hooks 通道（Stop / PreToolUse / PostToolUse 等），
/// 让会话状态直接由 ~/.claude/projects/**/*.jsonl 的写入驱动。
///
/// 行为约束：
///   - Filter = "*.jsonl"，IncludeSubdirectories = true
///   - 跳过 <see cref="ClaudeTranscriptDiscovery.IsAgentSession"/> 命中的 agent-* 文件
///   - 100ms 去抖（同一个文件的 Changed 事件折叠成一次解析）
///   - Start() 时先做一次全量扫描，给已有 transcripts 都补发 SessionStarted，避免 App 重启丢状态
/// </summary>
public class ClaudeTranscriptWatcher : IDisposable
{
    private readonly ILogger<ClaudeTranscriptWatcher>? _logger;
    private readonly ClaudeTranscriptDiscovery _discovery;
    private FileSystemWatcher? _watcher;
    // 已经 emit 过 SessionStarted 的文件（按完整路径）
    private readonly ConcurrentDictionary<string, byte> _knownFiles = new();
    // 已经 emit 过的 JumpTarget（用于决定是否补发 JumpTargetUpdated）
    private readonly ConcurrentDictionary<string, string> _emittedJumpTargets = new();
    // 文件级去抖
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new();
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(100);
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private bool _disposed;

    // 每个文件最近一次"我们处理完"的时刻 + 当时 emit 的 sessionId（静默检测用）
    // last-write 时间 + sessionId 给 idle tick 用：mtime 超过阈值 → emit Idle。
    private readonly ConcurrentDictionary<string, (DateTime LastSeen, string SessionId)> _lastActivity = new();
    // 每个文件上次 emit 的 phase。phase 去重的唯一来源 —— 主动 emit（EmitForSession）
    // 与 idle-tick 兜底 emit 都查这个表，仅在变化时 emit。这样：
    //   - SessionState/UI 不被同 phase 重复事件刷屏；
    //   - DispatchEventAsync 那层就不会因 watcher 的高频 emit 反复触发 TaskCompleted；
    //   - smoke 测试的"末条 assistant 没变时不重复 emit Idle"不变量保留。
    private readonly ConcurrentDictionary<string, SessionPhase> _lastEmittedPhase = new();
    // 是否允许 idle-tick 兜底转 Idle。仅当 transcript 末尾是 assistant + stop_reason='tool_use'
    // 时为 true —— Claude Code 实测一轮的最后一条 assistant response 多以 tool_use 结尾，
    // 真正的 end_turn 经常没刷到 jsonl，所以兜底是必要的。但若末尾是 user message（用户刚问、
    // 或 tool_result 等待下一轮 API），Claude 仍在工作，绝不能因静默就翻 Idle —— 否则用户在
    // "Lollygagging…"（API 调用进行中）的几十秒里灯一直绿，违反直觉。
    private readonly ConcurrentDictionary<string, byte> _eligibleForIdleFallback = new();
    // Idle 兜底阈值。3 秒折衷：短到回完话能很快变绿，长到不会被两次 tool 调用之间的间隙误判。
    private readonly TimeSpan _idleSilenceThreshold = TimeSpan.FromSeconds(3);
    private Timer? _idleTickTimer;

    /// <summary>
    /// 监听器产生的 AgentEvent（每个事件单独触发一次）。
    /// SessionManager 应订阅此事件并调用其 DispatchEventAsync 路径。
    /// </summary>
    public event EventHandler<AgentEvent>? EventEmitted;

    public ClaudeTranscriptWatcher(ClaudeTranscriptDiscovery discovery, ILogger<ClaudeTranscriptWatcher>? logger = null)
    {
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>
    /// 启动 watcher：先做一次全量扫描补齐状态，再挂 FileSystemWatcher。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var dir = _discovery.ProjectsDirectory;
        if (!Directory.Exists(dir))
        {
            _logger?.LogInformation("Claude projects directory missing, watcher idle: {Dir}", dir);
            return;
        }

        // 启动时全量扫描：把已有 transcripts 转成 SessionStarted 事件，App 重启后立即恢复显示
        try
        {
            var sessions = await _discovery.ScanSessionsAsync(ct);
            foreach (var s in sessions)
            {
                EmitInitialSession(s);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Initial Claude transcript scan failed");
        }

        _watcher = new FileSystemWatcher(dir)
        {
            Filter = "*.jsonl",
            IncludeSubdirectories = true,
            // 64KB 内部缓冲——再大 Windows 也不一定让，但深目录树下默认 8KB 容易溢出
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;

        // 静默检测 timer：每秒 tick，对超过阈值没写入的 transcript emit Idle
        _idleTickTimer = new Timer(_ => OnIdleTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _logger?.LogInformation("ClaudeTranscriptWatcher started on {Dir}", dir);
    }

    /// <summary>
    /// 每秒检查所有最近活跃的 transcript：mtime 静默超过阈值 → Claude 停手 → emit Idle。
    /// 这是对 stop_reason='end_turn' 不可靠的兜底；DispatchEventAsync 用 prevPhase 防重，
    /// 所以多次 emit Idle 不会引起 beep 反复。
    /// </summary>
    private void OnIdleTick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        foreach (var (path, entry) in _lastActivity)
        {
            if (now - entry.LastSeen < _idleSilenceThreshold) continue;
            // 当前 phase 已是 Idle → 跳过（不论是因为初始扫描预填、上次 stop_reason=end_turn
            // 主动 emit 还是上次 idle-tick emit 的）
            if (_lastEmittedPhase.TryGetValue(path, out var lp) && lp == SessionPhase.Idle) continue;
            // 末尾不是 assistant tool_use（比如末尾是 user message → Claude 在思考）→ 不兜底，
            // 否则 "Lollygagging…" 期间的几十秒静默会让灯错误地变绿。
            if (!_eligibleForIdleFallback.ContainsKey(path)) continue;

            try
            {
                Emit(new SessionActivityUpdated
                {
                    SessionId = entry.SessionId,
                    Summary = "Ready for input",
                    Phase = SessionPhase.Idle
                });
                _lastEmittedPhase[path] = SessionPhase.Idle;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Idle tick emit failed for {Path}", path);
            }
        }
    }

    /// <summary>
    /// 同步入口（DI 启动场景）；内部 fire-and-forget StartAsync。
    /// </summary>
    public void Start()
    {
        _ = StartAsync();
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }
        foreach (var t in _debounceTimers.Values) t.Dispose();
        _debounceTimers.Clear();
        _idleTickTimer?.Dispose();
        _idleTickTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _processLock.Dispose();
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => OnChanged(sender, e);

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(e.FullPath)) return;
        if (!e.FullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        if (ClaudeTranscriptDiscovery.IsAgentSession(e.FullPath)) return;

        // 同一文件 100ms 内的多次 Changed 折叠成一次解析
        var path = e.FullPath;
        _debounceTimers.AddOrUpdate(
            path,
            // 第一次：新建一个 100ms one-shot timer
            _ => new Timer(_ => _ = ProcessFileAsync(path), null, _debounceInterval, Timeout.InfiniteTimeSpan),
            // 已有：重置触发时间到 100ms 后
            (_, existing) =>
            {
                existing.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
                return existing;
            });
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher 缓冲溢出（深目录 + 大量 I/O）会丢事件；记录，依赖 SessionManager 的低频补扫
        _logger?.LogWarning(e.GetException(), "ClaudeTranscriptWatcher buffer overflow / error");
    }

    private async Task ProcessFileAsync(string path)
    {
        // 去抖 timer 触发后释放（保留 dictionary 项无所谓，后续会被 AddOrUpdate 重置）
        await _processLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return;

            ClaudeSessionInfo? session;
            try
            {
                session = await _discovery.ParseSessionFileAsync(path);
            }
            catch (IOException)
            {
                // 写入争抢：下一次 Changed 还会再触发，丢一次也无妨
                return;
            }
            if (session == null) return;

            EmitForSession(session);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ClaudeTranscriptWatcher process failed: {Path}", path);
        }
        finally
        {
            _processLock.Release();
        }
    }

    /// <summary>
    /// 启动时全量扫描的 emit：每个文件一次性补一组事件。
    /// </summary>
    private void EmitInitialSession(ClaudeSessionInfo session)
    {
        if (string.IsNullOrEmpty(session.SourcePath)) return;
        EmitForSession(session, isInitial: true);
    }

    /// <summary>
    /// 把一个 ClaudeSessionInfo 翻译成一组 AgentEvent 并按顺序 emit：
    ///   1) 第一次见到的文件：SessionStarted（带 JumpTarget / ClaudeMetadata）
    ///   2) cwd 后续才出现：JumpTargetUpdated
    ///   3) 始终：SessionActivityUpdated（Phase=Running，Title 仅在 HasRealTitle 时携带）
    ///   4) 始终：ClaudeSessionMetadataUpdated（token / cost）
    ///   5) 末条 assistant.stop_reason ∈ {end_turn, stop_sequence}：再 emit 一条 Phase=Idle 的 ActivityUpdated
    ///      （这是替代旧 Stop hook 的"任务完成"信号；DispatchEventAsync 会据此触发 TaskCompleted）
    /// </summary>
    private void EmitForSession(ClaudeSessionInfo session, bool isInitial = false)
    {
        var path = session.SourcePath!;
        var sessionId = session.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        var jumpTarget = session.BuildJumpTarget();

        // 1) 首次：SessionStarted
        if (_knownFiles.TryAdd(path, 1))
        {
            var metadata = BuildMetadata(session);

            // 初始 phase：mtime 超过静默阈值没改的 transcript 当作 Idle，否则 Running。
            // 这是关键修复：启动时全量扫描会把 N 条历史会话拿出来，如果统统当 Running，
            // DynamicIsland 的"任意 session 在 Running 就显示蓝灯"判定就永真了 ——
            // 即使当前活跃那条会话真的转 Idle，剩下几十条历史还是 Running，灯永远蓝。
            // Initial phase: stale transcripts (mtime > _idleSilenceThreshold) start as Idle so
            // they don't perpetually pin the dynamic-island dot to blue. Fresh transcripts get
            // Running and the idle-tick timer transitions them to Idle after silence.
            var initialPhase = SessionPhase.Running;
            DateTime mtime = DateTime.UtcNow;
            try { mtime = File.GetLastWriteTimeUtc(path); } catch { /* keep now */ }
            if (isInitial && DateTime.UtcNow - mtime > _idleSilenceThreshold)
                initialPhase = SessionPhase.Idle;

            Emit(new SessionStarted
            {
                SessionId = sessionId,
                Title = session.Title,
                Tool = AgentTool.ClaudeCode,
                Origin = SessionOrigin.Local,
                InitialPhase = initialPhase,
                Summary = $"{session.Summary} | Tokens: {session.Usage.TotalTokens:N0} | Cost: ${session.TotalCost:F4}",
                JumpTarget = jumpTarget,
                ClaudeMetadata = metadata
            });
            if (jumpTarget?.WorkingDirectory is { Length: > 0 } cwdNew)
                _emittedJumpTargets[path] = cwdNew;

            if (isInitial)
            {
                // 预填静默检测和 phase 去重表：用文件 mtime 当 LastSeen，记下 InitialPhase。
                // 这样 idle-tick 不会对老的 idle session 误触 TaskCompleted beep（phase 已是 Idle 跳过），
                // 真正的下一次写入会重新进入"emit Running → 静默 1.5s → emit Idle"循环。
                _lastActivity[path] = (mtime, sessionId);
                _lastEmittedPhase[path] = initialPhase;
                return;
            }
        }
        else
        {
            // 2) 已存在的 session：cwd 之前没有，现在有了 —— 补发 JumpTargetUpdated
            if (jumpTarget?.WorkingDirectory is { Length: > 0 } cwdNow
                && !_emittedJumpTargets.TryGetValue(path, out var cwdPrev))
            {
                Emit(new JumpTargetUpdated { SessionId = sessionId, JumpTarget = jumpTarget });
                _emittedJumpTargets[path] = cwdNow;
            }
            else if (jumpTarget?.WorkingDirectory is { Length: > 0 } cwdNow2
                     && _emittedJumpTargets.TryGetValue(path, out var cwdPrev2)
                     && cwdPrev2 != cwdNow2)
            {
                Emit(new JumpTargetUpdated { SessionId = sessionId, JumpTarget = jumpTarget });
                _emittedJumpTargets[path] = cwdNow2;
            }
        }

        // 3) Activity 更新：phase 由"末条 assistant 的 stop_reason"单次直接决定。
        // 之前是"先无脑 emit Running 再可能补 Idle"——但 transcript 末尾经常是 user/system/
        // file-history-snapshot 这种非 assistant 行的追加，这种 Changed 触发后会把 Phase
        // 错误地拉回 Running，且 _idleEmitted 防重导致 Idle 永远补不回去。改成单次 emit。
        // The phase is derived from the last assistant message's stop_reason in a single
        // emission. The previous "Running first, maybe Idle later" pattern was knocking the
        // phase back to Running every time a non-assistant line (user / system / file-history-
        // snapshot) appended to the transcript, leaving the dot stuck on blue.
        // Phase 判断：基于 transcript 末尾的"主消息"（user 或 assistant，跳过 system/file-history-snapshot）
        //   末尾 = user TEXT（用户输入）         → Running，不允许兜底
        //                                          Claude 处于 "Lollygagging"（API 调用进行中）阶段，
        //                                          transcript 可能完全静默几十秒，绝不能因静默转 Idle。
        //   末尾 = user tool_result               → Running，*允许* 兜底
        //                                          Claude 应立即用 tool_result 调下一次 API；如果静默
        //                                          _idleSilenceThreshold 还没新写入，认为一轮已结束（end_turn 没刷盘）。
        //   末尾 = assistant + end_turn/stop_sequence → Idle（明确"我说完了"信号）
        //   末尾 = assistant + tool_use           → Running，允许兜底（end_turn 没刷盘的常见形态）
        var lastMain = session.Messages.LastOrDefault(m => m.Role == "user" || m.Role == "assistant");
        // CLI session：任务完成靠 Claude Code 的 Stop hook（SessionManager eventName=="stop"
        // 分支翻 Idle），watcher 默认只 emit Running，仅 [Request interrupted] 中断标记兜底
        // —— 避免之前 stop_reason 推断在 multi-step 中段误判 Idle、绿灯/响铃乱闪。
        //
        // Claude Desktop session：内置 claude.exe 不跑用户安装的 hook，Stop hook 永远不来，
        // 否则桌面 session 一轮结束后永远卡在蓝灯（Running）回不到绿灯（Idle）。这种情况
        // 必须由 watcher 用「末条主消息 = assistant 且 stop_reason ∈ {end_turn,
        // stop_sequence}」作为权威的"我这轮说完了"信号转 Idle。
        // 安全性：带 tool_use 的中间 assistant 消息 stop_reason 是 "tool_use"，不是
        // end_turn/stop_sequence，所以这个判断不会在 multi-step 中段误判完成。
        bool isDesktop = string.Equals(session.Entrypoint, "claude-desktop",
            StringComparison.OrdinalIgnoreCase);
        bool isIdle =
            (lastMain != null && lastMain.Role == "user" && lastMain.IsInterruptMarker) ||
            (isDesktop && lastMain != null && lastMain.Role == "assistant" &&
             (string.Equals(lastMain.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(lastMain.StopReason, "stop_sequence", StringComparison.OrdinalIgnoreCase)));
        var newPhase = isIdle ? SessionPhase.Idle : SessionPhase.Running;
        bool eligibleForIdleFallback = false;
        _eligibleForIdleFallback.TryRemove(path, out _);

        // phase 去重：仅当本文件的 phase 真的变化才 emit Activity（避免 SessionState/UI 反复刷）
        var prevPhase = _lastEmittedPhase.TryGetValue(path, out var pp) ? (SessionPhase?)pp : null;
        if (prevPhase != newPhase)
        {
            // Summary 取最后一条 assistant 的 content；user 消息的 content 经常是 tool_result block 没展示价值
            var lastAssistantContent = session.Messages.LastOrDefault(m => m.Role == "assistant")?.Content;
            var activitySummary = isIdle
                ? "Ready for input"
                : ((lastAssistantContent ?? session.Summary ?? "").Truncate(100));
            if (string.IsNullOrEmpty(activitySummary))
                activitySummary = isIdle ? "Ready for input" : "New session";

            Emit(new SessionActivityUpdated
            {
                SessionId = sessionId,
                Summary = activitySummary,
                Phase = newPhase,
                Title = session.HasRealTitle ? session.Title : null,
                LastTranscriptTimestamp = lastMain?.Timestamp
            });
            _lastEmittedPhase[path] = newPhase;
        }

        // 4) Token/Cost 元数据 —— 用量随时变化，不去重
        Emit(new ClaudeSessionMetadataUpdated
        {
            SessionId = sessionId,
            ClaudeMetadata = BuildMetadata(session)
        });

        // 记录 last activity 时间给静默检测 timer 用
        _lastActivity[path] = (DateTime.UtcNow, sessionId);
    }

    private static ClaudeMetadata BuildMetadata(ClaudeSessionInfo session)
    {
        return new ClaudeMetadata
        {
            TranscriptPath = session.SourcePath,
            Model = session.Model,
            InputTokens = session.Usage.InputTokens,
            OutputTokens = session.Usage.OutputTokens,
            CacheReadTokens = session.Usage.CacheReadTokens,
            CacheCreationTokens = session.Usage.CacheCreationTokens,
            TotalCost = session.TotalCost,
            ActiveSubagents = 0,
            ActiveTasks = 0,
            Entrypoint = session.Entrypoint
        };
    }

    private void Emit(AgentEvent ev)
    {
        try
        {
            EventEmitted?.Invoke(this, ev);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EventEmitted handler threw");
        }
    }
}
