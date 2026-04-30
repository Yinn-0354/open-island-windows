using System.CommandLine;
using System.IO;
using OpenIsland.Core.Hooks.HookInstallers;

namespace OpenIsland.Setup;

/// <summary>
/// Open Island Setup CLI - 安装和管理代理hooks
/// </summary>
public class Program
{
    /// <summary>
    /// Claude 系列 source 列表 - 这些 source 已切换到 transcript 监听模式，install 时跳过。
    /// Claude-family source list - install path is now a deprecation warning; uninstall still
    /// works so users can clean up legacy installs by hand. Must stay in sync with
    /// SessionManager.ProcessHookEventAsync, OpenIsland.Hooks/Program.cs:IsInteractiveHook,
    /// and OpenIsland.App/Services/SetupService.cs:ClaudeFamilySources.
    /// </summary>
    private static readonly string[] ClaudeFamilySources =
    {
        "claude", "qoder", "qwen", "factory", "codebuddy", "kimi"
    };

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Open Island Setup - Install and manage AI agent hooks");

        // Install 命令
        var installCommand = new Command("install", "Install hooks for an AI agent");
        var installAgentOption = new Option<string>(
            name: "--agent",
            description: "Agent to install hooks for (claude, codex, cursor, gemini, all)"
        )
        {
            IsRequired = true
        };
        installAgentOption.AddAlias("-a");
        installCommand.AddOption(installAgentOption);
        installCommand.SetHandler(async (string agent) =>
        {
            var result = await InstallAsync(agent);
            Environment.ExitCode = result;
        }, installAgentOption);
        rootCommand.AddCommand(installCommand);

        // Uninstall 命令
        var uninstallCommand = new Command("uninstall", "Remove hooks for an AI agent");
        var uninstallAgentOption = new Option<string>(
            name: "--agent",
            description: "Agent to uninstall hooks for (claude, codex, cursor, gemini, all)"
        )
        {
            IsRequired = true
        };
        uninstallAgentOption.AddAlias("-a");
        uninstallCommand.AddOption(uninstallAgentOption);
        uninstallCommand.SetHandler(async (string agent) =>
        {
            var result = await UninstallAsync(agent);
            Environment.ExitCode = result;
        }, uninstallAgentOption);
        rootCommand.AddCommand(uninstallCommand);

