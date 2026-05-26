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
        var cwd = targetCwd?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(cwd))
        {
            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c.WorkingDirectory)
                    && string.Equals(c.WorkingDirectory!.TrimEnd('\\', '/'), cwd, StringComparison.OrdinalIgnoreCase))
                    return c.Pid;
            }
        }

        if (!string.IsNullOrEmpty(targetSessionId))
        {
            foreach (var c in candidates)
            {
                if (c.SessionId == targetSessionId)
                    return c.Pid;
            }
        }

        return null;
    }
}
