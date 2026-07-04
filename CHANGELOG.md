# Changelog

All notable changes to Open Island will be documented here.

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [0.6.0] - 2026-07-04

### Added

- **音乐 Clauding —— 液态玻璃胶囊随专辑封面实时变色** —— 现在播放模块（SMTC）读取当前曲目的封面缩略图，用 `AlbumPaletteExtractor` 提取主色三元组；胶囊皮肤的液态玻璃折射（离屏 WebView2 渲染 `glass.html`，SVG feDisplacementMap 色散 + backdrop-filter 磨砂 + 渐变高光边框）与底部 `WaveVisual` 三层贝塞尔波浪跟着当前专辑配色走。波浪振幅来自 NAudio WASAPI loopback 的实时响度包络（`AudioLevelReactor`），随音乐起伏。
- **代码审阅卡片（Edit / Write / MultiEdit）** —— Claude 要改文件时，权限卡不再只显示一个文件路径，而是在岛上直接渲染**行级红绿 diff**（红底删除 / 绿底新增 / 灰色上下文 + 真实行号），像 GitHub PR 一样一目了然。`DiffBuilder` 读磁盘旧内容做行级对齐：Edit/MultiEdit 定位改动区间只对附近做 LCS + 前后 3 行上下文，Write 整篇覆写抽取「改动附近 hunk」（长段未变行折叠，改动一定可见）。批准/拒绝走既有 Allow once / Always / Deny 通路不变。超大改动截断保护、CRLF 兼容、新建文件整块绿显示。
- **Plan 审阅卡片（ExitPlanMode）** —— Claude 退出计划模式请求批准时，岛上完整渲染计划的 **Markdown**（标题分级 / 列表 / 代码块 / 粗体，深色主题适配），而不是原始 JSON。选项按钮按宿主镜像：终端会话给 `1/2/3` 编号菜单，Claude Desktop 会话给 Accept / Accept and auto mode / Reject / Revise… 真实按钮；反馈输入框可直接写想法回给 Claude 继续规划。

### Changed

- **液态玻璃改用离屏 WebView2 渲染** —— 折射效果从原生 CPU 折射算法换成一个常驻屏幕外的 WebView2 渲染 `glass.html`，每帧把桌面截图喂进去、`CapturePreviewAsync` 读回当胶囊背景。关掉 Chromium 的屏幕外节流、PNG 编解码挪线程池，避免拖动时掉帧。

### Fixed

- **代码审阅 diff 的一批边界修复** —— Write 覆写大文件、改动在后半段时不再错显 `+0 −0`（改成「改动附近抽 hunk」渲染 + 全量统计，改动一定可见）；CRLF 文件上下文行不再因残留 `\r` 被 WPF 渲染成双倍行高；`CodeReviewDiff` 按请求缓存，避免每次刷新重复读盘 + LCS 以及头部统计与内容取自不同文件快照的矛盾。
- **`WaveVisual` 常驻满帧空转** —— 渲染循环挂钩从「随控件加载」改为「随可见性」，波形隐藏 / 关闭时退订，不再从启动到关机每帧空转。
- **`deploy.ps1` 目标框架目录** —— 对齐 csproj 改动后的 `net8.0-windows10.0.19041.0`，避免脚本把 hooks 塞进旧目录并启动上一版。

## [0.5.1] - 2026-06-18

### Added

- **在线升级** —— 命令栏「检查更新」按钮 + 启动后台静默检查；发现新版弹提示卡（版本号 + 更新日志 + 一键更新）。自动下载 GitHub 最新 `Setup.exe` 静默安装（复用 Inno 安装包：`CloseApplications=force` 关旧版 → 覆盖 → `RestartApplications=yes` 自动重启）。下载走 GitHub 原生 + 多镜像兜底（ghproxy 等），用 `MZ` 魔数 + 体积校验防镜像伪造响应；语义化版本比较。

### Fixed

- **网页回复 Claude Desktop 会话一直「发送中」最后失败** —— 原强制 UIA 侧边栏导航在长标题/截断时匹配不上，卡 ~3 分钟后 `session-nav-failed`。改为「回复客户端当前打开的会话」为可靠主路径：不导航、直接激活窗口 + 焦点校验（实测聊天框 `ControlType=Document`）+ 粘贴 + 回车，实测端到端 < 0.7s。**CLI 注入路径完全不变。**
- **安装 Skill 永久卡在「安装中」** —— stdout/stderr 读取无超时，claude CLI spawn 的 git/node 孙进程持有继承的管道写端句柄即永不返回。改为读取也受超时/取消约束（`WaitAsync`）、加全局总超时与「取消」按钮，任何情况都能返回、复位状态。

### Security

- **网页同步 18686 接口鉴权加固** —— 首启生成 32-hex 随机访问令牌（持久化，扫码/复制地址带 `?t=`）；所有 `/api/*`（GET / POST / SSE）校验 `X-OI-Token`，缺失或错误一律 403，静态页放行。局域网监听 `0.0.0.0` 全靠它挡未授权访问。
- **CSRF / DNS-rebinding 防护** —— 所有 POST 校验 Host 头白名单（挡 DNS rebinding）+ 拒绝跨站 Origin / Referer，OPTIONS 不回通配 CORS。
- **抗 DoS** —— 连接并发上限信号量（超限 503 / 429）、整请求时长预算、SSE 连接上限；Content-Length 重复 / 冲突 / 越界一律 400。

### Changed

- `installer/openisland.iss`：`RestartApplications=no` → `yes`，静默更新装完自动重启。

## [0.5.0] - 2026-06-12

### Added

