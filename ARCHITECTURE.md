# Architecture

Open Island 是一个常驻托盘的 WPF 应用，把 AI 编码代理（主要是 Claude Code）的运行状态汇聚到一个 macOS 风格的灵动岛上。本文给读者一个能改代码、能 review PR 的全局心智模型。

> 想动手改代码看 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 整体数据流

```
AI agent (Claude Code / Codex / Cursor / Gemini)
   │ stdin JSON
   ▼
open-island-hooks.exe   (per-event subprocess; --source <agent>)
   │ Named Pipe "OpenIsland_Pipe"
   ▼
BridgeServer  ──► SessionManager ──► SessionState (event-sourced)
   │                                    │
   │                                    ▼
   │                               SessionRegistry (disk persistence)
   ▼
DynamicIslandWindow / MainWindow / PopupWindow  (WPF, MVVM)
```

## 关键不变量

读 / 改这块代码前必须知道的事：

### 1. Hook 入口必须 fail-open
`OpenIsland.Hooks/Program.cs` 从 stdin 读 JSON、经命名管道转发、**永远** 返回 0 —— 不论解析错、超时、主应用没运行。Hook 不能阻塞或失败 agent。这条契约要保住。

### 2. 两套不同的 timeout
普通 hook 走 `--timeout`（默认 45s）。**交互事件**（`permission_request` / `question`）由 `IsInteractiveHook` 识别，超时延到 **24 小时** —— 它们要等用户。两套不能合并。

### 3. Event sourcing
所有 session 状态变更走 `SessionState.Apply(AgentEvent)`，返回新不可变 `SessionState`。新加事件类型要改三处：
- `OpenIsland.Core/Models/AgentEvent.cs` —— 加 record
- `SessionState.Apply` —— 加 switch case
- `SessionManager.GetSessionIdFromEvent` —— 加映射

漏改任何一处，心跳/持久化会静默失败。

### 4. Hook payload → AgentEvent 转换分 agent
- `OpenIsland.Core/Hooks/{Claude,Codex,Cursor,Gemini}Hooks.cs` 各管自己 agent 的 payload 解析
- `SessionManager.ProcessHookEventAsync` 按 `source` 字符串分发
- "Claude 一族" 的 6 个 source（`claude` / `qoder` / `qwen` / `factory` / `codebuddy` / `kimi`）共用 Claude 解析器 —— 加新 agent 必须三处同步：`SessionManager` / `OpenIsland.Hooks/Program.cs:IsInteractiveHook` / `OpenIsland.Setup/Program.cs:GetInstallersForAgent`

### 5. 命名管道用固定名 `OpenIsland_Pipe`
**不要** 改回 per-user 名字。中文/非 ASCII Windows 用户名会让 per-user 命名管道在不同进程间认错对象。详见 `BridgeServer.GetDefaultPipeName`。

### 6. 三个轮询 timer 在 SessionManager
驱动数据新鲜度：
- 1 分钟 `_cleanupTimer` 清理 >5 分钟的过期心跳
- 5 秒 `_scanTimer` 重扫 `~/.claude/projects/**/*.jsonl`，靠 `ClaudeTranscriptDiscovery`；用 `Interlocked` 守护防并发
- 2 秒 `_processSyncTimer` 把 `IsProcessAlive` 跟 `ProcessMonitorService`（用 `System.Management/WMI`）对齐

`IsSessionProcessRunning` 里 session-match 的尝试顺序很重要：JumpTarget 工作目录 > transcript 路径 > session ID > 标题兜底。

### 7. Hooks 安装写在 `%USERPROFILE%\.claude\settings.json`
`ClaudeHookInstaller.InstallAsync` 重写 `hooks` 字段，注册 `Stop` / `PreToolUse` / `PostToolUse` 三个事件，备份旧文件，并在 `~/.claude/open-island-manifest.<source>.json` 写一份 manifest。

`IsInstalledAsync` 判定**仅看 manifest** —— 删 manifest 即可重新触发自动安装。

### 8. Stop hook 是任务完成的权威信号
Watcher 默认只 emit `Running`，不靠 `stop_reason` 推断 Idle（之前在 multi-step 中段 `end_turn` 会误判完成）。Claude Code 在每轮 assistant 真 `end_turn / stop_sequence` 时触发 Stop hook，`SessionManager` 的 `eventName == "stop"` 分支直接 flip session 到 Idle 并发 `TaskCompleted`。

唯一例外：`[Request interrupted]` 标记（用户 Ctrl+C），watcher 这种情况会 emit Idle 兜底，因为 Claude Code 不发 Stop hook。

### 9. WPF DI 在 `App.xaml.cs:ConfigureServices`
所有服务 / ViewModel / Window 都在那里手动注册。没有 convention-based 扫描。加新服务记得也加。

### 10. Stop-event 提示音独立播放
`OpenIsland.Hooks/Program.cs:PlayBeep` 走真降级链（Asterisk → SimpleBeep → Console.Beep），保证 hook 子进程一定有声响 —— 主应用的 BeepService 已撤掉对 TaskCompleted 的订阅，避免双响。

## 模块划分

```
OpenIsland.Core/    # 共享类库 (net8.0)
  Bridge/             # 命名管道协议（BridgeServer / BridgeCommandClient）
  Hooks/              # 每个 agent 的 hook payload 解析 + installer
    HookInstallers/
      ClaudeHookInstaller.cs
  Models/             # 事件 / Session / SessionState（event sourcing 核心）
  Registry/           # SessionRegistry（持久化到 ~/.openisland/sessions.json）
  ClaudeTranscriptDiscovery.cs   # ~/.claude/projects/ 扫描器
  ClaudeTranscriptWatcher.cs     # FileSystemWatcher + 5s tick

OpenIsland.Hooks/   # open-island-hooks.exe — 由 agent 调用的 stdin → 桥转发器
  Program.cs

OpenIsland.Setup/   # open-island-setup.exe — install/uninstall/status 子命令
  Program.cs

OpenIsland.App/     # OpenIsland.exe (net8.0-windows WPF)
  App.xaml.cs           # DI 配置 + 启动期 hooks 自动安装
  Services/
    BridgeServer 桥接器
    SessionManager        # 中央协调器，时序 + 状态机
    ProcessMonitorService # WMI 拉 claude.exe 列表
    TerminalJumpService   # SendInput / SetForegroundWindow
    BeepService           # 任务完成提示音（已撤订阅，留作兜底）
    SoundService          # AttentionRequired 提示音
    WorkspaceSettings     # %APPDATA%\OpenIsland\settings.json 持久化
  ViewModels/
    DynamicIslandViewModel + IslandSessionItem
    MainViewModel + AgentSessionViewModel
    DashboardStats        # Overview/Models 计算（用 transcript mtime 作活跃时间）
    PopupViewModel
  Views/
    DynamicIslandWindow.xaml  # 灵动岛 + Notch 模式 + 权限面板
    MainWindow.xaml           # 控制中心（Sessions / Overview / Models tab）
    SettingsWindow.xaml       # 工作区目录管理
    PopupWindow.xaml          # 任务完成快速反馈
```

## 别动的东西

- `bin/` / `obj/` / `*_wpftmp.AssemblyInfo.cs` —— build artifacts
- `publish_output/`（如果还在仓库里）—— 已 checked-in 的 release zip 解压版，**不**编辑
- 中英双语注释 —— 中文行内注释是原作者留的关键 *为什么* 上下文，不要因为是中文就删
