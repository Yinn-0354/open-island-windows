# Changelog

All notable changes to Open Island will be documented here.

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [Unreleased]

### Added

- **网页功能栏** —— 每张会话卡输入框上方新增功能栏：模型下拉（全系 Claude 档，选中即注入 `/model` 实时切换）；输入 `/` 弹出命令自动补全面板（如 `/r` 列出 resume/review/rewind，点击选用，与 Claude Code 客户端一致）；页面右上角日/夜主题切换（localStorage 记住）
- **hook PID 绑定** —— hook 子进程沿父链上报 claude.exe 祖先 PID（payload 注入 `open_island_ppid`），岛端维护 session↔进程绑定：网页回复/快捷回复/切模型注入时**最优先**用绑定精确定位终端，彻底解决"同目录多会话"定位歧义；resume 换进程后下一个 hook 事件自动刷新
- **会话来源筛选** —— 命令栏新增三态按钮：全部 → 仅终端(CLI) → 仅客户端(Claude Desktop) 循环切换（依据 `ClaudeMetadata.Entrypoint`），筛选生效时按钮高亮并切换对应字形，悬停提示当前模式

### Fixed

- **网页发送失败（no-terminal-match）** —— 同 cwd 多会话时注入定位歧义被安全拒绝导致网页无法回复。三层修复：① hook PID 绑定（上）；② 绑定缺失时按"终端窗口标题 ↔ 会话标题"做唯一匹配兜底；③ 仍无法定位时网页显示人话原因（不再是裸代码）
- **新会话卡片不出现 / 显示卡死** —— ClaudeTranscriptWatcher 完全依赖 FileSystemWatcher，多会话狂写大 jsonl 时 64KB 缓冲溢出丢事件（OnError 只能记日志），且个别机器 FSW 根本不投递事件（实测），而旧的"5s 补扫"迁移时已移除 → 丢失的更新无人兜底。新增 3s mtime 轮询兜底，走与 FSW 相同的去抖处理路径（实测合成新转录 8s 内上卡）
- **发送提示"剪贴板被占用"** —— 注入写剪贴板从 SetText（带 OleFlushClipboard，CLIPBRD_E_CANT_OPEN 高发）改为 SetDataObject(copy:false) 并把重试窗口拉长到 ~1.2s，骑过剪贴板历史/微信/输入法的瞬间抢占；还原剪贴板也加重试
- **网页发送提示"没能把终端切到前台"** —— Windows 前台锁：SetForegroundWindow 的授权跟着"最近接收输入的线程"走，用户在浏览器/手机网页发起时由浏览器持有，旧代码 AttachThreadInput 只挂目标窗口线程、不够解锁（系统化复现：桌面持有输入时 fg 纹丝不动 → foreground-mismatch）。修复：激活时同时挂上**当前前台窗口**的线程；同条件复现转为前台切换成功 → 粘贴 → 发送 ok

- **安装 Skill** —— 模型命令栏新增"安装 Skill"按钮：粘贴 `claude plugin` 命令（支持连写 / `&&` / 换行）或 `owner/repo` 简写，后台 PowerShell 静默调用 claude CLI 安装，面板内实时显示进度与 CLI 真实输出。严格白名单校验防命令注入；任一命令不合法整体拒绝，绝不部分执行
- **Apple 风毛玻璃** —— 设置中心新增"毛玻璃效果"开关 + 背景不透明度滑块（20-100%，250ms 去抖落盘），开关实时生效并持久化。自绘模糊方案：150ms 重采岛后方屏幕（截图瞬间用 WDA_EXCLUDEFROMCAPTURE 把岛自身剔出捕获，防反馈回路）→ 1/10 缩小即模糊 → 叠 tint 设为岛背景刷，被 Border 圆角天然裁剪 —— 胶囊形状/阴影完整保留，无 DWM accent 的矩形板伪影
- **网页同步（可对话）** —— 头部关机键左边新增地球按钮：点击在本机 18686 端口开启零依赖迷你 HTTP 服务（手动开关，绿色=开启），把 CLI 与 Claude 桌面端会话的标题/状态/最近消息同步到深色移动端友好网页，手机平板局域网即可访问；开启时访问地址自动复制到剪贴板。每张会话卡带回复输入框，POST /api/send 复用灵动岛"快捷回复"注入通道，手机上可直接对话
- **状态圆点同步到网页** —— 网页同步开启时，灵动岛展开后每张会话卡的状态圆点可点击：点击把该对话置顶同步到网页（排第一 + ⭐ 标识 + 60 条完整历史，圆点带橙色外圈），再点一次取消
- **settings.json 热重载** —— 外部编辑配置文件 2 秒内自动生效（mtime 轮询），毛玻璃/快捷键等订阅项无需重启

