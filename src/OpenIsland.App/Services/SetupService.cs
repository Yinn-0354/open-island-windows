using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenIsland.Core.Hooks.HookInstallers;

namespace OpenIsland.App.Services;

/// <summary>
/// 设置服务 - 检查并自动安装hooks
/// Setup service - checks installation state and auto-installs/migrates hooks on startup.
/// </summary>
public class SetupService
{
    private readonly ILogger<SetupService>? _logger;

    /// <summary>
    /// Claude 系列 source 列表 - 这 6 个 source 现已切换到 transcript 监听模式，不再注册 hooks。
    /// Claude-family source list - these 6 sources have migrated to transcript watching and
    /// no longer register hooks. Must stay in sync with SessionManager.ProcessHookEventAsync,
    /// OpenIsland.Hooks/Program.cs:IsInteractiveHook, and OpenIsland.Setup/Program.cs:GetInstallersForAgent.
    /// </summary>
    public static readonly string[] ClaudeFamilySources =
    {
        "claude", "qoder", "qwen", "factory", "codebuddy", "kimi"
    };

    public SetupService(ILogger<SetupService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检查并自动安装hooks
    /// Run startup migration + auto-install. For Claude-family sources we tear down any
    /// existing hook install and drop a transcript-mode sentinel; for codex/cursor/gemini
    /// the legacy hook auto-install is preserved.
    /// </summary>
    public async Task CheckAndAutoInstallAsync()
    {
        _logger?.LogInformation("Checking hook installations...");

        // Claude 系列：迁移到 transcript 监听模式 - 卸载旧 hook 并写入 sentinel。
        // Claude family: migrate to transcript-watching mode (uninstall old hooks, write sentinel).
        if (IsAgentInstalled("claude"))
        {
            await MigrateClaudeFamilyToTranscriptModeAsync();
        }

        // 其他 agent 的自动检测保持原样（codex/cursor/gemini 仍走 hook 流程）。
        // Other agents (codex/cursor/gemini) keep the legacy hook flow untouched.
    }

    /// <summary>
    /// 把所有 Claude 系列 source 设到"transcript-only + 仅 PreToolUse hook"模式：
    /// transcript watcher 负责所有事后信号（session 发现、phase 颜色、token、中断标记...），
    /// 而 PreToolUse hook 只负责"事前权限拦截"这一件事 —— 这是物理上 transcript 做不到的。
    ///
    /// 每次启动都会：
    ///   1. 卸载 settings.json 里所有 OpenIsland 老 hook 条目（包括 SessionStart/PostToolUse 等
    ///      会拖慢 /resume 和每次 tool 调用的事件）
    ///   2. 重新安装 *仅* PreToolUse 这一个事件
    ///   3. 写一个标记文件标识当前 hook 子集（升级时用来判断是否需要 reinstall）
    /// 幂等 —— 即使二进制路径变了也能正确刷新（每次都重写 settings.json）。
    /// </summary>
    private async Task MigrateClaudeFamilyToTranscriptModeAsync()
    {
        var claudeDir = ClaudeHookInstaller.GetClaudeDirectory();
        var claudeInstaller = new ClaudeHookInstaller();

        // 6 个 Claude-family CLI 都读同一个 ~/.claude/settings.json，所以全局只装一条
        // source=claude 的 PreToolUse hook 就够 —— 任何 CLI 触发 PreToolUse 都会调它。
        // 不要 foreach 6 次：UninstallAsync 移除整个 hooks 块，循环里每次都会清掉
        // 上一次 install 的成果，最后只剩末尾那个 source 的安装。这是上一次部署观察到的 bug。
        try
        {
            await claudeInstaller.UninstallAsync("claude");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Pre-install uninstall failed, continuing");
        }

        try
        {
            var ok = await claudeInstaller.InstallAsync("claude", ClaudeHookInstaller.DefaultEvents);
            if (ok)
                _logger?.LogInformation("Installed PreToolUse-only hook for Claude family");
            else
                _logger?.LogWarning("Claude PreToolUse install returned false");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to install Claude PreToolUse hook");
        }

        // 给每个 source 写一个 sentinel（仅诊断用：方便用户 ls 看到模式标识）
        foreach (var source in ClaudeFamilySources)
        {
            try
            {
                var sentinelPath = GetTranscriptModeSentinelPath(claudeDir, source);
                await WriteTranscriptModeSentinelAsync(sentinelPath, source);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Sentinel write failed for {Source}", source);
            }
        }
    }

    /// <summary>
    /// 获取 transcript 监听模式 sentinel 文件路径。
    /// Path of the per-source sentinel file that signals "hooks already torn down for this source".
    /// </summary>
    public static string GetTranscriptModeSentinelPath(string claudeDir, string source)
    {
        return Path.Combine(claudeDir, $"open-island-transcript-mode.{source}.json");
    }

    private static async Task WriteTranscriptModeSentinelAsync(string sentinelPath, string source)
    {
        var dir = Path.GetDirectoryName(sentinelPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            version = 1,
            mode = "transcript",
            source,
            migratedAt = DateTime.UtcNow.ToString("O")
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(sentinelPath, json);
    }

    private bool IsAgentInstalled(string agent)
    {
        // 检查agent是否安装在系统中
        return agent.ToLowerInvariant() switch
        {
            "claude" => CheckClaudeInstalled(),
            "codex" => CheckCodexInstalled(),
            "cursor" => CheckCursorInstalled(),
            "gemini" => CheckGeminiInstalled(),
            _ => false
        };
    }

    private bool CheckClaudeInstalled()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(userProfile, ".claude");
        return Directory.Exists(claudeDir);
    }

    private bool CheckCodexInstalled()
    {
        // 检查codex命令
        return CheckCommandExists("codex");
    }

    private bool CheckCursorInstalled()
    {
        // 检查Cursor是否安装
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Directory.Exists(Path.Combine(localAppData, "Programs", "Cursor"));
    }

    private bool CheckGeminiInstalled()
    {
        // 检查gemini命令
        return CheckCommandExists("gemini");
    }

    private bool CheckCommandExists(string command)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                var extensions = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';') ?? new[] { ".exe", ".cmd", ".bat" };
                foreach (var path in pathEnv.Split(Path.PathSeparator))
                {
                    foreach (var ext in extensions)
                    {
                        if (File.Exists(Path.Combine(path, command + ext)))
                            return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }
}
