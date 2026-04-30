# Contributing to Open Island

Thanks for your interest! Issues 和 PR 都欢迎。本文是给打算改代码的贡献者的速查表。

## 环境

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows Terminal（可选但推荐）
- 一份能跑的 Claude Code（`claude --version` 能显示版本）

## 仓库布局

```
.
├── OpenIsland.sln
├── Directory.Build.props      # 集中元信息（Authors/Copyright/Version）
├── src/
│   ├── OpenIsland.Core/       # 共享类库（事件、桥、hooks 解析、registry）
│   ├── OpenIsland.Hooks/      # open-island-hooks.exe — Claude Code hook 触发的子进程
│   ├── OpenIsland.Setup/      # open-island-setup.exe — 装/卸 hooks 的 CLI
│   └── OpenIsland.App/        # OpenIsland.exe — WPF 主应用（托盘 + 灵动岛 + 控制中心）
├── tests/
│   └── OpenIsland.SmokeTest/  # 集成 smoke test，**不在 sln 里**，跑活的 ~/.claude 数据
└── scripts/
    └── deploy.ps1             # 一键 build + 重装 hooks + 重启 app
```

## 构建 / 运行 / 调试

```powershell
# 全量构建
dotnet build OpenIsland.sln

# 只跑主应用
dotnet run --project src/OpenIsland.App/OpenIsland.App.csproj

# 跑 smoke test（不在 sln 里，要单独跑）
dotnet run --project tests/OpenIsland.SmokeTest/OpenIsland.SmokeTest.csproj

# 装/卸/查 hooks
dotnet run --project src/OpenIsland.Setup -- install --agent claude
dotnet run --project src/OpenIsland.Setup -- uninstall --agent all
dotnet run --project src/OpenIsland.Setup -- status
```

## 改 hooks 后必跑 deploy.ps1

如果你改了 `OpenIsland.Hooks` 的代码，**必须**跑：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
```

这条脚本会：
1. 杀掉运行中的 `OpenIsland.exe`
2. Release build
3. 把整个 hooks 运行时（含 `.deps.json` / `.runtimeconfig.json` / `System.CommandLine.dll` 等）拷到 `OpenIsland.exe` 旁边 —— **不能只拷 .exe**，会找不到依赖
4. 删 `~/.claude/open-island-manifest.claude.json` 触发 SetupService 重新装 hooks
5. 重启 app

## 注释规范

代码里**双语并存**：

- **XML doc-comments** (`/// <summary>...</summary>`) 优先英文，方便 IntelliSense / 自动文档
- **行内注释** 中英文都可以 —— 写"为什么这么做"比"做了什么"更有价值，不必纠结语言

PR review 不卡注释语言。我们更关心：
- 注释解释**为什么**而非**做什么**
- 关键 bug 修复点附上 issue 链接 / 重现路径

## 提交规范

建议但不强制 [Conventional Commits](https://www.conventionalcommits.org/)：

- `feat: ...` 新功能
- `fix: ...` bug 修复
- `refactor: ...` 重构无功能变化
- `docs: ...` 文档
- `chore: ...` 构建 / CI / 杂项

## Smoke test

`tests/OpenIsland.SmokeTest/` 是手写的回归脚本，跑用户机器上活的 `~/.claude/projects/` 数据。它**不在 OpenIsland.sln 里**，需要单独 `dotnet run`。

```powershell
dotnet run --project tests/OpenIsland.SmokeTest/OpenIsland.SmokeTest.csproj
```

输出 `PASS`/`FAIL` 行，进程退出码恒为 0（不阻塞 CI），需要肉眼看输出。

PR 至少跑过一次 smoke test（不要求全 PASS —— 部分检查依赖你 Claude 的真实历史，新机器上会 FAIL）。

新加回归点请直接追加到 `Program.cs` 里，**不要**引入 xUnit / NUnit 等测试框架（这是有意为之的极简设计）。

## 设计原则

- **Hook 必须 fail-open**：`OpenIsland.Hooks/Program.cs` 永远返回 0，超时 / 解析失败 / bridge 不通都不能阻塞 Claude 的运行
- **Event sourcing**：所有 session 状态变更走 `SessionState.Apply(AgentEvent)` 返回新不可变状态。新加事件类型必须改三处：`AgentEvent.cs` / `SessionState.Apply` switch / `SessionManager.GetSessionIdFromEvent`
- **Named Pipe 名固定为 `OpenIsland_Pipe`**：不要改回 per-user 名（中文 / 非 ASCII Windows 用户名会出问题）
- **Stop hook 是任务完成的权威信号**：watcher 默认只 emit Running，避免 stop_reason 推测在 multi-step 中段误判 Idle

## 报 bug

[New Issue](../../issues/new) 时附：
- Windows 版本、.NET 版本（`dotnet --info`）
- Claude Code 版本（`claude --version`）
- 重现步骤
- 如能重现，开 OpenIsland 后跑 `tests/OpenIsland.SmokeTest` 把输出贴上

## 安全 / 漏洞披露

参见 [SECURITY.md](SECURITY.md)。
