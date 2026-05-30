<div align="center">

# 🟣 Open Island

**A macOS-style Dynamic Island for AI coding agents on Windows**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2011-0078D4.svg)]()

[English](README.md) · [简体中文](README.zh-CN.md)

<br/>

<img src="docs/screenshots/open-island.gif" alt="Open Island demo" width="760"/>

<br/><br/>

<img src="docs/screenshots/island-0.3.png" alt="Open Island — pixel sprite, stats bar, model switch, and 5-hour usage balance" width="340"/>

<sub>The header sprite is a pixel-art animation bound to session phase — blue &amp; bouncing while Claude works, red &amp; resting when idle/done. Below it: a live CPU·RAM·GPU·net bar, media controls, a one-click model switcher, and your Claude subscription's 5-hour usage balance (remaining % + reset countdown).</sub>

</div>

---

Open Island is a Windows tray companion that surfaces the live state of AI coding agents (primarily Claude Code) on a macOS-inspired Dynamic Island floating at the top of your screen — including permission prompts, token usage, and active sessions.

- 🎮 **Pixel status sprite** — the header status indicator is an animated pixel-art sprite bound to session phase: bouncing while Claude works, resting (with a short idle animation every 30s) when done
- 📈 **System stats bar** — live CPU / RAM / GPU / network throughput, refreshed every second (GPU works on any Windows locale)
- 🔔 **Sound notifications** — a chime when a task finishes and when a session needs your attention; mute toggle in the stats bar
- 🎵 **Media controls** — prev / play-pause / next + a system volume slider, works with any player (Spotify, browser, music apps…)
- 📡 **Permission mirror** — Claude Code's `PreToolUse` prompts mirror to the island so you can decide `1/2/3` without alt-tabbing back to the terminal
- ⚡ **Per-session mode buttons** — quick icon buttons on each session card to switch that session's permission mode (accept edits / auto / plan)
- 📊 **Stats dashboard** — sessions / tokens / model breakdown / activity heatmap, with All / 30d / 7d filters
- 🚀 **One-click resume** — clicking a session card runs `claude --resume {sessionId}` for CLI sessions, or brings the Claude Desktop app window to the front for desktop sessions

Stays out of the way — sits in collapsed mode at the top of the screen while you play DOTA, write code, or watch a stream:

<img src="docs/screenshots/in-action.png" alt="Open Island in collapsed mode while playing a game" width="900"/>

---

## ✨ Features

- **Model switch** — a **Switch Model** button below the volume row pops up a model list; pick one to switch. Add third-party models in the Control Center (cc-switch-style presets — DeepSeek / Zhipu GLM / Kimi / Qwen / OpenRouter / SiliconFlow / Novita / ModelScope / Xiaomi MiMo … base URLs pre-filled, just paste your API key). Official Claude profiles apply to both the desktop client and the CLI; third-party profiles write the `env` block in `~/.claude/settings.json` and take effect in a new CLI session. API keys are stored encrypted with Windows DPAPI
- **5-hour usage balance** — a row below the volume control shows your Claude subscription's 5-hour rolling-window remaining quota (green balance bar + "XX% left" + reset countdown), from `/api/oauth/usage` (same source as `/usage`, zero token cost), auto-refreshed every 5 minutes with a manual refresh button
- **Language switch (中文 / English)** — toggle the UI language from the tray right-click menu or the Control Center; defaults to your Windows system language and is remembered after you change it
- **Pin sessions** — a pin button on each card keeps that session from being removed by **Clear Tasks**
- **Free memory** — click the CPU% / RAM% to trim every process's working set (RAM-cleaner style); RAM% drops afterward
- **Pixel status sprite** — the header indicator is a pixel-art sprite (Aseprite sheet, nearest-neighbor + integer scaling so it stays crisp at 125% / 150% DPI) bound to `SessionPhase`:
  - **Running** → continuous loop + a gentle up-and-down bounce
  - **Idle / Completed** → holds the last frame, then every 30s randomly picks one of several idle variants (`idle.png` / `idle2.png` / `idle3.png` … auto-discovered, drop a file in to add one) and plays it once

  <img src="docs/screenshots/dynamic-island.png" alt="running sprite" width="380"/>&nbsp;&nbsp;<img src="docs/screenshots/dynamic-island-idle.png" alt="idle sprite" width="380"/>