- **网页同步 2.0：手机上的完整工作台** —— 头部地球按钮开启本机 18686 端口零依赖迷你 HTTP 服务（手动开关，开启时地址自动复制），手机平板局域网访问，从"只读镜像"升级为可操作的远程工作台：
  - **SSE 实时推送** —— `/api/events` 长连接 + 15s 心跳，会话变化 250ms 去抖推送，页面秒级更新；断线自动降级 5s 轮询、恢复自动切回
  - **会话标签布局** —— 顶部标签切换多会话，聊天式单会话视图（贴底滚动、每会话独立草稿），底部 dock 输入框 + 工具行 + 5h 余额行
  - **可对话** —— 输入框直接回复进终端 / 桌面端（复用快捷回复注入通道），`/` 命令自动补全，乐观回显
  - **一键审批** —— 权限请求橙色卡（工具名 + 命令描述 + 允许/都允许/拒绝），`POST /api/approve` 注入数字键
  - **提问一键作答** —— AskUserQuestion 渲染选项按钮（含描述提示）+ 跳过，`POST /api/answer`；也可输入框自由回复
  - **权限模式切换** —— 工具行下拉：默认询问 / 自动接受编辑 / 计划模式 / 自动模式（auto）。hook 上报的 `permission_mode` 记入会话，按实测四态循环（default→acceptEdits→plan→auto，Claude Code v2.1.156）精确连发 Shift+Tab 直达目标；模式未知 / 桌面端 / bypass / 权限弹窗挂起时拒绝并给人话原因
  - **切换模型** —— 工具行模型下拉（全系 Claude 档，注入 `/model`）
  - **提醒** —— 新待批准两短声（WebAudio，铃铛开关记忆，刷新后首次触屏自动解锁）+ 切后台标题闪烁"● 待批准 N"
  - **富渲染与细节** —— 代码块 / diff 行色 / 内联 code、>320 字折叠（展开状态跨刷新保留）、相对时间、tokens 人类化、日夜主题（含 theme-color）、apple-touch-icon 主屏书签、运行/待批准/空闲 stats 概览
- **状态圆点同步到网页** —— 灵动岛展开后点会话卡状态圆点，把该对话置顶同步到网页（⭐ + 60 条完整历史），再点取消
- **安装 Skill** —— 命令栏"安装 Skill"按钮：粘贴 `claude plugin` 命令（支持连写 / `&&` / 换行）或 `owner/repo` 简写，后台 PowerShell 静默调用 claude CLI 安装，面板实时显示 CLI 真实输出。严格白名单校验防命令注入；任一命令不合法整体拒绝
- **会话来源筛选** —— 命令栏三态按钮：全部 → 仅终端(CLI) → 仅客户端(Claude Desktop) 循环切换（依据 `ClaudeMetadata.Entrypoint`），筛选生效时按钮高亮并切换字形
- **hook PID 绑定** —— hook 子进程沿父链上报 claude.exe 祖先 PID（payload 注入 `open_island_ppid`），岛端维护 session↔进程绑定：网页回复 / 快捷回复 / 切模型注入时最优先用绑定精确定位终端，彻底解决"同目录多会话"定位歧义；resume 换进程后下一个 hook 事件自动刷新
- **settings.json 热重载** —— 外部编辑配置文件 2 秒内自动生效（mtime 轮询），快捷键等订阅项无需重启

### Fixed

- **清空的会话过一阵子集体复活** —— 根因：进程存活同步在 alive 翻转时把 phase 伪造成 Running，而存活匹配很松（同 cwd / 标题互含都算），任何 claude 进程出现都会"点亮"一批历史会话。三层修复：① 转录 2 分钟内真有写入才升 Running（"进程活着"≠"在干活"）；② 收起判定改用**转录内容水位**（mtime）——只有真有新内容落盘或弹出阻塞性提问/权限才重现，对 phase 谎言免疫；③ "清理任务"范围扩为此刻全部安静会话，杜绝陈年会话顶上补位
- **网页发送失败（no-terminal-match）** —— 同 cwd 多会话时注入定位歧义被安全拒绝导致网页无法回复。三层修复：① hook PID 绑定（上）；② 绑定缺失时按"终端窗口标题 ↔ 会话标题"做唯一匹配兜底；③ 仍无法定位时网页显示人话原因（不再是裸代码）
- **新会话卡片不出现 / 显示卡死** —— ClaudeTranscriptWatcher 完全依赖 FileSystemWatcher，多会话狂写大 jsonl 时 64KB 缓冲溢出丢事件，且个别机器 FSW 根本不投递事件（实测），旧的"5s 补扫"迁移时已移除 → 丢失的更新无人兜底。新增 3s mtime 轮询兜底，走与 FSW 相同的去抖路径（实测合成新转录 8s 内上卡）
- **发送提示"剪贴板被占用"** —— 注入写剪贴板从 SetText（带 OleFlushClipboard，CLIPBRD_E_CANT_OPEN 高发）改为 SetDataObject(copy:false) 并把重试窗口拉长到 ~1.2s，骑过剪贴板历史/微信/输入法的瞬间抢占；还原剪贴板也加重试
- **网页发送提示"没能把终端切到前台"** —— Windows 前台锁：SetForegroundWindow 的授权跟着"最近接收输入的线程"走，旧代码 AttachThreadInput 只挂目标窗口线程、不够解锁。修复：激活时同时挂上**当前前台窗口**的线程；同条件复现转为前台切换成功

### Changed

- **胶囊头部小人放大 1.3 倍** —— 新增 Zoom 视觉缩放（与弹跳动画共存于同一 TransformGroup），"Open Island" 文字右移留位
- **网页功能栏（卡片版）** —— 0.5.0 开发期内先做了"每卡片功能栏"（模型下拉 / `/` 补全 / 日夜主题），随后整页升级为标签布局并保留全部能力

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