## [0.4.2] - 2026-06-04

### Fixed

- **Claude 订阅 5h 余量在只用 Claude Desktop 的电脑上一直显示 "余 --"** —— 原因是 Claude Desktop 不把刷新后的 OAuth access token 写回 `~/.claude/.credentials.json`，导致 OpenIsland 探针长期用过期 token 调 `/api/oauth/usage` 一直 401。修法：探针检测到 token 过期或 401 时，自动调用本机 `claude` CLI 触发一次 OAuth 刷新（CLI 内置 refresh token 续命逻辑会把新 token 写回文件），刷新后立即重探一次 `/api/oauth/usage`；5 分钟节流，安全降级。装完即看到真实余额，不再需要用户手动开终端跑 `claude`

## [0.4.1] - 2026-05-31

### Changed

- **"工作中"动画改为忙碌敲键盘** —— 头部 Running 精灵从"戴眼镜思考"改成在键盘上敲字；去掉连接两眼的镜框，改为两只独立眼睛**一大一小、交替眨眼**
- **README 配图更新** —— 首图与 Features 说明图换成新的灵动岛截图（橙色 Claude 宠物 + 截图按钮 + 七天柱状图等），精灵说明图同步换成"敲键盘 / 喝可乐"

## [0.4.0] - 2026-05-31

### Added

- **区域截图** —— "清理任务"旁新增截图按钮，外加全局快捷键（默认 **Ctrl+Q**，控制中心可录制更改）。微信式逻辑：拖拽框选一个矩形，松手自动裁剪并复制到剪贴板（同时写 Bitmap/DIB 与 PNG 两种格式，粘贴兼容性好），Esc / 右键取消
- **最近七天 token 用量柱状图** —— 点击 5 小时余额行翻成七天用量柱状图（按天聚合 token，用量越多绿色越深越高，右侧只显示总量），再点切回余额。默认余额；切换状态持久化，下次启动灵动岛恢复关闭时的状态
- **全新橙色 Claude 小宠物表情** —— 头部精灵换成橙色 Claude 路障小宠物，并为各状态/交互配了动画：工作中戴眼镜思考、需关注头顶问号、完成放烟花、空闲随机（眨眼 / wink / 睡觉吐泡泡 / 喝可乐，每 3 分钟随机切换）、媒体控制戴耳机、点击彩蛋（龟派气功）、关闭挥手拜拜
- **圆形关机键关闭按钮** —— "Open Island" 头部右侧新增圆形电源键，点击挥手告别后隐藏灵动岛；托盘菜单"显示灵动岛"可再次叫出

### Changed

- **空闲动画改为连续循环 + 每 3 分钟随机切换**（原为停帧 + 每 30s 播一遍）；多 Agent 专属动画暂时停用，Running 一律播正常动画

## [0.3.0] - 2026-05-30

### Added

- **订阅 5 小时余量** —— 灵动岛音量栏下方显示 Claude 订阅"5 小时滚动窗口"剩余额度（绿色余额条 + "余 XX%" + 重置倒计时）。数据来自 `/api/oauth/usage`（与 `/usage` 同源，零 token 开销，经系统自带 `curl.exe` 取数以绕开部分环境下 .NET HttpClient 挂死），5 分钟自动刷新，行尾刷新按钮可手动立即刷新
- **全局模型切换** —— 音量栏下方"切换模型"按钮，点开弹出列表即切换。控制中心可添加第三方模型（参考 cc-switch 预设：DeepSeek / 智谱 GLM / Kimi / 通义千问 / OpenRouter / 硅基流动 / Novita / ModelScope / 小米 MiMo 等，预填地址、只需填 API Key）。官方 Claude 档客户端 + CLI 都生效；第三方档写 `~/.claude/settings.json` 的 env、对新 CLI 会话生效。API Key 以 Windows DPAPI 加密落盘
- **中英文界面切换** —— 托盘右键 / 控制中心切换 中文 / English，默认跟随 Windows 系统语言，切换后持久化
- **图钉固定会话** —— 会话卡右侧图钉按钮，被固定的会话"清理任务"不会清掉它
- **点击 CPU% / RAM% 释放内存** —— 清理各进程工作集（类 RAM 清理工具），RAM% 随后下降
- **灵动岛微动画** —— 卡片入场、状态点变色 / 脉冲、按压缩放、Running 时"思考中"三点脉冲等（只动 Opacity / RenderTransform，省 GPU、不触发布局）

