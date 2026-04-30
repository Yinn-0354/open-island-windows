using System.Text.Json;
using OpenIsland.Core;
using OpenIsland.Core.Models;

// Smoke tests that actually drive the same code paths the WPF app uses,
// printing observable results so a human can verify the three reported bugs are fixed.

int failures = 0;
void Section(string name) { Console.WriteLine(); Console.WriteLine($"=== {name} ==="); }
void Pass(string msg) { Console.WriteLine($"  PASS  {msg}"); }
void Fail(string msg) { Console.WriteLine($"  FAIL  {msg}"); failures++; }

// ----------------------------------------------------------------------------
Section("Problem 1: transcript scan title overrides hook-set project name");

var discovery = new ClaudeTranscriptDiscovery();
var sessions = await discovery.ScanSessionsAsync();
Console.WriteLine($"  discovered {sessions.Count} sessions");

var withRealTitle = sessions.Where(s => s.HasRealTitle).ToList();
var fallback = sessions.Where(s => !s.HasRealTitle).ToList();
Console.WriteLine($"  with real title (custom or first user msg): {withRealTitle.Count}");
Console.WriteLine($"  with project-name fallback only:           {fallback.Count}");

// Show a few with their real first-user-message titles.
foreach (var s in withRealTitle.OrderByDescending(s => s.LastActiveAt).Take(5))
    Console.WriteLine($"    {s.SessionId[..8]}  cwd={s.ProjectName,-25}  title={Truncate(s.Title, 70)}");

// Now exercise SessionState.ApplyActivityUpdated end-to-end.
// Simulate: hook fires session_start with project name, then scan delivers real title.
if (withRealTitle.FirstOrDefault() is { } pick)
{
    var state = new SessionState();
    state = state.Apply(new SessionStarted
    {
        SessionId = pick.SessionId,
        Title = pick.ProjectName ?? "fallback",
        Tool = AgentTool.ClaudeCode,
        InitialPhase = SessionPhase.Running
    });
    var beforeTitle = state.SessionsById[pick.SessionId].Title;

    state = state.Apply(new SessionActivityUpdated
    {
        SessionId = pick.SessionId,
        Summary = "scan tick",
        Phase = SessionPhase.Running,
        Title = pick.HasRealTitle ? pick.Title : null
    });
    var afterTitle = state.SessionsById[pick.SessionId].Title;

    Console.WriteLine($"  simulated: hook title='{beforeTitle}' -> scan title='{Truncate(afterTitle, 60)}'");
    if (afterTitle == pick.Title) Pass("scan title overwrote hook-set project name");
    else Fail($"expected scan title to win, got '{afterTitle}'");

    // Round 2: a subsequent scan tick where only assistant lines were appended
    // (HasRealTitle == false) MUST NOT downgrade the title back to the project name.
    state = state.Apply(new SessionActivityUpdated
    {
        SessionId = pick.SessionId,
        Summary = "scan tick 2",
        Phase = state.SessionsById[pick.SessionId].Phase,
        Title = null  // simulating HasRealTitle == false
    });
    var stableTitle = state.SessionsById[pick.SessionId].Title;
    if (stableTitle == pick.Title) Pass("subsequent scan with no new user msg preserves title");
    else Fail($"title got downgraded by null-title scan tick: '{stableTitle}'");

    // Round 3: scan also preserves the session's Phase (regression check for the
    // "Phase = Running default was clobbering WaitingForApproval" bug).
    state = state.Apply(new PermissionRequested
    {
        SessionId = pick.SessionId,
        Request = new PermissionRequest { Id = "x", ToolName = "Bash", Description = "test" }
    });
    var phaseBefore = state.SessionsById[pick.SessionId].Phase;
    state = state.Apply(new SessionActivityUpdated
    {
        SessionId = pick.SessionId,
        Summary = "scan tick 3",
        Phase = state.SessionsById[pick.SessionId].Phase,  // mirrors SessionManager fix
        Title = null
    });
    var phaseAfter = state.SessionsById[pick.SessionId].Phase;
    if (phaseBefore == SessionPhase.WaitingForApproval && phaseAfter == SessionPhase.WaitingForApproval)
        Pass("scan preserves WaitingForApproval phase");
    else
        Fail($"scan clobbered phase: before={phaseBefore} after={phaseAfter}");
}
else
{
    Fail("no transcript with a real title found in ~/.claude/projects/ — cannot verify");
}

