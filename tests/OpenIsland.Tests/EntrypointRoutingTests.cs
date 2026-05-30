using System.IO;
using OpenIsland.Core;

namespace OpenIsland.Tests;

/// <summary>
/// 快捷回复路由的核心：会话 entrypoint 必须反映"当前在哪运行"（cli vs claude-desktop）。
/// 回归 bug：同一会话先在 claude-desktop 开、后被 `claude --resume` 拉到 cli，旧逻辑锁死
/// 最早的 desktop → 快捷回复发去客户端而非 CLI。修复后取**最新**一行的 entrypoint。
/// </summary>
public class EntrypointRoutingTests
{
    private static async Task<string?> EntrypointOf(params string[] entrypoints)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "oi-ep-" + System.Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var lines = new List<string>();
            for (int i = 0; i < entrypoints.Length; i++)
                lines.Add(
                    "{\"type\":\"user\",\"sessionId\":\"s1\",\"cwd\":\"C:\\\\proj\"," +
                    $"\"entrypoint\":\"{entrypoints[i]}\"," +
                    $"\"timestamp\":\"2026-05-30T0{i}:00:00.000Z\"," +
                    "\"message\":{\"role\":\"user\",\"content\":\"hi\"}}");
            await File.WriteAllLinesAsync(tmp, lines);

            var disco = new ClaudeTranscriptDiscovery();
            var info = await disco.ParseSessionFileAsync(tmp);
            return info?.Entrypoint;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // 关键回归：desktop 开、resume 到 cli → 必须判为 cli（发到终端，不是客户端）。
    [Fact]
    public async Task DesktopThenResumedCli_UsesLatestCli()
        => Assert.Equal("cli", await EntrypointOf("claude-desktop", "claude-desktop", "cli"));

    // 反向：cli 开、后在 desktop 继续 → 判为 desktop。
    [Fact]
    public async Task CliThenDesktop_UsesLatestDesktop()
        => Assert.Equal("claude-desktop", await EntrypointOf("cli", "claude-desktop"));

    [Fact]
    public async Task PureCli_IsCli()
        => Assert.Equal("cli", await EntrypointOf("cli", "cli", "cli"));

    [Fact]
    public async Task PureDesktop_IsDesktop()
        => Assert.Equal("claude-desktop", await EntrypointOf("claude-desktop"));
}
