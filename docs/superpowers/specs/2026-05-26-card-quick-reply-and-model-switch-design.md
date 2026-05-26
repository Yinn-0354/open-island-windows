# Design: Per-card Quick Reply & Model Switch

Date: 2026-05-26
Status: Approved direction (autonomous build authorised by user); third-party scope caveat flagged.

## Goal

Add two controls to each Dynamic Island session card:

1. **Quick reply** — type a follow-up prompt on a card and send it to that session without alt-tabbing back to the terminal/Desktop.
2. **Model switch** — a toggle on the card to switch that session's model. Default is the official Claude model; the user can add their own (third-party) models in the Control Center.

The two features share one injection primitive, so quick reply is built first and model switch reuses it.

## Decisions (from brainstorming)

- Entry point: an input box / control **on each session card** (target session is unambiguous by construction).
- Send = **direct submit** (inject text + Enter), gated by strict foreground verification.
- Scope: **both CLI and Claude Desktop** sessions.
- Injection mechanism: **clipboard paste** (save → set → focus+verify target → Ctrl+V + Enter → restore). Robust for Chinese/code/long/multi-line; single focus moment (smallest "wrong-window" exposure); works for both the terminal and the Desktop Electron input.

## Shared injection primitive (the safety core)

A single mechanism used by both features. Inputs: a target session + the text to send.

- **Targeting (CLI):** resolve the session's terminal PID by **positive match only** — JumpTarget cwd → transcript cwd → sessionId. **Drop the "only one claude running → it's that one" fallback** (review bug #7) for text/command injection: if no positive match, abort with a clear status. (The fallback stays acceptable for plain jump/activate, not for blind injection.)
- **Focus + verify:** reuse `TerminalJumpService` activate-then-verify: activate the target window, wait, then require `GetForegroundWindow() == targetHwnd` (CLI) or that the Claude Desktop main window is foreground (Desktop). **Abort if not** — never paste into whatever happens to be focused.
- **Paste:** save current clipboard; set clipboard to the text; send `Ctrl+V`; if submit, send `Enter`; restore the previous clipboard. Clipboard ops run on the WPF UI/STA thread.
- **Desktop path:** reuse the existing Claude Desktop UIA approach (`ActivateClaudeDesktopWindow` + `SetFocus` on the message input element) to focus the Electron input, verify foreground, then paste + Enter.
- **Empty/whitespace text:** no-op. **Length cap:** reject absurdly long input (e.g. > 16 KB) to avoid pathological pastes.

### New service surface (OpenIsland.App / TerminalJumpService)
- `Task<InjectResult> SendTextToTerminalAsync(int claudePid, string text, bool submit)`
- `Task<InjectResult> SendTextToClaudeDesktopAsync(string sessionId, string text, bool submit)`
- `InjectResult` = `{ bool Ok; string? Reason }` so the card can show a precise status ("没找到终端" / "没切到前台，已取消" / "已发送").

## Feature 1: Quick reply

- **VM:** add `QuickReplyText` (string) + `SendQuickReplyCommand` to the per-session island item (`IslandSessionItem` in `DynamicIslandViewModel`). Mirror the existing per-card mode-button command pipeline.
- **Routing:** `SessionManager.SendQuickReplyAsync(sessionId, text)` looks up the session, branches on entrypoint (`cli` vs `claude-desktop`), calls the matching injection method.
- **UI (card XAML):** a `TextBox` on the card (Enter = send, Shift+Enter = newline) with a small send affordance and a transient status line. Hidden while the card is in `WaitingForApproval`/`WaitingForAnswer` (those have their own buttons). Cleared after a successful send.

## Feature 2: Model switch

### Data model — `ModelProfile`
A named entry the user can switch a session to. Stored in OpenIsland settings (extend `WorkspaceSettings` → `%APPDATA%\OpenIsland\settings.json`).