// ----------------------------------------------------------------------------
Section("Problem 3: scan-discovered sessions get a JumpTarget");

var withCwd = sessions.Where(s => !string.IsNullOrEmpty(s.WorkingDirectory)).ToList();
Console.WriteLine($"  scanned sessions with WorkingDirectory: {withCwd.Count}");

if (withCwd.FirstOrDefault() is { } sample)
{
    var jt = sample.BuildJumpTarget();
    var agentSession = sample.ToAgentSession();
    Console.WriteLine($"    sample cwd = {sample.WorkingDirectory}");
    Console.WriteLine($"    BuildJumpTarget().WorkingDirectory = {jt?.WorkingDirectory ?? "<null>"}");
    Console.WriteLine($"    ToAgentSession().JumpTarget.WorkingDirectory = {agentSession.JumpTarget?.WorkingDirectory ?? "<null>"}");

    if (jt?.WorkingDirectory == sample.WorkingDirectory) Pass("BuildJumpTarget populates WorkingDirectory");
    else Fail("BuildJumpTarget did not populate WorkingDirectory");

    if (agentSession.JumpTarget?.WorkingDirectory == sample.WorkingDirectory) Pass("ToAgentSession plumbs JumpTarget through");
    else Fail("ToAgentSession.JumpTarget is missing or wrong");
}
else
{
    Fail("no scanned session has a WorkingDirectory — pick a Claude project that already has messages");
}

// ----------------------------------------------------------------------------
Section("Problem 2: PermissionRequested / QuestionAsked produce events the App can route to sound");

// We can't actually wire SessionManager here (it lives in the App project), but
// we can verify the event-source design: applying a permission_request hook payload
// produces a PermissionRequested event, and a question payload produces QuestionAsked.
var permJson = """
{
  "session_id": "test-perm",
  "hook_event_name": "permission_request",
  "tool_name": "Bash",
  "message": "Run dangerous cmd?"
}
""";
var permPayload = JsonSerializer.Deserialize<OpenIsland.Core.Hooks.ClaudeHookPayload>(permJson);
var permEvent = permPayload?.ToAgentEvent("claude");
Console.WriteLine($"  permission_request payload -> {permEvent?.GetType().Name ?? "<null>"}");
if (permEvent is PermissionRequested) Pass("permission_request -> PermissionRequested event");
else Fail("permission_request did not produce PermissionRequested");

// Sanity-check that SessionState.Apply puts the session into WaitingForApproval,
// which is exactly the state the App's DispatchEventAsync now routes to AttentionRequired.
var permState = new SessionState();
permState = permState.Apply(new SessionStarted
{
    SessionId = "test-perm",
    Title = "x",
    Tool = AgentTool.ClaudeCode
});
permState = permState.Apply(permEvent!);
var phase = permState.SessionsById["test-perm"].Phase;
if (phase == SessionPhase.WaitingForApproval) Pass("state moves to WaitingForApproval");
else Fail($"expected WaitingForApproval, got {phase}");

// ----------------------------------------------------------------------------
Section("Bridge serialization round-trip (root cause check for hook delivery)");

var registerMsg = new OpenIsland.Core.Bridge.RegisterClientMessage
{
    ClientId = "smoke",
    ClientType = "hooks"
};
var registerJson = OpenIsland.Core.Bridge.BridgeCodec.Encode(registerMsg);
Console.WriteLine($"  RegisterClientMessage encoded:\n    {registerJson.TrimEnd()}");
var hookMsg = new OpenIsland.Core.Bridge.ProcessHookMessage
{
    Source = "claude",
    EventData = JsonDocument.Parse("""{"hook_event_name":"stop","session_id":"x"}""").RootElement
};
var hookJson = OpenIsland.Core.Bridge.BridgeCodec.Encode(hookMsg);
Console.WriteLine($"  ProcessHookMessage encoded:\n    {hookJson.TrimEnd()}");

var roundTrip = OpenIsland.Core.Bridge.BridgeCodec.Decode(hookJson.TrimEnd());
if (roundTrip is OpenIsland.Core.Bridge.ProcessHookMessage decoded
    && decoded.Source == "claude"
    && decoded.EventData.TryGetProperty("hook_event_name", out _))
    Pass("processHook serialization round-trip preserves Source/EventData");
