using OpenIsland.Core.Models;

namespace OpenIsland.App.ViewModels;

/// <summary>
/// Dashboard 概览统计：从 SessionManager 的 AgentSession 列表里聚合出来。
/// 跟 Sessions Tab 并列展示，给用户一个"我用 Claude 用了多少"的总观。
/// </summary>
public record DashboardStats
{
    public int SessionsCount { get; init; }
    public ulong TotalTokens { get; init; }
    public int ActiveDays { get; init; }
    public int CurrentStreak { get; init; }
    public int LongestStreak { get; init; }
    /// <summary>0-23；最活跃小时。-1 表示没数据。</summary>
    public int PeakHour { get; init; } = -1;
    public string? FavoriteModel { get; init; }
    public string? ComparisonLine { get; init; }
    public IReadOnlyList<DayActivity> Heatmap { get; init; } = Array.Empty<DayActivity>();
    public IReadOnlyList<ModelUsage> Models { get; init; } = Array.Empty<ModelUsage>();

    /// <summary>
    /// 时间范围筛选 + 聚合。window=null 取全部，>0 取最近 N 天。
    /// 活跃时间用 transcript 文件 LastWriteTime（文件系统层面的真实活动时刻）；
    /// AgentSession.UpdatedAt 不可靠 —— watcher 启动扫描时把所有 session 的 UpdatedAt 都
    /// 刷成"现在"，导致 ActiveDays / Heatmap 全坍塌到今天。
    /// </summary>
    public static DashboardStats Compute(IEnumerable<AgentSession> all, TimeSpan? window)
    {
        var cutoff = window.HasValue ? DateTime.UtcNow - window.Value : DateTime.MinValue;

        // 把每个 session 配上 transcript 的 mtime（拿不到 mtime 时回落 UpdatedAt 兜底）
        var pairs = all
            .Where(s => s.Tool == AgentTool.ClaudeCode)
            .Select(s => new SessionWithActivity(
                s,
                TryGetTranscriptMtime(s.ClaudeMetadata?.TranscriptPath) ?? s.UpdatedAt))
            .Where(p => p.ActivityAt >= cutoff)
            .ToList();

        if (pairs.Count == 0)
        {
            return new DashboardStats { Heatmap = BuildHeatmap(pairs, window) };
        }

        ulong totalTokens = 0;
        foreach (var p in pairs)
            totalTokens += p.Session.ClaudeMetadata?.TotalTokens ?? 0u;

        var datesLocal = pairs
            .Select(p => p.ActivityAt.ToLocalTime().Date)
            .ToHashSet();

        var (current, longest) = CalcStreaks(datesLocal);

        var peakHour = pairs
            .GroupBy(p => p.ActivityAt.ToLocalTime().Hour)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault() ?? -1;

        var favoriteModel = pairs
            .Where(p => !string.IsNullOrEmpty(p.Session.ClaudeMetadata?.Model))
            .GroupBy(p => p.Session.ClaudeMetadata!.Model!)
            .OrderByDescending(g => g.Sum(p => (long)(p.Session.ClaudeMetadata?.TotalTokens ?? 0u)))
            .Select(g => g.Key)
            .FirstOrDefault();

        return new DashboardStats
        {
            SessionsCount = pairs.Count,
            TotalTokens = totalTokens,
            ActiveDays = datesLocal.Count,
            CurrentStreak = current,
            LongestStreak = longest,
            PeakHour = peakHour,
            FavoriteModel = favoriteModel,
            ComparisonLine = BuildComparisonLine(totalTokens),
            Heatmap = BuildHeatmap(pairs, window),
            Models = BuildModels(pairs.Select(p => p.Session).ToList(), totalTokens)
        };
    }