```
ModelProfile {
  string Id;            // stable guid/slug
  string Name;          // display label, e.g. "Opus", "DeepSeek V4"
  ModelKind Kind;       // ClaudeModel | ThirdParty
  // ClaudeModel:
  string? ClaudeModelSlug;   // e.g. "opus" / "sonnet" / "haiku" — used as `/model <slug>`
  // ThirdParty:
  string? BaseUrl;           // ANTHROPIC_BASE_URL
  string  ApiKeyEnvName;     // "ANTHROPIC_AUTH_TOKEN" (default) | "ANTHROPIC_API_KEY"
  string? ApiKey;            // token value
  string? Model;             // ANTHROPIC_MODEL
  string? HaikuModel;        // ANTHROPIC_DEFAULT_HAIKU_MODEL (optional)
  string? SonnetModel;       // optional
  string? OpusModel;         // optional
}
```

- A built-in default profile **"Claude (官方)"** (Kind=ClaudeModel, no slug → native default) is always present and not deletable.
- Control Center gains an **"添加/管理模型"** UI (add/edit/delete `ThirdParty` profiles and name Claude-model shortcuts). Note: the existing stats tab named "Models" is unrelated — this management UI lives in the Settings window to avoid confusion.

### Switch behaviour (per the cc-switch research)
- **ClaudeModel profile** → inject `/model <slug>` into the session via the shared primitive. **Live, per-session, CLI + Desktop.** (No slug = `/model` with default; or skip.)
- **ThirdParty profile** → write the provider's `env` block to `~/.claude/settings.json`:
  - top-level `env` with `ANTHROPIC_BASE_URL`, the chosen key env name = token, `ANTHROPIC_MODEL`, and any role models.
  - **atomic write** (temp + rename) + **merge-preserving** all other settings keys (reuse the `JsonObject` approach from the hook-installer fix) + timestamped backup.
  - **Applies to new CLI sessions only.** Not live for the running session; never affects Desktop. The card toggle surfaces this ("新 CLI 会话生效").
  - Selecting "Claude (官方)" again clears the `env` overrides (write `{"env":{}}`-equivalent: remove the ANTHROPIC_* keys we manage), matching cc-switch's official = empty-env behaviour.

### Per-card toggle UI
- A compact dropdown/cycle control on each card showing the active profile for that session.
- Picking a `ClaudeModel` → immediate inject. Picking a `ThirdParty` → write env + toast "新 CLI 会话生效".
- "Active provider" for the global third-party case is tracked in OpenIsland settings.

## Testable units (TDD) vs manual verification

**Unit-testable (xUnit, OpenIsland.Tests):**
- `ModelProfile` store: add/edit/delete/serialize round-trip; default profile always present & protected.
- settings.json **env merge**: applying a ThirdParty profile sets exactly the managed `ANTHROPIC_*` keys, preserves all other settings/keys; selecting official removes only the managed keys. (Pure `JsonObject` function, like the hook merge.)
- `/model` command construction from a ClaudeModel profile.
- CLI targeting decision (positive-match only, no single-claude fallback) — pure function over a running-sessions list.
- Quick-reply text sanitation (trim, reject empty, length cap).

**Manual (needs a live desktop + Claude session — can't be auto-verified):**
- Actual clipboard paste + Enter landing in the right terminal / Desktop input.
- Foreground-verify abort behaviour when another window is focused.
- A model switch actually taking effect in Claude.

## Safety summary
- Per-card target = explicit session.
- Injection: positive-match targeting + foreground re-verify + abort-on-mismatch (no blind paste).
- settings.json writes: atomic + merge-preserving + backup.
- Clipboard saved/restored.
- Text length capped; empty no-op.

## Out of scope / caveats
- Third-party switch is **not** live, **not** per-running-session, **not** for Desktop — it sets the provider for **new CLI sessions** (global `~/.claude/settings.json`). Per-project scoping (`<cwd>/.claude/settings.json`) is a possible future refinement.
- Windows Terminal's multi-line paste warning (if enabled by the user) may show on multi-line sends.
- No secret encryption for stored third-party API keys in v1 (stored in OpenIsland settings.json like other config) — note for the user.
```