else
    Fail($"processHook round-trip failed; got {roundTrip?.GetType().Name ?? "null"}");

// ----------------------------------------------------------------------------
Section("Bridge in-process round-trip (server <- client over Named Pipe)");
{
    var pipeName = "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8];
    await using var server = new OpenIsland.Core.Bridge.BridgeServer(pipeName);
    var receivedMessages = new List<OpenIsland.Core.Bridge.BridgeMessage>();
    var registerSeen = new TaskCompletionSource<bool>();
    var hookSeen = new TaskCompletionSource<bool>();
    server.MessageReceived += (_, e) =>
    {
        receivedMessages.Add(e.Message);
        if (e.Message is OpenIsland.Core.Bridge.ProcessHookMessage) hookSeen.TrySetResult(true);
    };
    server.ClientConnected += (_, _) => registerSeen.TrySetResult(true);
    await server.StartAsync();
    Console.WriteLine($"  test pipe: {pipeName}");

    await using var client = new OpenIsland.Core.Bridge.BridgeCommandClient(TimeSpan.FromSeconds(30), pipeName);
    var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Console.WriteLine($"  cts1 created, IsCancellationRequested={cts1.IsCancellationRequested}");
    var connected = await client.ConnectAsync(cts1.Token);
    Console.WriteLine($"  client connected: {connected}, cts1.IsCancellationRequested={cts1.IsCancellationRequested}");

    var regCompleted = await Task.WhenAny(registerSeen.Task, Task.Delay(2000));
    Console.WriteLine($"  server saw RegisterClient within 2s: {regCompleted == registerSeen.Task}, cts1.IsCancellationRequested={cts1.IsCancellationRequested}");

    // Use a fresh token in case ConnectAsync somehow polluted cts1.
    var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Console.WriteLine($"  about to call SendHookEventAsync, cts2.IsCancellationRequested={cts2.IsCancellationRequested}");
    var ack = await client.SendHookEventAsync("claude",
        JsonDocument.Parse("""{"hook_event_name":"user_prompt_submit","session_id":"x","prompt":"hi"}""").RootElement,
        cts2.Token);
    Console.WriteLine($"  SendHookEventAsync returned: {(ack == null ? "null" : $"Success={ack.Success}")}, cts2.IsCancellationRequested={cts2.IsCancellationRequested}");

    var hookCompleted = await Task.WhenAny(hookSeen.Task, Task.Delay(2000));
    Console.WriteLine($"  server saw ProcessHook within 2s: {hookCompleted == hookSeen.Task}");
    Console.WriteLine($"  total messages received by server: {receivedMessages.Count}");
    foreach (var m in receivedMessages) Console.WriteLine($"    {m.GetType().Name}");

    if (hookCompleted == hookSeen.Task && ack?.Success == true)
        Pass("end-to-end pipe transport works (register + hook event)");
    else
        Fail("bridge transport is dropping messages — hook events never reach the App");
}

// ----------------------------------------------------------------------------
Section("Dynamic Island display: pick by transcript mtime when running process has no cwd");
{
    // Simulate two running claude.exe with no detectable cwd (Windows reality).
    var runningCount = 2;

    // Mirror the scan results into AgentSessions just like SessionManager would.
    var sessionsByRecency = sessions
        .Where(s => !string.IsNullOrEmpty(s.SourcePath))
        .Select(s => new { Session = s.ToAgentSession(), Mtime = TryMtime(s.SourcePath!) })
        .Where(x => x.Mtime.HasValue)
        .OrderByDescending(x => x.Mtime!.Value)
        .Select(x => x.Session)
        .ToList();

    // Apply the same fallback the DynamicIslandViewModel now uses.
    var assigned = new HashSet<string>();
    var displayedTitles = new List<string>();
    for (int i = 0; i < runningCount; i++)
    {
        var session = sessionsByRecency.FirstOrDefault(s => !assigned.Contains(s.Id));
        if (session != null)
        {
            assigned.Add(session.Id);
            displayedTitles.Add(session.Title);
        }
        else
        {
            displayedTitles.Add("Claude");
        }
    }

    Console.WriteLine("  Two running claude.exe processes would now display:");
    foreach (var t in displayedTitles)
        Console.WriteLine($"    -> {Truncate(t, 80)}");

    if (displayedTitles.All(t => t != "Claude" && !string.IsNullOrWhiteSpace(t)))
        Pass("Dynamic Island items get real titles instead of 'Claude' fallback");
    else
        Fail("at least one item still shows 'Claude' fallback");
}

