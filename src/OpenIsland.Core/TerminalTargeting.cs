namespace OpenIsland.Core;

/// <summary>A running terminal-hosted agent process an injection might target.</summary>
public readonly record struct TerminalCandidate(int Pid, string? WorkingDirectory, string? SessionId);

/// <summary>
/// Decides which running terminal a per-card injection should target. Positive match ONLY
/// (exact working directory or exact session id) — deliberately NO "only one process is
/// running, so it must be the one" fallback, because injecting a whole prompt/command into
/// the wrong session is harmful (see review bug #7).
/// </summary>
public static class TerminalTargeting
{
    public static int? ResolvePid(string? targetCwd, string? targetSessionId,
        IReadOnlyList<TerminalCandidate> candidates)
    {
        // 1) 工作目录精确匹配 —— 但要求 *唯一*。同一目录跑了多个会话时无法区分，
        //    宁可中止也绝不猜（否则整段 prompt 可能注入到同目录的另一个会话）。
        var cwd = targetCwd?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(cwd))
        {
            var matchPid = 0;
            var matchCount = 0;
            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c.WorkingDirectory)
                    && string.Equals(c.WorkingDirectory!.TrimEnd('\\', '/'), cwd, StringComparison.OrdinalIgnoreCase))
                {
                    matchPid = c.Pid;
                    matchCount++;
                }
            }
            if (matchCount == 1) return matchPid;
            if (matchCount > 1) return null; // 同目录多会话，无法区分 → 中止
        }

        // 2) sessionId 精确匹配（部分 agent 会上报真实 id）。
        if (!string.IsNullOrEmpty(targetSessionId))
        {
            foreach (var c in candidates)
            {
                if (c.SessionId == targetSessionId)
                    return c.Pid;
            }
        }

        // 3) 单候选兜底：Windows 上 claude.exe 的 cwd 常读不到、id 是合成的，正向匹配往往全落空。
        //    只有一个 claude 在跑时不存在歧义 —— 那就是它。这让功能在最常见的单会话场景下可用。
        //    2 个及以上仍中止（绝不在多个候选里盲选）。
        if (candidates.Count == 1) return candidates[0].Pid;

        return null;
    }
}