        // Status 命令
        var statusCommand = new Command("status", "Check installation status");
        statusCommand.SetHandler(async () =>
        {
            var result = await StatusAsync();
            Environment.ExitCode = result;
        });
        rootCommand.AddCommand(statusCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> InstallAsync(string agent)
    {
        var lowerAgent = agent.ToLowerInvariant();

        // Claude 系列已经废弃 hook 模式 - 单独请求时打印警告并直接退出。
        // Claude family is deprecated for hook install. When the user asked specifically for one
        // of those sources, print to stderr and exit 0 (no settings.json edit, no manifest write).
        if (IsClaudeFamilySource(lowerAgent))
        {
            PrintClaudeFamilyDeprecationWarning(lowerAgent);
            return 0;
        }

        var installers = GetInstallersForAgent(agent);
        if (installers.Count == 0)
        {
            Console.WriteLine($"Error: Unknown agent '{agent}'. Supported: claude, codex, cursor, gemini, all");
            return 1;
        }

        // --agent all 时，过滤掉 Claude 系列 - 仍然为它们打印一次废弃提示，但继续安装其他 agent。
        // When --agent all, drop Claude-family installers but emit one deprecation notice so users
        // are not surprised that Claude is missing from the install summary.
        if (lowerAgent == "all")
        {
            var filtered = installers
                .Where(t => !IsClaudeFamilySource(t.Name.ToLowerInvariant()))
                .ToList();
            if (filtered.Count != installers.Count)
            {
                PrintClaudeFamilyDeprecationWarning("claude");
            }
            installers = filtered;
        }

        if (installers.Count == 0)
        {
            // 全部被过滤掉（理论上 --agent all 不会走到这） - 视作成功无操作。
            // All entries filtered (only theoretically reachable with --agent all) - treat as no-op.
            Console.WriteLine("Nothing to install.");
            return 0;
        }

        Console.WriteLine($"Installing Open Island hooks for {agent}...\n");

        var results = new List<(string Name, bool Success)>();
        foreach (var (name, installer) in installers)
        {
            Console.Write($"  {name}... ");
            try
            {
                var success = await installer.InstallAsync(name.ToLowerInvariant());
                results.Add((name, success));
                Console.WriteLine(success ? "OK" : "FAILED");
            }
            catch (Exception ex)
            {
                results.Add((name, false));
                Console.WriteLine($"FAILED ({ex.Message})");
            }
        }

        Console.WriteLine();
        var allSuccess = results.All(r => r.Success);
        if (allSuccess)
        {
            Console.WriteLine("Installation complete!");
            Console.WriteLine("\nNext steps:");
            Console.WriteLine("  1. Restart your AI agent if it's running");
            Console.WriteLine("  2. Open Island dashboard will show sessions automatically");
            return 0;
        }
        else
        {
            var failed = results.Where(r => !r.Success).Select(r => r.Name);
            Console.WriteLine($"Installation failed for: {string.Join(", ", failed)}");
            return 1;
        }
    }

    private static async Task<int> UninstallAsync(string agent)
    {
        var installers = GetInstallersForAgent(agent);
        if (installers.Count == 0)
        {
            Console.WriteLine($"Error: Unknown agent '{agent}'. Supported: claude, codex, cursor, gemini, all");
            return 1;
        }

        Console.WriteLine($"Uninstalling Open Island hooks for {agent}...\n");

        var results = new List<(string Name, bool Success)>();
        foreach (var (name, installer) in installers)
        {
            Console.Write($"  {name}... ");
            try
            {
                var success = await installer.UninstallAsync(name.ToLowerInvariant());
                results.Add((name, success));
                Console.WriteLine(success ? "OK" : "FAILED");
            }
            catch (Exception ex)
            {
                results.Add((name, false));
                Console.WriteLine($"FAILED ({ex.Message})");
            }
        }

        Console.WriteLine();
        var allSuccess = results.All(r => r.Success);
        if (allSuccess)
        {
            Console.WriteLine("Uninstallation complete!");
            return 0;
        }
        else
        {
            Console.WriteLine("Some uninstall operations failed. You may need to manually clean up.");
            return 1;
        }
    }

    private static async Task<int> StatusAsync()
    {
        Console.WriteLine("Open Island Setup Status\n");
        Console.WriteLine("Installed hooks:");

        var agents = new[] { ("Claude", "claude"), ("Codex", "codex"), ("Cursor", "cursor"), ("Gemini", "gemini") };
        var claudeInstaller = new ClaudeHookInstaller();
        var claudeDir = ClaudeHookInstaller.GetClaudeDirectory();

        foreach (var (displayName, agentKey) in agents)
        {
            // Claude 系列：sentinel 优先级最高 - 已迁移到 transcript 监听。
            // Claude family: prefer the transcript-mode sentinel since hooks are deprecated.
            if (IsClaudeFamilySource(agentKey))
            {
                var sentinelPath = Path.Combine(claudeDir, $"open-island-transcript-mode.{agentKey}.json");
                if (File.Exists(sentinelPath))
                {
                    Console.WriteLine($"  {displayName}: transcript-mode (hooks deprecated)");
                    continue;
                }
            }

            bool installed = false;
            try
            {
                installed = await claudeInstaller.IsInstalledAsync(agentKey);
            }
            catch { }

            Console.WriteLine($"  {displayName}: {(installed ? "installed" : "not installed")}");
        }

        Console.WriteLine("\nUse 'open-island-setup install --agent <agent>' to install hooks");
        return 0;
    }

    private static List<(string Name, ClaudeHookInstaller Installer)> GetInstallersForAgent(string agent)
    {
        var result = new List<(string Name, ClaudeHookInstaller Installer)>();

        var lowerAgent = agent.ToLowerInvariant();

        if (lowerAgent == "all")
        {
            result.Add(("Claude", new ClaudeHookInstaller()));
            result.Add(("Codex", new ClaudeHookInstaller()));
            result.Add(("Cursor", new ClaudeHookInstaller()));
            result.Add(("Gemini", new ClaudeHookInstaller()));
        }
        else if (lowerAgent == "claude" || lowerAgent == "qoder" || lowerAgent == "qwen" ||
                 lowerAgent == "factory" || lowerAgent == "codebuddy" || lowerAgent == "kimi")
        {
            result.Add((char.ToUpper(lowerAgent[0]) + lowerAgent[1..], new ClaudeHookInstaller()));
        }
        else if (lowerAgent == "codex")
        {
            result.Add(("Codex", new ClaudeHookInstaller()));
        }
        else if (lowerAgent == "cursor")
        {
            result.Add(("Cursor", new ClaudeHookInstaller()));
        }
        else if (lowerAgent == "gemini")
        {
            result.Add(("Gemini", new ClaudeHookInstaller()));
        }

        return result;
    }

    private static bool IsClaudeFamilySource(string lowerAgent)
    {
        return Array.IndexOf(ClaudeFamilySources, lowerAgent) >= 0;
    }

    private static void PrintClaudeFamilyDeprecationWarning(string source)
    {
        // 写到 stderr - 即使重定向 stdout 用户也能看到。
        // Write to stderr so users still see the notice even when piping stdout.
        Console.Error.WriteLine(
            $"warning: Claude Code (source '{source}') now uses transcript watching; " +
            "hooks are no longer required and will not be installed. " +
            "Run 'open-island-setup uninstall --agent " + source + "' to remove any legacy hook entries.");
    }
}