static DateTime? TryMtime(string path)
{
    try { return System.IO.File.GetLastWriteTimeUtc(path); }
    catch { return null; }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: synthetic transcript drives the right events");
{
    // 搭一个临时 .claude 目录结构：tempRoot/projects/C--demo/<sessionId>.jsonl
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--demo");
    Directory.CreateDirectory(projectsDir);
    var sessionId = "watch-" + Guid.NewGuid().ToString("N")[..8];
    var transcriptPath = Path.Combine(projectsDir, sessionId + ".jsonl");
    Console.WriteLine($"  temp root: {tempRoot}");

    // 自定义 discovery，让它扫 tempRoot 而不是 ~/.claude
    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    // 启动 watcher（先做全量扫描——此时还没有文件，应该零事件）
    await watcher.StartAsync();
    await Task.Delay(150); // 让 FileSystemWatcher 起好

    // 写第一条 user 行（模拟 claude.exe 创建会话）
    var userLine = JsonSerializer.Serialize(new
    {
        sessionId,
        cwd = "C:/demo",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "hi watcher" }
    });
    await File.AppendAllTextAsync(transcriptPath, userLine + "\n");

    // 写一条 assistant 行（仍在 Running，没有 stop_reason）
    var assistantLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:01Z",
        message = new
        {
            role = "assistant",
            model = "claude-sonnet-4-6",
            content = new object[] { new { type = "text", text = "thinking..." } },
            usage = new { input_tokens = 100, output_tokens = 50 }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, assistantLine + "\n");

    // 等去抖 + 文件系统通知
    await WaitFor(() => events.Any(e => e is SessionStarted), TimeSpan.FromSeconds(3));
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.Phase == SessionPhase.Running), TimeSpan.FromSeconds(3));

    var snapshot1 = events.ToArray();
    var startedCount = snapshot1.OfType<SessionStarted>().Count(s => s.SessionId == sessionId);
    var runningActivity = snapshot1.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Running);

    if (startedCount >= 1) Pass($"watcher emitted SessionStarted for new transcript ({startedCount}x)");
    else Fail($"expected at least 1 SessionStarted, got {startedCount}");

    if (runningActivity) Pass("watcher emitted Running SessionActivityUpdated");
    else Fail("expected a Running SessionActivityUpdated, got none");

    // 现在追加一行 stop_reason=end_turn——应该 emit Phase=Idle 的 ActivityUpdated（替代 Stop hook）
    var stopLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:02Z",
        message = new
        {
            role = "assistant",
            model = "claude-sonnet-4-6",
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text = "done" } },
            usage = new { input_tokens = 10, output_tokens = 5 }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, stopLine + "\n");

    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.Phase == SessionPhase.Idle), TimeSpan.FromSeconds(3));

    var snapshot2 = events.ToArray();
    var idleActivity = snapshot2.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (idleActivity) Pass("watcher emitted Idle SessionActivityUpdated when stop_reason=end_turn appeared");
    else Fail("expected an Idle SessionActivityUpdated after stop_reason=end_turn, got none");

    // 字节偏移正确性：再追加一行不应该让之前的 idle 事件重复触发
    var idleCountBefore = snapshot2.OfType<SessionActivityUpdated>().Count(a => a.Phase == SessionPhase.Idle);
    var followupLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:03Z",
        message = new { role = "user", content = "next round" }
    });
    await File.AppendAllTextAsync(transcriptPath, followupLine + "\n");
    await Task.Delay(400); // 让去抖触发并解析完
    var snapshot3 = events.ToArray();
    var idleCountAfter = snapshot3.OfType<SessionActivityUpdated>().Count(a => a.Phase == SessionPhase.Idle);
    if (idleCountAfter == idleCountBefore) Pass("Idle event is not re-emitted on subsequent edits while stop_reason persists");
    else Fail($"Idle event re-emitted unexpectedly: {idleCountBefore} -> {idleCountAfter}");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: idle-tick fallback when stop_reason='tool_use' only");
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--demo-tool");
    Directory.CreateDirectory(projectsDir);
    var sessionId = "tool-" + Guid.NewGuid().ToString("N")[..8];
    var transcriptPath = Path.Combine(projectsDir, sessionId + ".jsonl");
    Console.WriteLine($"  temp root: {tempRoot}");

    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    await watcher.StartAsync();
    await Task.Delay(150);

    // 写一条 user + 一条带 tool_use 的 assistant —— Claude Code 实际行为：
    // 一轮里每条 assistant response 含 tool_use block 时 stop_reason='tool_use'，
    // 最终的纯文本 end_turn 经常没刷盘到 jsonl。watcher 必须靠静默 1.5s 兜底转 Idle。
    var userLine = JsonSerializer.Serialize(new
    {
        sessionId,
        cwd = "C:/demo-tool",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "do something" }
    });
    await File.AppendAllTextAsync(transcriptPath, userLine + "\n");

    var toolUseLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:01Z",
        message = new
        {
            role = "assistant",
            model = "claude-sonnet-4-6",
            stop_reason = "tool_use",
            content = new object[] { new { type = "text", text = "I'll use a tool" } },
            usage = new { input_tokens = 100, output_tokens = 50 }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, toolUseLine + "\n");

    // 应该 emit Running（因为 stop_reason 是 tool_use 不是 end_turn）
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.Phase == SessionPhase.Running), TimeSpan.FromSeconds(3));
    var sawRunning = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Running);
    if (sawRunning) Pass("watcher emits Running while stop_reason=tool_use");
    else Fail("expected Running emit while stop_reason=tool_use, none seen");

    // 然后什么也不写，等 1.5s + idle-tick 周期 —— 静默检测应该兜底 emit Idle
    var idleSeenBefore = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (idleSeenBefore) Fail("Idle emitted prematurely (should wait for silence)");

    // 静默 3s 阈值 + 1s tick 间隔 = 至多 4s 后 emit Idle
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Idle),
        TimeSpan.FromSeconds(6));
    var idleSeenAfter = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (idleSeenAfter) Pass("idle-tick emits Idle after 3s silence (no end_turn needed)");
    else Fail("idle-tick failed to emit Idle after silence");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: user tool_result tail allows idle-tick fallback (Claude finished a turn)");
{
    // Claude Code 实测：一轮真正结束时 transcript 末尾常常是 user tool_result（最后一个 tool 的结果），
    // 真正的 end_turn assistant 没刷盘。watcher 必须能从这种状态兜底转 Idle，否则灯永远卡蓝。
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--demo-tr");
    Directory.CreateDirectory(projectsDir);
    var sessionId = "tr-" + Guid.NewGuid().ToString("N")[..8];
    var transcriptPath = Path.Combine(projectsDir, sessionId + ".jsonl");

    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    await watcher.StartAsync();
    await Task.Delay(150);

    // user 输入 + assistant tool_use + user tool_result（tool_use array content）
    var userTextLine = JsonSerializer.Serialize(new
    {
        sessionId,
        cwd = "C:/demo-tr",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "do x" }
    });
    await File.AppendAllTextAsync(transcriptPath, userTextLine + "\n");

    var assistantToolUseLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:01Z",
        message = new
        {
            role = "assistant",
            model = "claude-sonnet-4-6",
            stop_reason = "tool_use",
            content = new object[] { new { type = "tool_use", id = "tu_1", name = "Bash", input = new { command = "echo" } } },
            usage = new { input_tokens = 100, output_tokens = 50 }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, assistantToolUseLine + "\n");

    var toolResultLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:02Z",
        message = new
        {
            role = "user",
            content = new object[] { new { type = "tool_result", tool_use_id = "tu_1", content = "done" } }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, toolResultLine + "\n");

    // Running 应被 emit
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Running),
        TimeSpan.FromSeconds(3));

    // 末尾 = user tool_result，eligibleForIdleFallback=true，3s 兜底转 Idle
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Idle),
        TimeSpan.FromSeconds(6));
    var idleSeen = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (idleSeen) Pass("user tool_result tail: idle-tick correctly falls back to Idle after silence");
    else Fail("user tool_result tail: idle-tick failed to fall back (灯会永远卡蓝)");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: Ctrl+C interrupt marker tail flips to Idle immediately");
{
    // Claude Code 的 Ctrl+C 写入 user message 含 "[Request interrupted by user..."。
    // 用户希望中断当前回合即视为结束 → 灯立即变绿（不需要静默兜底）。
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--demo-int");
    Directory.CreateDirectory(projectsDir);
    var sessionId = "int-" + Guid.NewGuid().ToString("N")[..8];
    var transcriptPath = Path.Combine(projectsDir, sessionId + ".jsonl");

    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    await watcher.StartAsync();
    await Task.Delay(150);

    // user 文本输入（Claude 在思考）→ Running
    var userTextLine = JsonSerializer.Serialize(new
    {
        sessionId,
        cwd = "C:/demo-int",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "重构所有模块" }
    });
    await File.AppendAllTextAsync(transcriptPath, userTextLine + "\n");

    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Running),
        TimeSpan.FromSeconds(3));

    // 用户 Ctrl+C → Claude Code 写中断标记
    var interruptLine = JsonSerializer.Serialize(new
    {
        sessionId,
        timestamp = "2026-04-30T00:00:01Z",
        message = new
        {
            role = "user",
            content = new object[] { new { type = "text", text = "[Request interrupted by user]" } }
        }
    });
    await File.AppendAllTextAsync(transcriptPath, interruptLine + "\n");

    // 应**立即** emit Idle（不需要等 3s 静默兜底）
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Idle),
        TimeSpan.FromMilliseconds(800));

    var idleSeen = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (idleSeen) Pass("interrupt marker tail: watcher emits Idle immediately (no silence wait)");
    else Fail("interrupt marker tail: did NOT emit Idle within 800ms (灯不会及时变绿)");

    // 另一个变体 "for tool use"
    var sessionId2 = "int2-" + Guid.NewGuid().ToString("N")[..8];
    var path2 = Path.Combine(projectsDir, sessionId2 + ".jsonl");
    var seedLine = JsonSerializer.Serialize(new
    {
        sessionId = sessionId2,
        cwd = "C:/demo-int",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "do tool" }
    });
    var interruptToolLine = JsonSerializer.Serialize(new
    {
        sessionId = sessionId2,
        timestamp = "2026-04-30T00:00:01Z",
        message = new
        {
            role = "user",
            content = new object[] { new { type = "text", text = "[Request interrupted by user for tool use]" } }
        }
    });
    await File.AppendAllTextAsync(path2, seedLine + "\n");
    await File.AppendAllTextAsync(path2, interruptToolLine + "\n");

    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId2 && a.Phase == SessionPhase.Idle),
        TimeSpan.FromMilliseconds(800));
    var idleSeen2 = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId2 && a.Phase == SessionPhase.Idle);
    if (idleSeen2) Pass("interrupt-for-tool-use variant also emits Idle");
    else Fail("interrupt-for-tool-use variant did NOT emit Idle");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: user-message tail keeps Running across long silence (Lollygagging case)");
{
    // Claude API 调用进行中（"Lollygagging"）期间 transcript 完全静默 ——
    // 但末尾消息是用户输入，Claude 实际在思考。watcher 必须 *不能* 因静默就翻 Idle。
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--demo-think");
    Directory.CreateDirectory(projectsDir);
    var sessionId = "think-" + Guid.NewGuid().ToString("N")[..8];
    var transcriptPath = Path.Combine(projectsDir, sessionId + ".jsonl");

    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    await watcher.StartAsync();
    await Task.Delay(150);

    // 仅写一条 user message —— 模拟用户刚问，Claude 还没回（API 调用进行中）
    var userLine = JsonSerializer.Serialize(new
    {
        sessionId,
        cwd = "C:/demo-think",
        timestamp = "2026-04-30T00:00:00Z",
        message = new { role = "user", content = "请帮我重构这段代码" }
    });
    await File.AppendAllTextAsync(transcriptPath, userLine + "\n");

    // 应该 emit Running
    await WaitFor(() => events.Any(e => e is SessionActivityUpdated a && a.SessionId == sessionId && a.Phase == SessionPhase.Running),
        TimeSpan.FromSeconds(3));
    var sawRunning = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Running);
    if (sawRunning) Pass("user-tail watcher emits Running");
    else Fail("expected Running emit on user tail");

    // 静默 5 秒（>3s 阈值 + 几次 tick），idle-tick 不应该兜底转 Idle
    await Task.Delay(5000);
    var idleEmitted = events.OfType<SessionActivityUpdated>().Any(a => a.SessionId == sessionId && a.Phase == SessionPhase.Idle);
    if (!idleEmitted) Pass("user-tail stays Running across 5s silence (idle-tick must NOT fallback)");
    else Fail("user-tail bug: idle-tick wrongly fell back to Idle while Claude is still 'thinking'");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