### Fixed

- **快捷回复 / 点击卡片跳转发错宿主、找不到终端** —— Claude 桌面端常驻时有十几个 `claude.exe`（桌面主进程 + 大量派生子进程），旧逻辑在其中按 cwd 匹配歧义、单候选兜底失效 → CLI 会话的消息弹到了客户端、点卡片退化成开新终端 `claude --resume`。修复：① entrypoint 取转录文件**最新**一行的值（会话 desktop→`--resume`→cli 后被正确判为 cli）；② 终端解析只在"终端宿主"的 claude 里找（父进程是 shell / 终端，排除桌面端及其子进程）
- **收起 / 展开灵动岛把任务弄没** —— 收起再展开不再清空任务卡；清理改由"清理任务"按钮显式触发，正在运行的会话也不会被清
- **命令注入 / UTC 时间偏移 / hook settings 覆盖** —— `session_id` 严格校验防命令注入；transcript 时间戳按本地时区解析；安装 hook 改为**合并**写入，不再整体覆盖用户 `~/.claude/settings.json`
- **若干健壮性** —— 剪贴板注入失败重试、退出时 `Application.Current` 为 null 崩溃、`settings.json` 并发写竞态、动画时钟泄漏等

### Changed

- **模型切换移到全局岛栏** —— 每会话单独切模型不可行，改为音量栏下方一栏全局切换
- **快捷回复暂时取消** —— 功能未完善，暂时隐藏卡片上的回复图标与输入框（代码保留），后续更新再加入

## [0.2.2] - 2026-05-17

### Added

- **提示音** —— 会话从 Running → Idle/Completed（任务完成）以及进入需关注状态（橙色权限 / 红色待答）时各响一声，沿状态边缘触发；系统状态栏新增喇叭开关，静音状态持久化
- **每会话快捷模式按钮** —— 每张会话卡新增小图标按钮（accept edits / auto / plan，hover 显示英文），一键切该 Claude 会话的权限模式
- **点头部一键清空会话列表** —— 点击 "Open Island" 头部清空当前会话列表；会话下次活动时自动重现

### Fixed

- **GPU 利用率在非英文 Windows 不显示（"GPU --"）** —— 改用 PDH 英文计数器 API（`PdhAddEnglishCounterW`）读取 GPU Engine "Utilization Percentage"，与系统语言无关，非英文 Windows 也显示真实 %
- **状态栏数值抖位** —— CPU / RAM / GPU 不再随网速文本宽度变化而左右抖动（状态栏列宽固定）

### Changed

- **系统状态栏增加提示音开关列** —— 状态栏布局新增喇叭静音/取消静音按钮
- **会话卡操作区在 × 前新增三个模式按钮** —— accept edits / auto / plan，紧挨临时收起的 × 之前

## [0.2.1] - 2026-05-16

### Fixed

- **Claude Desktop 桌面端权限按钮无反应** —— 桌面端会话点灵动岛权限按钮无反应（之前只对终端有效）：按 entrypoint 分流，claude-desktop 经 UI Automation 点 Claude Desktop 弹窗里的真实按钮（实测真名 `Allow once Ctrl+Enter` / `Deny`，前缀匹配；SetFocus 后 Invoke）

### Changed

- **桌面端权限卡按钮文案镜像 Claude Desktop** —— 桌面端会话灵动岛权限卡按钮文案严格镜像 Claude Desktop（`Allow once` / `Deny`，2 键），不再套终端的 1/2/3 模板；底部提示相应调整

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

[Unreleased]: https://github.com/ludiwangfpga/open-island-windows/compare/v0.4.2...HEAD
[0.4.2]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.4.2
[0.2.2]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.2.2
[0.2.1]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.2.1
[0.2.0]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.2.0
[0.1.0]: https://github.com/ludiwangfpga/open-island-windows/releases/tag/v0.1.0
