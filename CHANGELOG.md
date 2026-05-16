# Changelog

All notable changes to Open Island will be documented here.

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [Unreleased]

## [0.2.0] - 2026-05-16

### Added

- **像素状态精灵** — 灵动岛 header 的状态点换成绑 SessionPhase 的像素动画（Aseprite sprite sheet，NearestNeighbor + 整数倍缩放，125%/150% DPI 不糊）：
  - Running → 连续循环 + 整体上下跳动
  - Idle / Completed → 平时停最后一帧，每 30s 从多个变体里随机挑一个完整播一遍（小动作 + 24/7 省电，定时器只在那 ~1.5s 跑）
  - 变体机制：`idle.png` / `idle2.png` / `idle3.png`… 自动发现，加图不改代码
- **系统状态栏** — 头部与会话列表之间一行 CPU / 内存 / GPU / 网速，1s 刷新（GetSystemTimes / GlobalMemoryStatusEx / GPU Engine 计数器 / NetworkInterface）
- **媒体控制栏** — 上一首 / 播放暂停 / 下一首（系统媒体键，网易云/Spotify/任意播放器通用）+ 系统音量滑块（CoreAudio IAudioEndpointVolume，双向同步）
- **会话卡片小叉号** — 临时收起某条 session；再次活动（新一轮 Running 或需关注 phase）自动重现，纯内存临时态
- **品牌图标** — face.ico 用作 exe / 任务栏 / 系统托盘 / 窗口图标（多尺寸，最近邻保持像素清晰）
- **DESIGN.md** — 引入 Apple 设计语言文档（来自 awesome-design-md）供后续 UI 参考

### Fixed

- **终端激活鲁棒性** — WMI 父链查询换成 CreateToolhelp32Snapshot（某些环境 WMI 卡死 30s+ 导致点会话卡片无响应/开新终端）；三层兜底：父链 → AttachConsole conhost → 窗口标题打分；处理 `claude.exe.old.<ts>` 自更新改名
- **复用现有 WT** — `claude --resume` 用 `wt -w 0 new-tab` 在现有窗口开新 tab，不再每次弹独立窗口
- **桌面端 session 状态** — Claude Desktop 不跑用户 hook，Stop hook 永不触发导致一轮结束后永远卡蓝灯；watcher 改为对 desktop session 用末条 assistant 的 `stop_reason ∈ {end_turn, stop_sequence}` 转 Idle（绿）
- **桌面 session 跳转** — entrypoint 优先分流；ActivateClaudeDesktopWindow 按窗口面积选真 UI（避开 offscreen Electron 任务栏代理 helper）、SW_SHOWNORMAL 恢复隐藏窗口、UIA SetFocus+Invoke 在侧边栏精确切到对应会话
- **ClaudeMetadata.Entrypoint 丢失** — 扫描 tick 重建 metadata 时漏填 Entrypoint，被 ApplyClaudeMetadataUpdated 整体替换清空，导致桌面 session 路由失效

## [0.1.0] - 2025-04-30

第一个公开版本。

### Added

- **Dynamic Island UI** — 屏顶悬浮的活跃 AI 会话指示器，按工具图标 + 项目名 + 状态点显示，鼠标点击展开会话列表
- **Permission mirror** — Claude Code 的 PreToolUse 权限询问镜像到岛上，三按钮（Yes / Yes don't ask again / No）通过 `SendInput` 注入对应数字到 Claude 终端
- **AskUserQuestion 解码** — Claude 工具 `AskUserQuestion` 的 JSON `questions` 数组按"Q + 编号选项 + 描述"格式化显示，不再喷转义 Unicode
- **Notch snap mode** — 拖动岛体距屏顶 28px 内自动吸附为 macOS 刘海形态；下拉超过 48px 自动恢复
- **Control Center** —— 三 Tab 结构：
  - Sessions：所有 Claude 会话列表（按 transcript mtime 排序）
  - Overview：Token 总量 / Active days / Current/Longest streak / Peak hour / Favorite model + 84 天活动热力图
  - Models：按模型分组的 Token 占比 + Input/Output 详情，百分比加和=100%
- **Workspace 过滤** — 设置里指定项目根目录，统计仅算 `cwd` 在该目录下的会话
- **Stop hook 任务完成判定** — Claude Code 真正 `end_turn` 时触发桌面响铃 + 灵动岛绿灯闪
- **CLI / 桌面端区分跳转** — 跳转按钮自动检测 transcript 的 `entrypoint` 字段：
  - `cli` → 开新终端跑 `claude --resume {sessionId}`
  - `claude-desktop` → 激活 Claude Desktop 客户端窗口（两遍兜底找窗口：进程名匹配 + 标题含 "Claude"）
- **Stats 时间窗** — All / 30d / 7d 三档时间范围切换
- **Hooks auto-install** — 启动时自动注册 `PreToolUse` / `PostToolUse` / `Stop` 三个 Claude Code hook
- **Apple-flat UI** — 控制中心 / 灵动岛权限面板配色统一深色 macOS 风（`#1C1C1E` 背景 / `#0A84FF` 主色 / 系统标题栏跟随暗色主题）
- **Smoke test** — `tests/OpenIsland.SmokeTest`，跑活的 `~/.claude/projects/` 数据回归测试

### Fixed

- Hooks 子进程读 stdin 强制 UTF-8（中文 Windows 默认 GBK 会让中文 prompt 乱码）
- `AgentSession.With(...)` 不再把 `PermissionRequest` / `QuestionPrompt` 默认清空（之前 watcher tick 会刷掉橙卡内容）
- DataTrigger 在权限面板的 `DataContext` 嵌套覆盖时正确响应 `IsPermissionMode`（Visibility 不再永远 Visible）
- DashboardStats 用 transcript 文件 LastWriteTime 作活跃时间（不再因 watcher 启动扫描把 ActiveDays 坍塌到 1 天）
- WPF Window 属性动画用 `FillBehavior.Stop` + Completed 写本地值（之前 HoldEnd 锁住 Top/Left 让 Notch 吸附后无法拖动）
- 任务完成提示音单声 Asterisk（之前 hook 子进程 + 主进程 BeepService 双触发 + PlayBeep 串行 3 声）

### Changed

- Watcher 不再靠 `stop_reason` 推断 Idle，全权交给 Stop hook（之前 multi-step 中段 `end_turn` 误判任务完成的 bug 修复）
- Permission 面板按钮配色从 3 色改为 Apple 风 2 色（白底深字 = 主，深底浅字 = 次）
- Token 百分比统一口径（分子分母都含 cache token，加和恒等于 100%）

[Unreleased]: https://github.com/ludiwangfpga/open-island-windows/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.1.0
