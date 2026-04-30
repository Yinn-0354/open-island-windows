<div align="center">

# 🟣 Open Island

**Windows 上的 AI 编码助手控制中心 · 仿 macOS 灵动岛**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2011-0078D4.svg)]()

</div>

---

Open Island 是一个常驻托盘的桌面助手，把 Claude Code 等 AI 编码代理的运行状态、Token 用量、权限请求都汇聚到屏幕顶部一个 macOS 风格的"灵动岛"上。

- 📡 **实时镜像** Claude Code 的权限询问，让你不用切回终端就能 `1/2/3` 决策
- 📊 **统计面板** —— sessions / token / 模型占比 / 活跃热力图，全部 / 30 天 / 7 天三档可切
- 🪟 **Notch 模式** —— 拖到屏顶吸附成 macOS 刘海形态，不占主屏
- 🚀 **一键跳回** —— 点卡片直接 `claude --resume {sessionId}` 恢复历史会话；桌面端会话则把客户端窗口拉前台

---

## 截图

> _截图位待补，TODO_

## ✨ Features

- **Dynamic Island** —— 屏顶悬浮的活跃会话指示器，按工具图标 + 项目名 + 状态点显示
- **Permission mirror** —— Claude Code 的 PreToolUse 权限询问会同时镜像到岛上，配合三按钮（Yes / Yes don't ask again / No），点击会通过 SendInput 注入对应数字到 Claude 终端
- **Notch snap** —— 拖动岛体靠近屏顶 28px 内自动吸附成横条贴顶；下拉超过 48px 则恢复
- **Control Center** —— 三 Tab：
  - **Sessions** 列出所有 Claude 会话（按 mtime 排序）
  - **Overview** Token 总量 / 活跃天数 / 连续天数 / 高峰小时 / 最常用模型 / 84 天活动热力图
  - **Models** 按模型分组的 Token 占比 + I/O 详情
- **Workspace 过滤** —— 设置里指定项目目录，统计仅算 cwd 在该目录下的会话
- **Stop hook 触发任务完成** —— Claude Code 真正 `end_turn` 时桌面响铃 + 灵动岛绿灯闪
- **CLI / 桌面端区分** —— 跳转按钮自动判断 entrypoint，CLI 会话开终端跑 `claude --resume`，桌面端会话激活客户端窗口

## 📦 安装

从 [Releases](../../releases) 下载，二选一：

- 🟢 **推荐** · `OpenIsland-Setup-X.Y.Z-win-x64.exe` —— 标准安装包，双击自动装到 `%LOCALAPPDATA%\OpenIsland`，无需管理员，可在 Add/Remove Programs 卸载，可选开机自启
- 🟦 **绿色版** · `OpenIsland-vX.Y.Z-win-x64.zip` —— 解压即用，不写注册表

> ⚠️ 未做代码签名，Windows SmartScreen 会拦截。点 **更多信息 → 仍要运行** 即可。zip 版需要先右键属性 → 解除锁定。

首次启动会自动在 `%USERPROFILE%\.claude\settings.json` 注册 Claude Code 的 hook（`PreToolUse` / `PostToolUse` / `Stop` 三种），无需手动操作。

## 🏃 快速开始

1. 启动 `OpenIsland.exe`，托盘出现紫色岛标
2. 屏顶出现"Open Island"灵动岛
3. 在终端开个 Claude Code 会话：`claude` 或 `claude --resume`
4. 跑任何需要权限的工具（fetch、Edit 等），岛上会同步出权限提示

托盘菜单 → **Control Center** 可看完整 dashboard。

## 🛠 从源码构建

要求 Windows + .NET 8 SDK。

```powershell
git clone https://github.com/ludiwangfpga/open-island-windows.git
cd open-island-windows
dotnet build OpenIsland.sln -c Release
dotnet run --project src/OpenIsland.App/OpenIsland.App.csproj
```

开发期改 hook 二进制后，跑部署脚本一键重装：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
```

详见 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 🏗 架构

```
AI agent (Claude Code / Codex / Cursor / ...)
   │ stdin JSON
   ▼
open-island-hooks.exe   (per-event subprocess)
   │ Named Pipe "OpenIsland_Pipe"
   ▼
BridgeServer ──► SessionManager ──► SessionState (event-sourced)
   │                                   │
   ▼                                   ▼
DynamicIslandWindow / ControlCenter   SessionRegistry (持久化)
```

具体细节看 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 🤝 Contributing

欢迎 PR / issue。代码用双语注释（英文 XML doc + 中英行内皆可），具体规范见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 🙏 Acknowledgements

依赖以下开源库（皆 MIT/BSD）：
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM 工具
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — 系统托盘
- [System.CommandLine](https://github.com/dotnet/command-line-api) — Hooks CLI 解析
- Claude Code 团队 —— hook 协议设计

## 📄 License

[MIT](LICENSE) © 2025 ludiwangfpga