    private static DateTime? TryGetTranscriptMtime(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return System.IO.File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    /// <summary>session + 它的真实活动时间（transcript mtime 或 UpdatedAt 兜底）</summary>
    private sealed record SessionWithActivity(AgentSession Session, DateTime ActivityAt);

    /// <summary>
    /// 当前连续活跃天数（含今天）+ 历史最长连续天数。
    /// 今天没活动则 current=0；昨天无活动也算断；连续天的判定用本地日期。
    /// </summary>
    private static (int current, int longest) CalcStreaks(HashSet<DateTime> activeDates)
    {
        if (activeDates.Count == 0) return (0, 0);

        var sorted = activeDates.OrderBy(d => d).ToList();
        int longest = 1, run = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if ((sorted[i] - sorted[i - 1]).TotalDays == 1) run++;
            else run = 1;
            if (run > longest) longest = run;
        }

        var today = DateTime.Now.Date;
        int current = 0;
        var d = today;
        while (activeDates.Contains(d))
        {
            current++;
            d = d.AddDays(-1);
        }

        return (current, longest);
    }

    /// <summary>
    /// 热力图：默认 84 天（12 周 × 7 行）。每天活动数按 transcript mtime 归类，5 档强度。
    /// </summary>
    private static IReadOnlyList<DayActivity> BuildHeatmap(List<SessionWithActivity> pairs, TimeSpan? window)
    {
        const int defaultDays = 84;
        int days = window.HasValue
            ? Math.Max(7, Math.Min(defaultDays, (int)window.Value.TotalDays))
            : defaultDays;

        var endDate = DateTime.Now.Date;
        var startDate = endDate.AddDays(-(days - 1));

        var perDay = new Dictionary<DateTime, int>();
        foreach (var p in pairs)
        {
            var d = p.ActivityAt.ToLocalTime().Date;
            if (d < startDate || d > endDate) continue;
            perDay[d] = perDay.TryGetValue(d, out var c) ? c + 1 : 1;
        }

        if (perDay.Count == 0)
        {
            return Enumerable.Range(0, days)
                .Select(i => new DayActivity(startDate.AddDays(i), 0))
                .ToList();
        }

        var max = perDay.Values.Max();
        var result = new List<DayActivity>(days);
        for (int i = 0; i < days; i++)
        {
            var d = startDate.AddDays(i);
            int count = perDay.TryGetValue(d, out var c) ? c : 0;
            int intensity = count == 0
                ? 0
                : (int)Math.Ceiling(4.0 * count / max); // 1..4
            result.Add(new DayActivity(d, intensity));
        }
        return result;
    }

    private static IReadOnlyList<ModelUsage> BuildModels(List<AgentSession> sessions, ulong totalTokens)
    {
        if (totalTokens == 0) return Array.Empty<ModelUsage>();

        return sessions
            .Where(s => !string.IsNullOrEmpty(s.ClaudeMetadata?.Model))
            .GroupBy(s => s.ClaudeMetadata!.Model!)
            .Select(g =>
            {
                ulong inT = 0, outT = 0, cacheR = 0, cacheC = 0;
                foreach (var s in g)
                {
                    inT += s.ClaudeMetadata?.InputTokens ?? 0u;
                    outT += s.ClaudeMetadata?.OutputTokens ?? 0u;
                    cacheR += s.ClaudeMetadata?.CacheReadTokens ?? 0u;
                    cacheC += s.ClaudeMetadata?.CacheCreationTokens ?? 0u;
                }
                // 分子 = 模型完整 token 数（含 cache），跟分母 totalTokens 口径一致 ——
                // 这样所有模型的百分比加起来 == 100%。in/out 单独显示给用户看 I/O 比例。
                ulong sum = inT + outT + cacheR + cacheC;
                return new ModelUsage(
                    g.Key,
                    inT, outT, sum,
                    totalTokens > 0 ? sum * 100.0 / totalTokens : 0
                );
            })
            .OrderByDescending(m => m.TotalTokens)
            .ToList();
    }

    private static string BuildComparisonLine(ulong totalTokens)
    {
        // 玩点小幽默对照（参考图里的"~79× more tokens than The Lord of the Rings"）
        if (totalTokens < 100_000) return "";
        const ulong lotrTokens = 575_000UL;
        var ratio = totalTokens / (double)lotrTokens;
        if (ratio < 1) return "";
        return $"You've used ~{ratio:0.#}× more tokens than The Lord of the Rings.";
    }
}

public record DayActivity(DateTime Date, int Intensity);

public record ModelUsage(
    string Name,
    ulong InputTokens,
    ulong OutputTokens,
    ulong TotalTokens,
    double Percentage);