// ----------------------------------------------------------------------------
Section("ClaudeTranscriptWatcher: initial scan phase by mtime (stale=Idle, fresh=Running)");
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "OpenIslandSmoke_" + Guid.NewGuid().ToString("N")[..8]);
    var projectsDir = Path.Combine(tempRoot, "projects", "C--init");
    Directory.CreateDirectory(projectsDir);

    var staleSessionId = "stale-" + Guid.NewGuid().ToString("N")[..8];
    var freshSessionId = "fresh-" + Guid.NewGuid().ToString("N")[..8];
    var stalePath = Path.Combine(projectsDir, staleSessionId + ".jsonl");
    var freshPath = Path.Combine(projectsDir, freshSessionId + ".jsonl");

    string LineFor(string sid) => JsonSerializer.Serialize(new
    {
        sessionId = sid,
        cwd = "C:/init",
        timestamp = "2026-04-29T00:00:00Z",
        message = new { role = "user", content = "seed" }
    });
    await File.WriteAllTextAsync(stalePath, LineFor(staleSessionId) + "\n");
    await File.WriteAllTextAsync(freshPath, LineFor(freshSessionId) + "\n");

    // 把 stale 的 mtime 设为 1 小时前；fresh 保留刚才写入的现时 mtime
    File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-1));

    var customDiscovery = new ClaudeTranscriptDiscovery(tempRoot);
    using var watcher = new ClaudeTranscriptWatcher(customDiscovery);
    var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
    watcher.EventEmitted += (_, ev) => events.Enqueue(ev);

    await watcher.StartAsync();
    await Task.Delay(400); // 让全量扫描+所有 SessionStarted emit 完成

    var startedEvents = events.OfType<SessionStarted>().ToArray();
    var staleStarted = startedEvents.FirstOrDefault(s => s.SessionId == staleSessionId);
    var freshStarted = startedEvents.FirstOrDefault(s => s.SessionId == freshSessionId);

    if (staleStarted == null) Fail($"stale session not seen in initial scan (got {startedEvents.Length} SessionStarted)");
    else if (staleStarted.InitialPhase == SessionPhase.Idle) Pass("stale transcript starts in Idle phase (mtime > 1.5s ago)");
    else Fail($"stale transcript started in {staleStarted.InitialPhase}, expected Idle");

    if (freshStarted == null) Fail("fresh session not seen in initial scan");
    else if (freshStarted.InitialPhase == SessionPhase.Running) Pass("fresh transcript starts in Running phase (mtime within 1.5s)");
    else Fail($"fresh transcript started in {freshStarted.InitialPhase}, expected Running");

    watcher.Stop();
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