- **System stats bar** — a row of CPU / RAM / GPU / network speed between the header and the session list, refreshed every second (`GetSystemTimes` / `GlobalMemoryStatusEx` / GPU Engine counters / `NetworkInterface`). GPU utilization is read via the PDH **English-counter** API (`PdhAddEnglishCounterW`), so it reports a real % even on non-English Windows. Column widths are fixed so CPU/RAM/GPU don't jitter as the network text changes width
- **Sound notifications** — the island plays a chime when a session goes Running → Idle/Completed (task done) and when it enters a needs-attention state (orange permission / red awaiting answer). A speaker toggle in the system stats bar mutes/unmutes (persisted)
- **Media controls** — prev / play-pause / next (system media keys, works with Spotify, browsers, any player) plus a system volume slider (CoreAudio `IAudioEndpointVolume`, two-way synced)
- **Per-session quick-mode buttons** — each session card has small icon buttons (hover shows "accept edits" / "auto mode" / "plan mode") to quickly switch that Claude session's permission mode, plus a × to temporarily hide the card; a hidden card reappears on its next activity (a new Running round or an attention phase)
- **Click the header to clear** — clicking the "Open Island" header clears the session list; sessions reappear automatically when they next become active
- **Dynamic Island** — floating top-screen indicator for active sessions, tagged by tool icon, project name, and a colored status dot
- **Permission mirror** — Claude Code's `PreToolUse` permission prompts are mirrored to the island. The three buttons inject `1` / `2` / `3` keystrokes into the Claude terminal via `SendInput`, equivalent to typing them yourself

  <img src="docs/screenshots/permission-mirror.png" alt="Permission Request mirrored to the island while DOTA 2 is running" width="800"/>

- **Control Center** — three tabs:
  - **Sessions** — all Claude conversations (sorted by transcript mtime)
  - **Overview** — Total tokens / Active days / Current/Longest streak / Peak hour / Favorite model + 84-day activity heatmap
  - **Models** — token share per model + I/O breakdown
- **Workspace filter** — restrict statistics to sessions whose `cwd` is under your configured project root(s)
- **Stop hook completion signal** — the green "task complete" indicator and beep fire only when Claude Code emits a real `Stop` hook (true `end_turn` / `stop_sequence`), not on every mid-task `end_turn`
- **CLI / Desktop routing** — clicking a session card auto-detects the transcript's `entrypoint` (using its **latest** value, so a session resumed from desktop into the CLI is routed correctly), and the terminal is located among only the **terminal-hosted** `claude.exe` processes (so the Claude Desktop app's many child processes don't confuse the match):
  - `cli` → activates the existing terminal where the session is running (only opens a new `claude --resume` tab if that terminal is gone)
  - `claude-desktop` → activates the Claude Desktop window

## 📦 Installation

Grab a build from [Releases](../../releases). Two flavors:

- 🟢 **Recommended** — `OpenIsland-Setup-X.Y.Z-win-x64.exe` — standard installer; installs into `%LOCALAPPDATA%\OpenIsland` without admin, registers in Add/Remove Programs, optional auto-start at login
- 🟦 **Portable** — `OpenIsland-vX.Y.Z-win-x64.zip` — extract anywhere and run; no registry writes

> ⚠️ Builds are not code-signed. Windows SmartScreen will warn — click **More info → Run anyway**. For the zip, right-click → Properties → Unblock first.

## 🏃 Quick Start

1. Run `OpenIsland.exe` — a purple icon appears in the system tray
2. The Dynamic Island shows up at the top of your screen
3. Start a Claude Code session: `claude` or `claude --resume`
4. Trigger any tool that needs permission (e.g. WebFetch) — the island shows the same prompt the terminal does

The system tray menu → **Control Center** opens the full dashboard.

## 🛠 Build From Source

Requires Windows + .NET 8 SDK.

```powershell
git clone https://github.com/ludiwangfpga/open-island-windows.git
cd open-island-windows
dotnet build OpenIsland.sln -c Release
dotnet run --project src/OpenIsland.App/OpenIsland.App.csproj
```

After modifying hook binaries, run the deploy script to repackage and reinstall:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and [ARCHITECTURE.md](ARCHITECTURE.md) for more.

## 🏗 Architecture

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
DynamicIslandWindow / ControlCenter   SessionRegistry (persistence)
```

Full details in [ARCHITECTURE.md](ARCHITECTURE.md).

## 🤝 Contributing

Issues and PRs welcome. The codebase uses bilingual comments (English XML docs + Chinese inline context); see [CONTRIBUTING.md](CONTRIBUTING.md).

## 🙏 Acknowledgements

Built on these excellent open-source libraries (all MIT/BSD):
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM helpers
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — system tray icon
- [System.CommandLine](https://github.com/dotnet/command-line-api) — CLI parsing for hooks
- The Claude Code team — for designing a clean hook protocol

## 📄 License

[MIT](LICENSE) © 2025 ludiwangfpga
