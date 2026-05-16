using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenIsland.App.ViewModels;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Services;

/// <summary>
/// Claude 订阅 "5 小时滚动窗口" 用量快照，给灵动岛 "Plan usage" 行用。
///
/// - Plan 模式：真实用量优先走 Anthropic "unified rate limit" 响应头（用 ~/.claude/.credentials.json
///   里的 OAuth token 探一次，零/极小 token 开销），拿到 = 和 Claude Desktop 显示的一致、自动、
///   不需手动校准。探不到（无 token / 过期 / 离线 / 头不含数字）才退回 "最近 5h 累计 token /
///   固定额度估算"（绝不再用自指分母——那会恒等 100%）。
/// - API 模式（ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN）：只报终身累计 token，无进度条/重置。
/// </summary>
public record PlanUsageSnapshot
{
    public bool IsApi { get; init; }
    public ulong UsedTokens { get; init; }
    public double Fraction { get; init; }
    public int Percent { get; init; }
    public TimeSpan? ResetIn { get; init; }
}

public sealed class PlanUsageService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly SessionManager _sessionManager;
    private readonly WorkspaceSettings _settings;

    private static readonly HttpClient _http = new(new HttpClientHandler())
    { Timeout = TimeSpan.FromSeconds(12) };

    // 探针结果缓存（由后台探针线程写，Sample 读）。
    private volatile bool _probeRunning;
    private double? _probeFraction;     // 0..1，真实 5h 利用率
    private DateTime? _probeResetUtc;   // 5h 窗口绝对重置时刻 (UTC)
    private DateTime _lastProbeUtc = DateTime.MinValue;

    /// <summary>探针失败时的兜底分母（粗略，仅占位防 100%；真实值永远以探针为准）。</summary>
    private const ulong FallbackBudget5h = 220_000_000UL;

    /// <summary>探针最短间隔：5 分钟（API 调用，别频繁；用量本来也是慢变量）。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    private static readonly string DiagPath = Path.Combine(Path.GetTempPath(), "openisland-usage.log");

    public event EventHandler<PlanUsageSnapshot>? UsageUpdated;

    public PlanUsageService(SessionManager sessionManager, WorkspaceSettings settings)
    {
        _sessionManager = sessionManager;
        _settings = settings;

        _timer = new System.Timers.Timer(15000) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
        // 暂时取消 Plan 功能：不启动定时器/探针/日志（灵动岛那行也已折叠）。
        // 想恢复：把 FeatureEnabled 改回 true + 删 XAML 里 Plan Border 的 Visibility="Collapsed"。
        if (!FeatureEnabled) return;
        _timer.Start();
        Sample();
    }

    /// <summary>功能总开关。false = 整个 Plan usage 功能停用（行折叠 + 后台不跑）。</summary>
    private const bool FeatureEnabled = false;

    private static void Diag(string msg)
    {
        try { File.AppendAllText(DiagPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    private void Sample()
    {
        try
        {
            bool isApi =
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

            var sessions = _sessionManager.GetAllSessions();

            if (isApi)
            {
                ulong lifetime = DashboardStats.Compute(sessions, null).TotalTokens;
                UsageUpdated?.Invoke(this, new PlanUsageSnapshot
                {
                    IsApi = true,
                    UsedTokens = lifetime,
                    Fraction = 0,
                    Percent = 0,
                    ResetIn = null
                });
                return;
            }

            // 最近 5h 累计 token（口径与 Dashboard 一致）。
            ulong used5h = DashboardStats.Compute(sessions, TimeSpan.FromHours(5)).TotalTokens;

            // 本地估算的重置倒计时（窗口内最早活动满 5h 被释放）。探针拿到精确重置时优先用探针。
            var now = DateTime.UtcNow;
            var windowStart = now - TimeSpan.FromHours(5);
            DateTime? oldestInWindow = null;
            foreach (var s in sessions)
            {
                if (s.Tool != AgentTool.ClaudeCode) continue;
                var activity = TryGetMtime(s.ClaudeMetadata?.TranscriptPath) ?? s.UpdatedAt;
                if (activity < windowStart) continue;
                if (oldestInWindow == null || activity < oldestInWindow.Value)
                    oldestInWindow = activity;
            }
            TimeSpan? localReset = null;
            if (oldestInWindow != null)
            {
                var rem = TimeSpan.FromHours(5) - (now - oldestInWindow.Value);
                localReset = rem < TimeSpan.Zero ? TimeSpan.Zero : rem;
            }
            else
            {
                used5h = 0;
            }

            // 触发探针（节流 5min，后台跑，不阻塞计时器）。
            MaybeProbe();

            double frac;
            TimeSpan? resetIn;
            if (_probeFraction is double pf)
            {
                // ✅ 真实值：Anthropic 限流头
                frac = pf;
                if (_probeResetUtc is DateTime pr)
                {
                    var rr = pr - now;
                    resetIn = rr < TimeSpan.Zero ? TimeSpan.Zero : rr;
                }
                else
                {
                    resetIn = localReset;
                }
            }
            else
            {
                // 兜底：固定额度估算（绝不自指）。优先用 settings.json 里**实时**读到的
                // plan5hTokenBudget（我按一个真实数据点校准好写进去 → 纯自动、随用量按比例走、
                // 不用重启/重部署即可生效）；没有才用内置常量。
                ulong budget = ReadLiveBudget() ?? FallbackBudget5h;
                frac = budget == 0 ? 0 : used5h / (double)budget;
                resetIn = localReset;
                Diag($"fallback: used5h={used5h} budget={budget} -> {(int)Math.Round(Math.Clamp(frac, 0, 1) * 100)}%");
            }

            double clamped = Math.Clamp(frac, 0, 1);
            UsageUpdated?.Invoke(this, new PlanUsageSnapshot
            {
                IsApi = false,
                UsedTokens = used5h,
                Fraction = clamped,
                Percent = (int)Math.Round(clamped * 100),
                ResetIn = resetIn
            });
        }
        catch
        {
            /* 单次采样失败不崩计时器 */
        }
    }

    /// <summary>5min 节流 + 不重入地后台探一次真实用量。</summary>
    private void MaybeProbe()
    {
        if (_probeRunning) return;
        if (DateTime.UtcNow - _lastProbeUtc < ProbeInterval) return; // 5min 节流（即使没成功也别每 15s 空打）
        _probeRunning = true;
        _ = Task.Run(async () =>
        {
            try { await ProbeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Diag($"probe error: {ex.GetType().Name}: {ex.Message}"); }
            finally { _lastProbeUtc = DateTime.UtcNow; _probeRunning = false; }
        });
    }

    /// <summary>
    /// 读 OAuth token，向 Anthropic 发一个最小请求，解析 `anthropic-ratelimit-unified-*`
    /// 响应头算出真实 5h 利用率 + 精确重置时刻。所有限流头落 openisland-usage.log 以便对照/收窄。
    /// 绝不打印 token 本身。
    /// </summary>
    private async Task ProbeAsync()
    {
        string? token = null;
        try
        {
            if (!File.Exists(CredentialsPath)) { Diag("no .credentials.json -> fallback"); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            var root = doc.RootElement;
            JsonElement oauth = root.TryGetProperty("claudeAiOauth", out var o) ? o : root;
            if (oauth.TryGetProperty("accessToken", out var at)) token = at.GetString();
            if (oauth.TryGetProperty("expiresAt", out var ea) && ea.TryGetInt64(out var expMs))
            {
                var exp = DateTimeOffset.FromUnixTimeMilliseconds(expMs).UtcDateTime;
                if (exp <= DateTime.UtcNow) { Diag("oauth token expired -> fallback"); return; }
            }
        }
        catch (Exception ex) { Diag($"cred parse failed: {ex.Message} -> fallback"); return; }
        if (string.IsNullOrEmpty(token)) { Diag("no accessToken -> fallback"); return; }

        // 最小消息请求；限流头在边缘即返回（即便 4xx 也有），max_tokens:1 token 开销可忽略。
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Content = new StringContent(
            "{\"model\":\"claude-3-5-haiku-20241022\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\".\"}]}",
            Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        var rl = new List<string>();
        long? limit = null, remaining = null;
        DateTime? resetUtc = null;
        double? statusFraction = null;

        foreach (var h in resp.Headers)
        {
            if (!h.Key.StartsWith("anthropic-ratelimit", StringComparison.OrdinalIgnoreCase)) continue;
            var val = string.Join(",", h.Value);
            rl.Add($"{h.Key}={val}");
            var key = h.Key.ToLowerInvariant();

            // 优先 5h 窗口的 limit/remaining；没有则吃 unified 通用的。
            if (key.Contains("unified") && key.EndsWith("limit") && long.TryParse(val, out var lv)) limit = lv;
            if (key.Contains("unified") && key.EndsWith("remaining") && long.TryParse(val, out var rv)) remaining = rv;
            if (key.Contains("unified") && key.Contains("reset"))
            {
                if (DateTimeOffset.TryParse(val, out var dto)) resetUtc = dto.UtcDateTime;
                else if (long.TryParse(val, out var secs))
                    resetUtc = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            }
            // 退路：unified-status = allowed / allowed_warning / rejected
            if (key.Contains("unified") && key.Contains("status"))
                statusFraction = val.Contains("reject", StringComparison.OrdinalIgnoreCase) ? 1.0
                    : val.Contains("warning", StringComparison.OrdinalIgnoreCase) ? 0.85 : (double?)null;
        }

        Diag(rl.Count == 0
            ? $"HTTP {(int)resp.StatusCode}: no anthropic-ratelimit headers -> fallback"
            : $"HTTP {(int)resp.StatusCode}: {string.Join(" | ", rl)}");

        if (limit is long L && L > 0 && remaining is long Rem)
        {
            _probeFraction = Math.Clamp(1.0 - (double)Rem / L, 0, 1);
            _probeResetUtc = resetUtc;
            Diag($"-> real fraction={_probeFraction:P1} reset={(_probeResetUtc?.ToString("u") ?? "n/a")}");
        }
        else if (statusFraction is double sf)
        {
            _probeFraction = sf;
            _probeResetUtc = resetUtc;
            Diag($"-> status-only fraction~{sf:P0} (no limit/remaining headers)");
        }
        else
        {
            Diag("-> headers present but no numeric limit/remaining/status -> keep fallback");
        }
    }

    /// <summary>
    /// 实时从 %APPDATA%\OpenIsland\settings.json 读 plan5hTokenBudget（>0 才返回）。
    /// 按真实数据点校准后只改这个文件就生效，无需重启 / 重部署。
    /// </summary>
    private static ulong? ReadLiveBudget()
    {
        try
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenIsland", "settings.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("plan5hTokenBudget", out var b))
            {
                if (b.ValueKind == JsonValueKind.Number && b.TryGetUInt64(out var v) && v > 0) return v;
                if (b.ValueKind == JsonValueKind.String && ulong.TryParse(b.GetString(), out var sv) && sv > 0) return sv;
            }
        }
        catch { }
        return null;
    }

    private static DateTime? TryGetMtime(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