// ----------------------------------------------------------------------------
Section("PreToolUse 链路: hook 子进程 → BridgeServer → ResolvePermission → directive 回包");
{
    // case A: BridgeServer 没启动 → ConnectAsync 短时间 fail
    {
        var pipeName = "OpenIslandSmoke_NS_" + Guid.NewGuid().ToString("N")[..8];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var client = new OpenIsland.Core.Bridge.BridgeCommandClient(TimeSpan.FromMilliseconds(500), pipeName);
        var connected = await client.ConnectAsync(new CancellationTokenSource(500).Token);
        sw.Stop();
        if (!connected && sw.ElapsedMilliseconds < 1500) Pass($"no-server: ConnectAsync fails fast ({sw.ElapsedMilliseconds}ms)");
        else Fail($"no-server: ConnectAsync hung or succeeded unexpectedly (connected={connected}, elapsed={sw.ElapsedMilliseconds}ms)");
    }

    // case B: server 在但不发 directive → await 在超时内返回 null
    {
        var pipeName = "OpenIslandSmoke_TO_" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new OpenIsland.Core.Bridge.BridgeServer(pipeName);
        await server.StartAsync();
        await using var client = new OpenIsland.Core.Bridge.BridgeCommandClient(TimeSpan.FromSeconds(5), pipeName);
        var connected = await client.ConnectAsync();
        if (!connected) { Fail("case B: ConnectAsync failed"); }
        else
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var ctsTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var directive = await client.SendHookAndAwaitDirectiveAsync(
                "claude",
                JsonDocument.Parse("""{"hook_event_name":"pre_tool_use","session_id":"timeout-sid","tool_name":"Bash"}""").RootElement,
                "timeout-sid",
                ctsTimeout.Token);
            sw.Stop();
            if (directive == null && sw.ElapsedMilliseconds < 1500)
                Pass($"no-directive: SendHookAndAwaitDirectiveAsync returns null on cancellation ({sw.ElapsedMilliseconds}ms)");
            else
                Fail($"no-directive: did not return null on timeout (directive={directive?.GetRawText() ?? "null"}, elapsed={sw.ElapsedMilliseconds}ms)");
        }
    }

    // case C: server 收到 hook → 立刻发 approve directive → client 收到 approve=true
    {
        var pipeName = "OpenIslandSmoke_OK_" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new OpenIsland.Core.Bridge.BridgeServer(pipeName);
        var sessionId = "approve-sid";
        // server 端模拟 SessionManager 的"收到 PermissionRequested → 用户点 Allow → 回包"
        server.MessageReceived += async (_, e) =>
        {
            if (e.Message is OpenIsland.Core.Bridge.ProcessHookMessage)
            {
                var directive = JsonSerializer.SerializeToElement(new { permission_request = new { approve = true } });
                await server.SendToClientAsync(e.ClientId, new OpenIsland.Core.Bridge.HookDirectiveMessage
                {
                    SessionId = sessionId,
                    Directive = directive
                });
            }
        };
        await server.StartAsync();

        await using var client = new OpenIsland.Core.Bridge.BridgeCommandClient(TimeSpan.FromSeconds(5), pipeName);
        await client.ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var directive = await client.SendHookAndAwaitDirectiveAsync(
            "claude",
            JsonDocument.Parse($$"""{"hook_event_name":"pre_tool_use","session_id":"{{sessionId}}","tool_name":"Bash"}""").RootElement,
            sessionId,
            cts.Token);

        if (directive is JsonElement d
            && d.TryGetProperty("permission_request", out var perm)
            && perm.TryGetProperty("approve", out var approve)
            && approve.GetBoolean() == true)
            Pass("approve path: client receives approve=true directive");
        else
            Fail($"approve path: bad directive (got {directive?.GetRawText() ?? "null"})");
    }

    // case D: server 发 deny directive → client 收到 approve=false
    {
        var pipeName = "OpenIslandSmoke_NO_" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new OpenIsland.Core.Bridge.BridgeServer(pipeName);
        var sessionId = "deny-sid";
        server.MessageReceived += async (_, e) =>
        {
            if (e.Message is OpenIsland.Core.Bridge.ProcessHookMessage)
            {
                var directive = JsonSerializer.SerializeToElement(new { permission_request = new { approve = false } });
                await server.SendToClientAsync(e.ClientId, new OpenIsland.Core.Bridge.HookDirectiveMessage
                {
                    SessionId = sessionId,
                    Directive = directive
                });
            }
        };
        await server.StartAsync();

        await using var client = new OpenIsland.Core.Bridge.BridgeCommandClient(TimeSpan.FromSeconds(5), pipeName);
        await client.ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var directive = await client.SendHookAndAwaitDirectiveAsync(
            "claude",
            JsonDocument.Parse($$"""{"hook_event_name":"pre_tool_use","session_id":"{{sessionId}}","tool_name":"Edit"}""").RootElement,
            sessionId,
            cts.Token);

        if (directive is JsonElement d
            && d.TryGetProperty("permission_request", out var perm)
            && perm.TryGetProperty("approve", out var approve)
            && approve.GetBoolean() == false)
            Pass("deny path: client receives approve=false directive");
        else
            Fail($"deny path: bad directive (got {directive?.GetRawText() ?? "null"})");
    }
}

// ----------------------------------------------------------------------------
Section("Result");
if (failures == 0) { Console.WriteLine("ALL PASS"); return 0; }
Console.WriteLine($"{failures} FAILURE(S)");
return 1;

static string Truncate(string? s, int n) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        if (condition()) return;
        await Task.Delay(50);
    }
}
