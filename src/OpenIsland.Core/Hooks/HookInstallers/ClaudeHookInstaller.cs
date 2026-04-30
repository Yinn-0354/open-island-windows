using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenIsland.Core.Hooks.HookInstallers;

/// <summary>
/// Hook安装器接口
/// </summary>
public interface IHookInstaller
{
    Task<bool> InstallAsync(string source, CancellationToken ct = default);
    Task<bool> UninstallAsync(string source, CancellationToken ct = default);
    Task<bool> IsInstalledAsync(string source, CancellationToken ct = default);
}

/// <summary>
/// Claude Code Hook安装器
/// </summary>
public class ClaudeHookInstaller : IHookInstaller
{
    private readonly ILogger<ClaudeHookInstaller>? _logger;

    public ClaudeHookInstaller(ILogger<ClaudeHookInstaller>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 获取Claude Code配置目录
    /// </summary>
    public static string GetClaudeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude");
    }

    /// <summary>
    /// 生成hook命令
    /// </summary>
    public static string GetHookCommand(string hooksBinaryPath, string source)
    {
        return $"\"{hooksBinaryPath}\" --source {source}";
    }

    /// <summary>
    /// 默认会注册的事件子集 ——
    /// PreToolUse: 同步阻塞 Claude 直到用户审批（事前权限拦截，transcript 无法做到）
    /// PostToolUse: "用户已在终端按 1/2 同意" 的回信号，让灵动岛上挂着的橙色卡片
    ///              在 tool 跑完后立刻消失。
    /// Stop:        Claude Code 每轮 assistant 真正 end_turn / stop_sequence 后触发的
    ///              权威"任务完成"信号 —— 替代 watcher 凭 stop_reason 推测的 Idle 判定，
    ///              避免中段纯文本 end_turn 造成的提早完成误判。
    /// transcript watcher 仍负责 phase 颜色、token、session 标题等其它一切。
    /// 不装 SessionStart（拖慢 /resume）/ UserPromptSubmit 等。
    /// </summary>
    public static readonly string[] DefaultEvents = new[] { "PreToolUse", "PostToolUse", "Stop" };

    /// <summary>
    /// 安装hooks到settings.json
    /// </summary>
    public Task<bool> InstallAsync(string source = "claude", CancellationToken ct = default)
        => InstallAsync(source, DefaultEvents, ct);

    /// <summary>
    /// 安装指定事件子集的 hooks 到 settings.json
    /// </summary>
    public async Task<bool> InstallAsync(string source, IReadOnlyList<string> events, CancellationToken ct = default)
    {
        try
        {
            var claudeDir = GetClaudeDirectory();
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            _logger?.LogInformation("Installing hooks for {Source} (events={Events}) to {Path}",
                source, string.Join(",", events), settingsPath);

            // 获取hooks二进制路径
            var hooksBinaryPath = GetHooksBinaryPath();
            if (string.IsNullOrEmpty(hooksBinaryPath))
            {
                _logger?.LogError("Hooks binary not found");
                return false;
            }

            // 读取现有配置
            Dictionary<string, JsonElement>? settings = null;
            if (File.Exists(settingsPath))
            {
                var json = await File.ReadAllTextAsync(settingsPath, ct);
                settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            }
            settings ??= new Dictionary<string, JsonElement>();

            // 创建hooks配置（Claude Code 要求的嵌套格式）
            var hookCommand = GetHookCommand(hooksBinaryPath, source);
            var hookEntry = new
            {
                hooks = new[]
                {
                    new { type = "command", command = hookCommand }
                }
            };
            var hooks = new Dictionary<string, object[]>();
            foreach (var ev in events)
            {
                hooks[ev] = new object[] { hookEntry };
            }

            // 更新settings
            settings["hooks"] = JsonSerializer.SerializeToElement(hooks);

            // 备份原配置
            if (File.Exists(settingsPath))
            {
                var backupPath = settingsPath + $".backup.{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(settingsPath, backupPath, overwrite: true);
                _logger?.LogDebug("Backed up settings to {BackupPath}", backupPath);
            }

            // 写入新配置
            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var newJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(settingsPath, newJson, ct);

            // 保存安装清单
            await SaveManifestAsync(source, claudeDir, ct);

            _logger?.LogInformation("Successfully installed hooks for {Source}", source);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to install hooks for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// 卸载hooks
    /// </summary>
    public async Task<bool> UninstallAsync(string source = "claude", CancellationToken ct = default)
    {
        try
        {
            var claudeDir = GetClaudeDirectory();
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            var manifestPath = Path.Combine(claudeDir, $"open-island-manifest.{source}.json");

            _logger?.LogInformation("Uninstalling hooks for {Source}", source);

            if (!File.Exists(settingsPath))
            {
                _logger?.LogWarning("Settings file not found, nothing to uninstall");
                return true;
            }

            // 读取现有配置
            var json = await File.ReadAllTextAsync(settingsPath, ct);
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (settings == null) return true;

            // 移除hooks
            if (settings.ContainsKey("hooks"))
            {
                settings.Remove("hooks");
            }

            // 备份并写入
            var backupPath = settingsPath + $".backup.uninstall.{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(settingsPath, backupPath, overwrite: true);

            var newJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(settingsPath, newJson, ct);

            // 删除清单
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            _logger?.LogInformation("Successfully uninstalled hooks for {Source}", source);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to uninstall hooks for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// 检查是否已安装
    /// </summary>
    public Task<bool> IsInstalledAsync(string source = "claude", CancellationToken ct = default)
    {
        var claudeDir = GetClaudeDirectory();
        var manifestPath = Path.Combine(claudeDir, $"open-island-manifest.{source}.json");
        return Task.FromResult(File.Exists(manifestPath));
    }

    private async Task SaveManifestAsync(string source, string claudeDir, CancellationToken ct)
    {
        var manifest = new HookManifest
        {
            Source = source,
            InstalledAt = DateTime.UtcNow,
            HooksBinaryVersion = GetType().Assembly.GetName().Version?.ToString() ?? "unknown"
        };

        var manifestPath = Path.Combine(claudeDir, $"open-island-manifest.{source}.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, json, ct);
    }

    private static string? GetHooksBinaryPath()
    {
        // 在开发环境中，假设hooks二进制与核心库在同一目录
        // 生产环境应该从安装目录获取
        var assemblyDir = Path.GetDirectoryName(typeof(ClaudeHookInstaller).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDir)) return null;

        // 首先检查当前目录
        var hooksPath = Path.Combine(assemblyDir, "open-island-hooks.exe");
        if (File.Exists(hooksPath)) return hooksPath;

        // 检查相对于解决方案的路径（开发环境）
        // assemblyDir = .../src/OpenIsland.App/bin/Debug/net8.0-windows，上4层 = .../src
        var srcDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
        var devPath = Path.Combine(srcDir, "OpenIsland.Hooks", "bin", "Debug", "net8.0", "open-island-hooks.exe");
        if (File.Exists(devPath)) return devPath;

        // 检查PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, "open-island-hooks.exe");
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        return null;
    }
}

/// <summary>
/// Hook安装清单
/// </summary>
public class HookManifest
{
    public string Source { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    public string HooksBinaryVersion { get; set; } = "";
}
