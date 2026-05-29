using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenIsland.App.ViewModels;
using OpenIsland.Core;
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

    /// <summary>true = 还没拿到真实 5h 用量（探针未成功/网络不可达）。此时显示"余 --"，
    /// 绝不用猜测额度伪造百分比。</summary>
    public bool Indeterminate { get; init; }
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

    /// <summary>探针最短间隔：5 分钟（/api/oauth/usage 对频繁轮询会 429；用量本来也是慢变量）。</summary>
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

    /// <summary>功能总开关。true = 显示订阅 5 小时余额（行展开 + 后台探针跑）。</summary>
    private const bool FeatureEnabled = true;

    /// <summary>访问 /api/oauth/usage 必须带的 User-Agent；缺它会命中激进限流桶持续 429。</summary>
    private const string ClaudeCodeUserAgent = "claude-code/2.1.156";

    private static string Trunc(string? s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

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

            if (_probeFraction is double pf)
            {
                // ✅ 真实值：/api/oauth/usage 的 5h 窗口利用率
                TimeSpan? resetIn;
                if (_probeResetUtc is DateTime pr)
                {
                    var rr = pr - now;
                    resetIn = rr < TimeSpan.Zero ? TimeSpan.Zero : rr;
                }
                else
                {
                    resetIn = localReset;
                }

                double clamped = Math.Clamp(pf, 0, 1);
                UsageUpdated?.Invoke(this, new PlanUsageSnapshot
                {
                    IsApi = false,
                    UsedTokens = used5h,
                    Fraction = clamped,
                    Percent = (int)Math.Round(clamped * 100),
                    ResetIn = resetIn
                });
            }
            else
            {
                // 还没有真实数据（探针未成功 / 网络不可达 / OAuth 失效）：标记 Indeterminate，
                // UI 显示"余 --"。绝不用猜测额度伪造一个百分比（那会显示成误导性的 0% 余额）。
                UsageUpdated?.Invoke(this, new PlanUsageSnapshot
                {
                    IsApi = false,
                    UsedTokens = used5h,
                    Indeterminate = true
                });
            }
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

        // 取数：优先用系统自带 curl.exe。实测 .NET HttpClient（Framework 与 .NET 8 都试过）在
        // 本环境会"连上后挂死到超时"，而 curl 0.5s 返回 200（TLS/HTTP2/代理/IPv6 它自己处理好）。
        // curl 缺失（极老 Windows，<1803）才退回 HttpClient。
        var body = await FetchUsageBodyAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            Diag("-> no usage body -> keep fallback");
            return;
        }

        if (OAuthUsageParser.TryParseFiveHour(body, out var usedFrac, out var resetUtc))
        {
            _probeFraction = usedFrac;
            _probeResetUtc = resetUtc;
            Diag($"-> real 5h used={_probeFraction:P1} reset={(_probeResetUtc?.ToString("u") ?? "n/a")}");
        }
        else
        {
            Diag($"-> body present but five_hour not parseable: {Trunc(body, 200)}");
        }
    }

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>取订阅用量 JSON：curl.exe 优先（可靠），缺失才退回 HttpClient。绝不打印 token。</summary>
    private async Task<string?> FetchUsageBodyAsync(string token)
    {
        var curl = ResolveCurlPath();
        if (curl != null)
        {
            // curl 存在就只用 curl —— 不再退回 HttpClient（后者在本环境会挂死 ~12s）。
            try { return await FetchViaCurlAsync(curl, token).ConfigureAwait(false); }
            catch (Exception ex) { Diag($"curl fetch failed: {ex.Message}"); return null; }
        }
        try { return await FetchViaHttpClientAsync(token).ConfigureAwait(false); }
        catch (Exception ex) { Diag($"httpclient fetch failed: {ex.Message}"); return null; }
    }

    private static string? ResolveCurlPath()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (File.Exists(sys)) return sys;
        return "curl.exe"; // 交给 PATH 解析
    }

    /// <summary>
    /// 用 curl.exe 拉用量。url + header（含 token）写进临时 -K 配置文件，避免 token 出现在
    /// 命令行被其它进程窥见；用完即删。返回 200 时的 body，否则 null。
    /// </summary>
    private async Task<string?> FetchViaCurlAsync(string curlPath, string token)
    {
        var cfg = Path.Combine(Path.GetTempPath(), "oi-usage-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            var cfgText =
                $"url = \"{UsageUrl}\"\n" +
                $"header = \"Authorization: Bearer {token}\"\n" +
                "header = \"anthropic-beta: oauth-2025-04-20\"\n" +
                $"header = \"user-agent: {ClaudeCodeUserAgent}\"\n";
            await File.WriteAllTextAsync(cfg, cfgText, new UTF8Encoding(false)).ConfigureAwait(false);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = curlPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-sS");
            psi.ArgumentList.Add("-m"); psi.ArgumentList.Add("12");
            psi.ArgumentList.Add("-w"); psi.ArgumentList.Add("\n__HTTP__%{http_code}");
            psi.ArgumentList.Add("-K"); psi.ArgumentList.Add(cfg);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) { Diag("curl: process start returned null"); return null; }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { proc.Kill(true); } catch { } Diag("curl: timed out"); return null; }
            }
            var raw = await stdoutTask.ConfigureAwait(false);

            int code = 0; string bodyText = raw;
            int marker = raw.LastIndexOf("__HTTP__", StringComparison.Ordinal);
            if (marker >= 0)
            {
                bodyText = raw.Substring(0, marker).TrimEnd('\r', '\n');
                int.TryParse(raw.Substring(marker + 8).Trim(), out code);
            }
            Diag($"curl usage HTTP {code}: {Trunc(bodyText, 400)}");
            return code == 200 ? bodyText : null;
        }
        finally
        {
            try { File.Delete(cfg); } catch { }
        }
    }

    /// <summary>退路：HttpClient（部分环境会挂死，仅在 curl 缺失时用）。</summary>
    private async Task<string?> FetchViaHttpClientAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.UserAgent.TryParseAdd(ClaudeCodeUserAgent);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Diag($"httpclient usage HTTP {(int)resp.StatusCode}: {Trunc(body, 400)}");
        return resp.IsSuccessStatusCode ? body : null;
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
