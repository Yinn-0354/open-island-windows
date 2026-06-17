using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace OpenIsland.App.Services;

/// <summary>
/// 一次"检查更新"的结果快照。
/// Snapshot describing the outcome of a single update check.
/// </summary>
public record UpdateInfo
{
    /// <summary>是否存在比当前更新的版本。/ Whether a newer version than the running build exists.</summary>
    public bool HasUpdate { get; init; }

    /// <summary>当前运行版本（X.Y.Z）。/ Currently running version (X.Y.Z).</summary>
    public string CurrentVersion { get; init; } = "";

    /// <summary>Release 里的最新版本（X.Y.Z，已去掉前导 'v'）。/ Latest version from the release (X.Y.Z, leading 'v' stripped).</summary>
    public string LatestVersion { get; init; } = "";

    /// <summary>更新日志（Release body 原文）。/ Release notes (raw release body text).</summary>
    public string Notes { get; init; } = "";

    /// <summary>最新版 Setup .exe 的官方下载地址；找不到匹配资产时为 null。/ Official download URL of the latest Setup .exe; null when no matching asset.</summary>
    public string? SetupUrl { get; init; }
}

/// <summary>
/// 在线升级服务：查 GitHub Release → 比对版本 → 下载 Setup.exe（GitHub 原生 + 镜像兜底）→ 静默安装并重启。
///
/// 复用现成的 Inno 安装包（installer/openisland.iss）：固定 AppId + 装 %LOCALAPPDATA% +
/// PrivilegesRequired=lowest + CloseApplications=force + RestartApplications=yes，
/// 因此这里只需把 Setup.exe 拉下来用 /VERYSILENT 跑起来，安装器自己会关旧版、覆盖装、重启新版。
///
/// Online update service: queries the latest GitHub release, compares versions, downloads the
/// Setup.exe (GitHub native URL first, then acceleration mirrors as fallback), then runs it
/// silently and lets the installer close/replace/restart the app.
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>GitHub releases/latest API。/ GitHub releases/latest API endpoint.</summary>
    private const string LatestReleaseApi =
        "https://api.github.com/repos/ludiwangfpga/open-island-windows/releases/latest";

    /// <summary>GitHub API 必须带 UA，否则 403。/ GitHub API requires a User-Agent header or it returns 403.</summary>
    private const string UserAgent = "OpenIsland-Updater";

    /// <summary>
    /// 下载加速镜像前缀：拼成 "&lt;前缀&gt;&lt;原始 https url&gt;"。原生失败/超时时依次尝试。
    /// Acceleration mirror prefixes, composed as "&lt;prefix&gt;&lt;original https url&gt;";
    /// tried in order when the native GitHub URL fails or stalls.
    /// </summary>
    private static readonly string[] MirrorPrefixes =
    {
        "https://ghproxy.net/",
        "https://mirror.ghproxy.com/",
        "https://gh.ddlc.top/",
        "https://github.moeyy.xyz/",
    };

    /// <summary>启动后延迟多久做一次静默检查（仿 PlanUsageService 的延迟触发，给主界面留出加载时间）。
    /// Delay before the one-shot silent startup check (mirrors PlanUsageService's deferred kickoff).</summary>
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(8);

    /// <summary>下载时单个来源多久无进展即放弃换下一个。/ Per-source stall timeout: abandon a source if no bytes arrive within this window.</summary>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>校验下载完整性的最小体积下限（Setup.exe 远大于 1MB）。/ Minimum size sanity check (the real Setup.exe is far larger than 1MB).</summary>
    private const long MinSetupBytes = 1024 * 1024;

    /// <summary>诊断日志路径，仿 SkillInstall/PlanUsage 的 Diag 习惯。绝不写 token 之类敏感信息。
    /// Diagnostic log path (same convention as SkillInstall/PlanUsage). Never logs secrets.</summary>
    private static readonly string DiagPath = Path.Combine(Path.GetTempPath(), "openisland-update.log");

    /// <summary>检查用 HttpClient：带默认 UA 头，超时偏短（仅取一小段 JSON）。
    /// HttpClient for the API check: default UA header, short timeout (it only fetches a small JSON).</summary>
    private static readonly HttpClient _checkHttp = CreateHttp(TimeSpan.FromSeconds(15));

    /// <summary>下载用 HttpClient：无总超时（大文件靠每源的 stall 超时控制），同样带 UA。
    /// HttpClient for downloads: no overall timeout (large file; governed by the per-source stall timeout instead).</summary>
    private static readonly HttpClient _downloadHttp = CreateHttp(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = timeout
        };
        // GitHub API / 镜像都需要 UA；缺它 GitHub 直接 403。
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private CancellationTokenSource? _startupCts;

    /// <summary>发现新版本时触发（仅 HasUpdate=true 才 raise）。marshal 到 UI 线程的事由订阅方（VM）做。
    /// Raised only when a newer version is found. Marshalling to the UI thread is the subscriber's job (the VM).</summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    private static void Diag(string msg)
    {
        try { File.AppendAllText(DiagPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    /// <summary>
    /// 启动后延迟几秒在后台静默检查一次；发现新版即 raise <see cref="UpdateAvailable"/>。
    /// 不阻塞调用方（构造期可直接调），任何异常都被吞掉只记日志。
    ///
    /// Kicks off a single deferred background check a few seconds after startup; raises
    /// <see cref="UpdateAvailable"/> if a newer version is found. Fire-and-forget; never throws.
    /// </summary>
    public void StartSilentCheck()
    {
        _startupCts?.Cancel();
        _startupCts = new CancellationTokenSource();
        var token = _startupCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupCheckDelay, token).ConfigureAwait(false);
                var info = await CheckForUpdateAsync(token).ConfigureAwait(false);
                if (info.HasUpdate)
                {
                    Diag($"silent check: update available {info.CurrentVersion} -> {info.LatestVersion}");
                    UpdateAvailable?.Invoke(this, info);
                }
                else
                {
                    Diag($"silent check: up to date (current={info.CurrentVersion}, latest={info.LatestVersion})");
                }
            }
            catch (OperationCanceledException) { /* 应用退出 / 重新触发，正常 */ }
            catch (Exception ex) { Diag($"silent check error: {ex.GetType().Name}: {ex.Message}"); }
        }, token);
    }

    /// <summary>
    /// 查 GitHub 最新 Release，解析版本/日志/Setup 资产，与本地 AssemblyVersion 比较。
    /// 任何网络/解析异常都被吞掉，返回 HasUpdate=false（不抛）。
    ///
    /// Queries the latest GitHub release, parses version/notes/Setup asset, and compares against
    /// the local AssemblyVersion. Any network/parse error is swallowed and returns HasUpdate=false.
    /// </summary>
    public async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = GetLocalVersion();
        var currentStr = $"{current.Major}.{current.Minor}.{current.Build}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            // GitHub 推荐的 Accept 头（明确请求 v3 JSON）。
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await _checkHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Diag($"check: HTTP {(int)resp.StatusCode} from releases/latest");
                return new UpdateInfo { HasUpdate = false, CurrentVersion = currentStr };
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // tag_name 如 "v0.5.0" —— 去前导 'v' 再解析。
            string rawTag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string latestStr = rawTag.TrimStart('v', 'V').Trim();

            string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

            // 在 assets[] 里找 name 形如 OpenIsland-Setup-*.exe 的资产，取其 browser_download_url。
            string? setupUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (name.StartsWith("OpenIsland-Setup-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setupUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(setupUrl)) break;
                    }
                }
            }

            // 版本比较：用 System.Version 解析 latest tag。解析失败 → 稳妥兜底 HasUpdate=false，不抛。
            bool hasUpdate = false;
            if (TryParseVersion(latestStr, out var latest))
            {
                // 只比 Major.Minor.Build（tag 通常三段，本地是四段含 .0 revision）。
                var latestNorm = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
                var currentNorm = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
                hasUpdate = latestNorm > currentNorm;
            }
            else
            {
                Diag($"check: cannot parse latest tag '{rawTag}' -> treat as no update");
            }

            // 有新版但没找到 Setup 资产 → 无法安装，降级成"无更新"避免 UI 卡在没法点的状态。
            if (hasUpdate && string.IsNullOrWhiteSpace(setupUrl))
            {
                Diag($"check: latest {latestStr} > {currentStr} but no Setup asset -> cannot update");
                hasUpdate = false;
            }

            return new UpdateInfo
            {
                HasUpdate = hasUpdate,
                CurrentVersion = currentStr,
                LatestVersion = string.IsNullOrWhiteSpace(latestStr) ? currentStr : latestStr,
                Notes = notes,
                SetupUrl = setupUrl,
            };
        }
        catch (OperationCanceledException)
        {
            throw; // 取消透传给上游（启动检查的 Task.Delay / VM 的命令取消）
        }
        catch (Exception ex)
        {
            Diag($"check error: {ex.GetType().Name}: {ex.Message}");
            return new UpdateInfo { HasUpdate = false, CurrentVersion = currentStr };
        }
    }

    /// <summary>
    /// 下载 Setup.exe（原生 URL 优先，失败依次走镜像前缀）到 %TEMP%，校验体积 + PE 魔数后
    /// /VERYSILENT 静默安装并退出本进程让安装器接管。全部来源都失败返回 false。
    ///
    /// Downloads the Setup.exe (native URL first, mirrors as fallback) to %TEMP%, validates size +
    /// PE magic, launches it with /VERYSILENT, then shuts this process down so the installer can
    /// close/replace/restart the app. Returns false if every source fails.
    /// </summary>
    /// <param name="setupUrl">CheckForUpdateAsync 返回的官方 Setup 下载地址。/ Official Setup URL from CheckForUpdateAsync.</param>
    /// <param name="progress">下载进度回调 0..1（拿不到 ContentLength 时可能不连续）。/ Download progress callback 0..1 (may be sparse without ContentLength).</param>
    /// <param name="ct">取消令牌（UI "稍后"/退出可中断）。/ Cancellation (e.g. user dismiss / app exit).</param>
    public async Task<bool> DownloadAndInstallAsync(string setupUrl, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(setupUrl))
        {
            Diag("download: empty setupUrl");
            return false;
        }

        // 从原始 URL 末段拿一个稳定文件名（含版本号）；解析失败用固定名兜底。
        string fileName;
        try { fileName = Path.GetFileName(new Uri(setupUrl).AbsolutePath); }
        catch { fileName = "OpenIsland-Setup.exe"; }
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "OpenIsland-Setup.exe";
        var targetPath = Path.Combine(Path.GetTempPath(), fileName);

        // 候选来源：原生 URL + 各镜像前缀拼接（前缀 + 原始 https url）。任一成功即停。
        var sources = new List<string> { setupUrl };
        foreach (var prefix in MirrorPrefixes)
            sources.Add(prefix + setupUrl);

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Diag($"download: trying {Truncate(source, 80)}");
                if (await TryDownloadAsync(source, targetPath, progress, ct).ConfigureAwait(false)
                    && IsValidSetup(targetPath))
                {
                    Diag($"download: success via {Truncate(source, 80)} ({new FileInfo(targetPath).Length} bytes)");
                    return LaunchInstallerAndExit(targetPath);
                }
                Diag($"download: source produced no valid file, next");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户/应用主动取消：透传，别继续试别的源
            }
            catch (Exception ex)
            {
                // 单源失败（含 stall 超时）→ 记日志换下一个源。
                Diag($"download: source failed ({ex.GetType().Name}: {ex.Message}), next");
            }
        }

        Diag("download: all sources failed");
        return false;
    }

    /// <summary>
    /// 从单个来源流式下载到目标文件，带进度回调；每 <see cref="DownloadStallTimeout"/> 无新字节即超时换源。
    /// Streams one source into the target file with progress; times out a source that stalls.
    /// </summary>
    private async Task<bool> TryDownloadAsync(string url, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _downloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Diag($"download: HTTP {(int)resp.StatusCode}");
            return false;
        }

        long? total = resp.Content.Headers.ContentLength;
        await using var http = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            // "无进展即换源"：每次读取套一个 stall 超时令牌（与外部取消联动）。
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(DownloadStallTimeout);

            int n = await http.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false);
            if (n == 0) break;

            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is long len && len > 0)
                progress?.Report(Math.Clamp((double)read / len, 0, 1));
        }

        await file.FlushAsync(ct).ConfigureAwait(false);
        progress?.Report(1.0);
        return read > 0;
    }

    /// <summary>体积 &gt; 1MB 且以 PE 头 "MZ" 开头才认是有效安装器（防镜像返回 HTML 错误页冒充 exe）。
    /// Valid only if &gt; 1MB and starts with the PE "MZ" magic (guards against a mirror serving an HTML error page).</summary>
    private static bool IsValidSetup(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MinSetupBytes) return false;
            using var fs = File.OpenRead(path);
            int b0 = fs.ReadByte(), b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动静默安装器并让本进程退出 —— 给安装器 ~1s 起步窗口，再 Application.Shutdown()。
    /// 配合 iss 的 CloseApplications=force + RestartApplications=yes，安装器关旧版、覆盖装、重启新版。
    /// Launches the silent installer and exits this process (~1s grace), so the installer can take over.
    /// </summary>
    private static bool LaunchInstallerAndExit(string setupPath)
    {
        try
        {
            // UseShellExecute=true：以普通用户身份直接跑 exe（安装到 %LOCALAPPDATA% 无需提权）。
            var psi = new ProcessStartInfo(setupPath, "/VERYSILENT /SUPPRESSMSGBOXES")
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            Diag("install: launched silent installer, shutting down");

            // 给安装器一点起步时间再退（CloseApplications=force 会等本进程退出后覆盖装）。
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    try { System.Windows.Application.Current.Shutdown(); } catch { }
                });
            });
            return true;
        }
        catch (Exception ex)
        {
            Diag($"install: failed to launch ({ex.Message})");
            return false;
        }
    }

    /// <summary>本地版本 = 当前程序集 AssemblyVersion（来自 Directory.Build.props）。
    /// Local version from the executing assembly's AssemblyVersion (set in Directory.Build.props).</summary>
    private static Version GetLocalVersion()
        => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>稳妥解析版本字符串（仅取 X.Y.Z 三段前缀，忽略 -beta 等后缀）。失败返回 false 不抛。
    /// Lenient version parse (X.Y.Z prefix only, ignores -beta suffixes). Returns false on failure, never throws.</summary>
    private static bool TryParseVersion(string? s, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;

        // 截掉 '-'/'+' 之后的预发布/构建元数据（如 0.5.0-rc1），只留数字段。
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        var core = cut >= 0 ? s[..cut] : s;
        return Version.TryParse(core, out version!);
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    public void Dispose()
    {
        try { _startupCts?.Cancel(); _startupCts?.Dispose(); } catch { }
    }
}
