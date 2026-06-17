using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenIsland.Core;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Services;

/// <summary>
/// 网页同步服务 —— 手机/平板在同一局域网打开 http://本机IP:18686/ 即可实时查看
/// CLI 与桌面端 Claude 会话（标题 / 状态 / 最近对话气泡，2.5s 轮询），并能直接在
/// 网页上回复：POST /api/send 复用灵动岛"快捷回复"的注入通道
/// （SessionManager.SendQuickReplyAsync → 终端 SendInput / 桌面端 UIA）。
///
/// 为什么用 TcpListener 手写迷你 HTTP 而不是 HttpListener：HttpListener 监听非
/// localhost 前缀（http://+:18686/）需要 URL ACL（netsh http add urlacl）或管理员
/// 权限，普通用户双击启动直接 Access Denied；TcpListener 直接绑端口没有这个限制。
///
/// 注意：第一次 Start() 监听 0.0.0.0 时 Windows 防火墙可能弹"允许访问"对话框 ——
/// 属正常行为，用户点允许后局域网设备才能连进来（仅本机访问则无需允许）。
/// </summary>
public sealed class WebSyncService : IDisposable
{
    private const int Port = 18686;

    // ── DoS/Slowloris 护栏（bug ③）──────────────────────────────────────────
    // 全局并发连接上限：accept 后先抢信号量，抢不到直接拒绝（回 503 并关），挡住海量半开连接。
    private const int MaxConcurrentConnections = 32;
    // 单 IP 并发连接上限：单台设备/单个攻击源最多占这么多槽，防一个 IP 吃光全局额度。
    private const int MaxConnectionsPerIp = 8;
    // SSE 长连接池上限：超过即拒绝新 EventSource（每条都常驻，必须封顶）。
    private const int MaxSseClients = 24;
    // 单请求"整请求时长预算"：从拿到连接到读完头+body 累计超过这个时长就断（不只靠单次 ReadTimeout，
    // 防 Slowloris 用"每隔几秒发一字节"把单次读超时永远撑不到的慢速攻击）。
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _connGate = new(MaxConcurrentConnections, MaxConcurrentConnections);
    // 每个远端 IP 当前占用的连接数（HandleClient 进出各 +/-1）
    private readonly ConcurrentDictionary<string, int> _ipConns = new();

    // 网页同步访问令牌（持久化在 WorkspaceSettings.WebSyncToken）：GetUrl 带进 ?t=，
    // 所有 /api/* 请求据此鉴权。运行期不变，缓存成 byte[] 供常量时间比较。
    private readonly WorkspaceSettings _settings;
    private readonly byte[] _tokenBytes;

    private readonly SessionManager _sessionManager;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // ── SSE（/api/events）长连接池：HandleClient 把连接所有权移交进来后就不再关，
    // 心跳/广播写失败时才移除并 Dispose。Guid 仅作字典键，无业务含义。
    // WriteLock 按连接持有 —— 心跳 Timer 和去抖广播 Timer 可能同时写同一个
    // NetworkStream（交错出坏帧），但锁必须是每连接粒度：全局一把锁会让一个发送
    // 缓冲卡满的客户端（锁屏弱网手机，WriteTimeout 10s）队头阻塞其余所有客户端。
    private readonly ConcurrentDictionary<Guid, (TcpClient Client, NetworkStream Stream, object WriteLock)> _sseClients = new();
    // Start/Stop/异常退出收尾 跨线程互斥；IsRunning 的回写也在此锁内（代际守卫见 StopInternal）
    private readonly object _lifecycleLock = new();
    private System.Threading.Timer? _sseHeartbeat;   // 15s 周期心跳（": ping\n\n" 注释帧）
    private System.Threading.Timer? _notifyDebounce; // 250ms 去抖单发：连发 SessionsChanged 只广播一次
    private readonly object _notifyLock = new();
    private static readonly byte[] PingFrame = Encoding.ASCII.GetBytes(": ping\n\n");

    // /icon.png 懒加载缓存：null=未加载，空数组=加载失败（缓存失败避免每次请求都走 Dispatcher）
    private byte[]? _iconPng;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// accept 循环非 Stop() 路径崩溃并完成自清理后触发（线程池线程上）。
    /// VM 订阅它把地球按钮拨回"关"—— 否则界面显示开启而服务实际已死，网页静默冻结。
    /// </summary>
    public event EventHandler? StoppedUnexpectedly;

    // ── 选中同步集合：点灵动岛卡片状态圆点把会话加入/移出。集合非空时网页**只显示**
    // 选中的会话（每个带 60 条完整历史，多个并行展示）；空集合 = 默认显示最近 8 条会话。
    private readonly object _selLock = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>切换某会话的选中状态，返回切换后是否选中。</summary>
    public bool ToggleSelected(string id)
    {
        lock (_selLock)
        {
            if (_selected.Remove(id)) return false;
            _selected.Add(id);
            return true;
        }
    }

    public bool IsSelected(string id) { lock (_selLock) return _selected.Contains(id); }

    public void ClearSelected() { lock (_selLock) _selected.Clear(); }

    private string[] SelectedSnapshot() { lock (_selLock) return _selected.ToArray(); }

    // 最近一帧 5h 订阅余额快照（PlanUsageService 15s 推一帧；网页底部工具行显示）
    private volatile PlanUsageSnapshot? _lastUsage;

    public WebSyncService(SessionManager sessionManager, PlanUsageService planUsage, WorkspaceSettings settings)
    {
        _sessionManager = sessionManager;
        _settings = settings;
        // 令牌运行期不变（WorkspaceSettings 首次启动已确保生成）：缓存其 ASCII 字节，供常量时间比较。
        _tokenBytes = Encoding.ASCII.GetBytes(_settings.WebSyncToken ?? "");
        // 会话有任何变化（阶段切换/权限请求/新会话…）就推 SSE —— 网页端不用再 2.5s 盲轮询。
        // IsRunning 守门：服务没开时不必启动去抖定时器。
        _sessionManager.SessionsChanged += (_, _) => { if (IsRunning) NotifyChanged(); };
        // 余额变化也推一帧（stats 里捎带 usage 字段），手机端余额条与岛同步刷新
        planUsage.UsageUpdated += (_, snap) => { _lastUsage = snap; if (IsRunning) NotifyChanged(); };
    }

    /// <summary>开始监听。端口被占用等失败会抛异常，调用方负责把消息呈现给用户。</summary>
    public void Start()
    {
        if (IsRunning) return;

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start(); // 首次监听 0.0.0.0 可能触发防火墙"允许访问"弹窗（见类注释）
        var cts = new CancellationTokenSource();
        lock (_lifecycleLock)
        {
            // 防御：上一代若异常退出未走完清理（或清理与本次 Start 竞速），先把残留的
            // 心跳/CTS 收掉再覆写 —— 否则旧 Timer 永久泄漏并继续向池子重复发 ping。
            try { _sseHeartbeat?.Dispose(); } catch { }
            _sseHeartbeat = null;
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }

            _listener = listener;
            _cts = cts;
            IsRunning = true;

            // SSE 心跳：15s 给所有长连接写一帧注释（": ping"）——既防中间设备掐空闲连接，
            // 也是唯一能发现"手机锁屏悄悄断开"的途径（写失败 → 移除并 Dispose）。
            _sseHeartbeat = new System.Threading.Timer(
                _ => SseBroadcast(PingFrame), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        // 后台 accept 循环：每个客户端丢线程池单独处理，循环本身只管收新连接。
        // Stop() 时 cancel + listener.Stop() 会让 AcceptTcpClientAsync 抛异常退出循环。
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException) { break; }  // Stop() 取消 —— 正常退出
                catch (ObjectDisposedException) { break; }     // Stop() 关 listener —— 正常退出
                catch (SocketException)
                {
                    // backlog 里的连接被对端 RST（WSAECONNRESET）等瞬态错误：跳过这次
                    // accept 继续收新连接（对齐 Kestrel 的处理），绝不让循环静默死掉。
                    if (cts.Token.IsCancellationRequested) break;
                    continue;
                }
                catch { break; }

                // bug ③ DoS：全局并发上限。抢不到信号量（已 32 条在处理）直接拒绝并关，
                // 不让海量半开/慢速连接堆死线程池。短促回个 503 让正常客户端能看懂。
                if (!_connGate.Wait(0))
                {
                    try
                    {
                        using var ns = client.GetStream();
                        ns.WriteTimeout = 1000;
                        var busy = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        ns.Write(busy, 0, busy.Length);
                    }
                    catch { }
                    try { client.Dispose(); } catch { }
                    continue;
                }
                // HandleClientGated 在它的 finally 里成对归还信号量与单 IP 计数（无论如何退出）。
                _ = Task.Run(() => HandleClientGated(client));
            }
            // 非 Stop() 路径的异常退出：做与 Stop() 等价的**完整**清理（停心跳/取消 CTS/
            // 关 listener/清 SSE 池）。只置 IsRunning=false 是不够的 —— 心跳还活着会让
            // EventSource 看起来健康（不触发 onerror、网页不降级轮询），但广播全部 no-op，
            // 页面静默冻结。代际守卫：迟到的清理不许踩新 Start() 的状态。
            if (!cts.Token.IsCancellationRequested && StopInternal(cts))
                StoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>停止监听并断开所有处理中的连接（含 SSE 长连接）。重复调用安全。</summary>
    public void Stop() => StopInternal(null);

    /// <summary>
    /// 完整清理。onlyIfCurrent 非空 = 代际守卫：仅当它仍是当前 _cts 时才执行
    /// （accept 循环崩溃路径用 —— 若用户已 Stop+Start 换了代，这次迟到的清理直接放弃，
    /// 不许把新实例的 IsRunning 踩成 false / Dispose 新实例的定时器）。
    /// 返回是否真的执行了清理。
    /// </summary>
    private bool StopInternal(CancellationTokenSource? onlyIfCurrent)
    {
        lock (_lifecycleLock)
        {
            if (onlyIfCurrent != null && !ReferenceEquals(_cts, onlyIfCurrent)) return false;

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _cts = null;
            _listener = null;

            // 停心跳与去抖定时器 —— Stop 后不再向任何客户端推送
            try { _sseHeartbeat?.Dispose(); } catch { }
            _sseHeartbeat = null;
            lock (_notifyLock)
            {
                try { _notifyDebounce?.Dispose(); } catch { }
                _notifyDebounce = null;
            }

            // 主动断开所有 SSE 长连接并清空池（这些连接不归 HandleClient 管，必须在这里收尾）
            foreach (var key in _sseClients.Keys.ToArray())
            {
                if (_sseClients.TryRemove(key, out var c))
                {
                    try { c.Stream.Dispose(); } catch { }
                    try { c.Client.Dispose(); } catch { }
                }
            }
            return true;
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 返回局域网可访问的地址：取第一个 Up 且非回环/非虚拟网卡的 IPv4 单播地址。
    /// 排除常见虚拟网卡（Hyper-V/VMware/VirtualBox/vEthernet）—— 它们的网段手机进不来。
    /// 找不到合适网卡时回退 localhost（至少本机浏览器能用）。
    /// URL 末尾带 ?t={token}：扫码/复制地址即带访问令牌，网页加载后存进 localStorage 并据此鉴权。
    /// </summary>
    public string GetUrl()
    {
        // 令牌作为查询参数附在 / 之后（形如 http://192.168.1.5:18686/?t=abcd...）
        var q = $"?t={_settings.WebSyncToken}";
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var desc = nic.Description ?? "";
                var name = nic.Name ?? "";
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address))
                    {
                        return $"http://{addr.Address}:{Port}/{q}";
                    }
                }
            }
        }
        catch { /* 网卡枚举失败走 localhost 兜底 */ }
        return $"http://localhost:{Port}/{q}";
    }

    /// <summary>
    /// HandleClient 的外壳：持有 bug ③ 的并发额度。进来时全局信号量已被 accept 循环抢到，
    /// 这里再做单 IP 计数（超限拒绝），无论后续如何退出都在 finally 里**成对**归还信号量与
    /// IP 计数。注意：SSE 长连接虽不在 finally 关流，但额度仍在请求处理结束时归还 ——
    /// 否则每条常驻 SSE 都白占一个全局槽，很快把 32 个名额耗尽。
    /// </summary>
    private async Task HandleClientGated(TcpClient client)
    {
        // 远端 IP（用于单 IP 并发上限）。取不到地址按 "?" 归一类，照样受限。
        string ip = "?";
        try { ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?"; }
        catch { }

        bool ipCounted = false;
        try
        {
            // bug ③ 单 IP 上限：原子自增后若超限即回退并拒绝，挡住单源吃光全局额度。
            int now = _ipConns.AddOrUpdate(ip, 1, (_, v) => v + 1);
            ipCounted = true;
            if (now > MaxConnectionsPerIp)
            {
                try
                {
                    using var ns = client.GetStream();
                    ns.WriteTimeout = 1000;
                    var busy = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    ns.Write(busy, 0, busy.Length);
                }
                catch { }
                try { client.Dispose(); } catch { }
                return;
            }

            await HandleClient(client).ConfigureAwait(false);
        }
        catch
        {
            // 兜底：HandleClient 内部已全包 try/catch，这里只防极端意外
        }
        finally
        {
            if (ipCounted)
            {
                // 归还单 IP 计数：减到 0 就移除键，避免字典无限增长
                _ipConns.AddOrUpdate(ip, 0, (_, v) => v - 1);
                if (_ipConns.TryGetValue(ip, out var left) && left <= 0)
                    _ipConns.TryRemove(new KeyValuePair<string, int>(ip, left));
            }
            try { _connGate.Release(); } catch { }
        }
    }

    /// <summary>
    /// 处理单个客户端。头部按字节读到 \r\n\r\n（不能用带缓冲的 StreamReader —— 它会把
    /// POST body 的字节也吞进自己的缓冲区，后续读 body 就缺数据），body 按 Content-Length
    /// 精确读取。整个方法 try/catch 全包 —— 手机锁屏断开 / 半截请求都静默关连接，不影响别人。
    ///
    /// 安全护栏（按本文件 6 个 bug）：
    ///  ③ 整请求时长预算（RequestBudget，~10s）：从进入到读完头+body 累计超时即断，
    ///     与单次 ReadTimeout 互补 —— 防 Slowloris 用"每隔几秒一字节"把单次超时永远撑不到。
    ///  ⑥ Content-Length 出现重复/冲突/负值/超界一律 400，绝不静默取最后一个。
    ///  ① 所有 /api/* 校验 token（POST/GET 取 X-OI-Token 头），不符 403；静态页 / 不校验。
    ///  ② 所有 POST：Host 头白名单（挡 DNS rebinding）+ 拒绝跨站 Origin/Referer；OPTIONS 显式回不含通配 CORS。
    ///
    /// 不再 using(client) 包整个生命周期：/api/events（SSE）是长连接，所有权移交给
    /// _sseClients 后这里必须**不关**；其余短请求在 finally 里关。
    /// </summary>
    private async Task HandleClient(TcpClient client)
    {
        NetworkStream? stream = null;
        bool handedOff = false; // true = SSE 已接管连接，finally 不许 Dispose
        var budget = System.Diagnostics.Stopwatch.StartNew(); // bug ③ 整请求时长预算
        try
        {
            stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            // 逐字节读头部直到空行（请求极小、每连接一次，性能无虞）
            var headerBuf = new MemoryStream();
            int state = 0; // \r\n\r\n 状态机
            while (state < 4)
            {
                if (budget.Elapsed > RequestBudget) return; // bug ③：慢速攻击 —— 累计超预算即断
                int b = stream.ReadByte();
                if (b < 0) return;
                headerBuf.WriteByte((byte)b);
                state = b switch
                {
                    '\r' when state == 0 || state == 2 => state + 1,
                    '\n' when state == 1 || state == 3 => state + 1,
                    _ => 0
                };
                if (headerBuf.Length > 16 * 1024) return; // 头部异常大，掐掉
            }
            var lines = Encoding.ASCII.GetString(headerBuf.ToArray()).Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var path = parts[1];

            // ── 解析需要的头：Host / Origin / Referer / X-OI-Token，以及（含校验的）Content-Length ──
            string? host = null, origin = null, referer = null, headerToken = null;
            int contentLength = 0;
            bool clSeen = false, clBad = false;
            foreach (var hl in lines)
            {
                if (hl.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    host = hl["Host:".Length..].Trim();
                else if (hl.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
                    origin = hl["Origin:".Length..].Trim();
                else if (hl.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase))
                    referer = hl["Referer:".Length..].Trim();
                else if (hl.StartsWith("X-OI-Token:", StringComparison.OrdinalIgnoreCase))
                    headerToken = hl["X-OI-Token:".Length..].Trim();
                else if (hl.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    // bug ⑥：任何重复（哪怕同值）/ 非法 / 负值的 Content-Length 一律 400 ——
                    // 不静默取最后一个（重复 CL 是请求走私的典型手法，保守全拒）。
                    var raw = hl["Content-Length:".Length..].Trim();
                    if (clSeen) { clBad = true; }                              // 出现第二个 = 拒
                    else if (!int.TryParse(raw, out var cl) || cl < 0) { clBad = true; }
                    else { contentLength = cl; clSeen = true; }
                }
            }
            if (clBad)
            {
                WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", "Bad Content-Length");
                return;
            }

            bool isApi = path.StartsWith("/api/", StringComparison.Ordinal);

            // ── bug ②（CSRF/Origin）：OPTIONS 预检显式回应，绝不含通配 CORS（不放行跨站脚本带凭据调用）──
            if (method == "OPTIONS")
            {
                // 不回 Access-Control-Allow-Origin: *，也不回任何允许跨站的 CORS 头 —— 同源页面本就不需要预检
                var h = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nAllow: GET, POST\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(h);
                stream.Write(hb, 0, hb.Length);
                stream.Flush();
                return;
            }

            // ── bug ②：Host 头白名单（挡 DNS rebinding）。所有 /api/* 与 POST 都校验 ──
            //    只接受 localhost / 127.0.0.1 / 本机任一局域网 IPv4，端口必须是 18686（或省略）。
            if (isApi && !IsHostAllowed(host))
            {
                WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", "Forbidden");
                return;
            }

            // ── bug ① 鉴权：所有 /api/* 校验 token。SSE 从查询参数取（EventSource 不能带自定义头），
            //    其余从 X-OI-Token 头取。静态页 GET /（含 /?...）不校验 —— 要让页面先加载拿 token。──
            if (isApi)
            {
                bool isSse = path == "/api/events" || path.StartsWith("/api/events?");
                string? presented = isSse ? ExtractQueryToken(path) : headerToken;
                if (!TokenOk(presented))
                {
                    WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", "Forbidden");
                    return;
                }
            }

            // SSE 路由：token 已过（上面），不读 body（GET 没有）、写完头和 hello 帧就移交所有权返回
            if (method == "GET" && (path == "/api/events" || path.StartsWith("/api/events?")))
            {
                handedOff = TryStartSse(client, stream);
                return;
            }

            // bug ②：所有 POST 拒绝跨站 Origin/Referer（缺失也拒 —— 浏览器 fetch 同源会带 Origin；
            //        非浏览器客户端没有同源概念，但已被上面的 token 挡住，这里专防"用户在恶意站点
            //        被诱导发跨站请求"）。
            if (method == "POST" && !IsSameOrigin(origin, referer, host))
            {
                WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", "Forbidden");
                return;
            }

            byte[] body = Array.Empty<byte>();
            if (contentLength > 64 * 1024)
            {
                WriteResponse(stream, "413 Payload Too Large", "text/plain; charset=utf-8", "Too Large");
                return;
            }
            if (contentLength > 0)
            {
                body = new byte[contentLength];
                int off = 0;
                while (off < contentLength)
                {
                    if (budget.Elapsed > RequestBudget) return; // bug ③：读 body 也吃整请求预算
                    int n = stream.Read(body, off, contentLength - off);
                    if (n <= 0) break;
                    off += n;
                }
            }

            if (method == "GET" && (path == "/" || path.StartsWith("/?")))
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", IndexHtml);
            else if (method == "GET" && (path == "/api/sessions" || path.StartsWith("/api/sessions?")))
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", BuildSessionsJson());
            else if (method == "GET" && path == "/api/models")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", BuildModelsJson());
            else if (method == "GET" && path == "/icon.png")
            {
                var png = GetIconPng();
                if (png.Length > 0)
                    WriteResponse(stream, "200 OK", "image/png", png);
                else
                    WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not Found");
            }
            // bug ④：注入续体不再压在 UI 线程、HTTP 线程也不再 .GetResult() 同步阻塞 ——
            //         各 HandleXxx 改 async，内部用 await InvokeAsync(...).Task.Unwrap()。
            else if (method == "POST" && path == "/api/send")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", await HandleSend(body).ConfigureAwait(false));
            else if (method == "POST" && path == "/api/approve")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", await HandleApprove(body).ConfigureAwait(false));
            else if (method == "POST" && path == "/api/setmode")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", await HandleSetMode(body).ConfigureAwait(false));
            else if (method == "POST" && path == "/api/answer")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", await HandleAnswer(body).ConfigureAwait(false));
            else
                WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not Found");
        }
        catch
        {
            // 客户端中断 / 写失败 —— 静默关连接即可
        }
        finally
        {
            if (!handedOff)
            {
                try { stream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// bug ① token 常量时间比较。presented 为空或长度/内容不符均返回 false。
    /// 用 CryptographicOperations.FixedTimeEquals 避免按字节提前返回的计时侧信道。
    /// Constant-time token check; empty/mismatched tokens fail.
    /// </summary>
    private bool TokenOk(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        if (_tokenBytes.Length == 0) return false; // 没配置令牌（理论不会发生）= 一律拒绝，绝不"裸奔放行"
        byte[] given;
        try { given = Encoding.ASCII.GetBytes(presented); }
        catch { return false; }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, _tokenBytes);
    }

    /// <summary>从形如 "/api/events?t=xxxx&amp;a=b" 的路径里取 t 参数（SSE 鉴权用，EventSource 不能带自定义头）。</summary>
    private static string? ExtractQueryToken(string path)
    {
        var qi = path.IndexOf('?');
        if (qi < 0 || qi + 1 >= path.Length) return null;
        foreach (var kv in path[(qi + 1)..].Split('&'))
        {
            if (kv.StartsWith("t=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(kv[2..]);
        }
        return null;
    }

    /// <summary>
    /// bug ② Host 头白名单：挡 DNS rebinding。只接受 localhost / 127.0.0.1 / 本机任一活动 IPv4，
    /// 端口必须是 18686（或缺省）。host 缺失或不在白名单一律拒绝。
    /// </summary>
    private bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        // 拆出主机名与端口（IPv6 不在本服务监听范围，简单处理）
        string h = host;
        int port = Port;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) // 排除 IPv6 字面量里的冒号
        {
            h = host[..colon];
            if (!int.TryParse(host[(colon + 1)..], out port)) return false;
        }
        if (port != Port) return false;

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(h, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        // 本机任一活动 IPv4（手机访问时 Host 就是电脑的局域网 IP）
        return IsLocalIPv4(ip);
    }

    /// <summary>给定 IP 是否是本机某张网卡的 IPv4 地址（Host 白名单用）。失败保守返回 false。</summary>
    private static bool IsLocalIPv4(IPAddress ip)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && addr.Address.Equals(ip))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// bug ② 跨站防护：POST 必须同源。优先看 Origin（浏览器 fetch 同源会带），缺 Origin 退看 Referer；
    /// 两者都缺则拒绝（同源浏览器请求至少带其一）。比较只认 http + 与 Host 相同的主机:端口。
    /// </summary>
    private bool IsSameOrigin(string? origin, string? referer, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        // 取 Origin 的 scheme://host:port；没有 Origin 用 Referer 的同段
        var src = !string.IsNullOrWhiteSpace(origin) ? origin : referer;
        if (string.IsNullOrWhiteSpace(src)) return false;
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp) return false; // 本服务只跑 http
        // Origin 端口必须是本服务端口（默认端口即非 18686，直接判否）
        int oport = u.IsDefaultPort ? 80 : u.Port;
        if (oport != Port) return false;
        // Host 头形如 "192.168.1.5:18686" 或 "192.168.1.5"（端口缺省）；剥掉端口取主机名
        var hostNorm = host;
        var colon = host!.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']'))
        {
            if (!int.TryParse(host[(colon + 1)..], out var hp) || hp != Port) return false;
            hostNorm = host[..colon];
        }
        // 同源 = Origin 主机名与 Host 主机名一致；再叠一层 Host 白名单（双保险，挡 rebinding）
        return string.Equals(u.Host, hostNorm, StringComparison.OrdinalIgnoreCase)
            && IsHostAllowed(host);
    }

    /// <summary>
    /// /api/events：写 SSE 响应头 + hello 帧后把连接放进长连接池。
    /// 成功返回 true（调用方不许再关这条连接）；任何一步写失败返回 false 走正常清理。
    /// </summary>
    private bool TryStartSse(TcpClient client, NetworkStream stream)
    {
        try
        {
            // bug ③：SSE 池有上限（每条常驻必须封顶）。已满直接拒绝，回 503 让前端降级轮询。
            if (_sseClients.Count >= MaxSseClients)
            {
                try
                {
                    var full = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    stream.Write(full, 0, full.Length);
                    stream.Flush();
                }
                catch { }
                return false; // 走 HandleClient finally 收尾
            }

            // 长连接：读永不超时（SSE 单向推送、不再读请求）；写 10s 超时防僵尸连接卡住广播
            stream.ReadTimeout = Timeout.Infinite;
            stream.WriteTimeout = 10000;

            // 注意：没有 Content-Length、Connection 不是 close —— 流式长连接的标志
            var header = "HTTP/1.1 200 OK\r\n"
                       + "Content-Type: text/event-stream; charset=utf-8\r\n"
                       + "Cache-Control: no-cache\r\n"
                       + "Connection: keep-alive\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            // 立即发一帧 hello —— 让 EventSource 马上触发 onopen/onmessage，确认通道可用
            var hello = Encoding.UTF8.GetBytes("data: {\"type\":\"hello\"}\n\n");
            stream.Write(hello, 0, hello.Length);
            stream.Flush();

            var key = Guid.NewGuid();
            _sseClients[key] = (client, stream, new object());

            // 复查闭合竞态窗口：Stop() 先置 IsRunning=false 再清池，若它在我们入池**之前**
            // 已经遍历完池子，这条连接就成了无人 Dispose 的孤儿 —— 入池后复查一次即可兜住
            //（要么 Stop 清池时看见我们，要么我们复查时看见 IsRunning=false，二者必居其一）。
            if (!IsRunning)
            {
                if (_sseClients.TryRemove(key, out _)) return false; // 走 HandleClient finally 收尾
                return true; // Stop() 已替我们 Dispose 过，别再关第二次
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 给所有 SSE 客户端写同一帧字节。写失败 = 客户端已断（锁屏/切网/关页面），移除并 Dispose。
    /// bug ⑤：每个连接各起一个 Task 并发分发（仍各自持本连接的 WriteLock 防同流帧交错），
    /// Task.WhenAll 套一个整体超时 —— 一个写超时卡满 10s 的僵尸客户端只拖自己，不再队头阻塞
    /// 其余所有客户端整轮心跳/广播。整体超时（12s，略大于单连接 10s WriteTimeout）只是兜底，
    /// 不强杀慢任务（它们自己的 WriteTimeout 到点会抛而被回收）。
    /// </summary>
    private void SseBroadcast(byte[] frame)
    {
        var snapshot = _sseClients.ToArray();
        if (snapshot.Length == 0) return;

        var tasks = new List<Task>(snapshot.Length);
        foreach (var kv in snapshot)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    // 每连接各持自己的 WriteLock：与该连接上的心跳/其它广播互斥（帧不交错），
                    // 但不同连接之间完全并行。
                    lock (kv.Value.WriteLock)
                    {
                        kv.Value.Stream.Write(frame, 0, frame.Length);
                        kv.Value.Stream.Flush();
                    }
                }
                catch
                {
                    if (_sseClients.TryRemove(kv.Key, out var dead))
                    {
                        try { dead.Stream.Dispose(); } catch { }
                        try { dead.Client.Dispose(); } catch { }
                    }
                }
            }));
        }
        // 整体超时兜底：正常情况下全部很快返回；个别慢连接由各自 WriteTimeout 收尾，
        // 这里不阻塞调用线程（心跳 Timer / 去抖 Timer）超过 12s。
        try { Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(12)); } catch { }
    }

    /// <summary>
    /// 会话状态变化 → 250ms 去抖后向所有 SSE 客户端广播一帧 update（带最新 stats）。
    /// 去抖原因：SessionManager 在一次操作里可能连发多次 SessionsChanged（阶段+权限+标题），
    /// 逐次广播浪费且会让手机端连环刷新。
    /// </summary>
    public void NotifyChanged()
    {
        lock (_notifyLock)
        {
            // IsRunning 检查必须在锁内：Stop() 在同一把锁下 Dispose/置空 _notifyDebounce，
            // 锁外检查会留 TOCTOU 窗口 —— Stop 完整跑完后这里再 ??= 重建一个孤儿 Timer。
            if (!IsRunning) return;
            // 单发定时器：每次变化都把倒计时重置回 250ms，静默期结束才真正广播
            _notifyDebounce ??= new System.Threading.Timer(
                _ => BroadcastUpdate(), null, Timeout.Infinite, Timeout.Infinite);
            _notifyDebounce.Change(250, Timeout.Infinite);
        }
    }

    /// <summary>去抖落地：序列化当前 stats 并广播。Stop 后的迟到回调直接丢弃。</summary>
    private void BroadcastUpdate()
    {
        if (!IsRunning) return;
        try
        {
            var json = JsonSerializer.Serialize(new { type = "update", stats = BuildStats() });
            SseBroadcast(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        }
        catch
        {
            // 序列化/广播失败不致命，下次变化再推
        }
    }

    /// <summary>
    /// POST /api/send：{"id":"会话id","text":"消息"} → 复用灵动岛快捷回复的注入通道发给
    /// 对应 CLI 终端 / Claude 桌面端（SessionManager.SendQuickReplyAsync）。注入要激活
    /// 窗口/动剪贴板，必须在 UI 线程**启动**；bug ④：用 InvokeAsync(...).Task.Unwrap() 把整条
    /// 注入异步链（含其续体）交给 UI Dispatcher 自行排队，HTTP 线程只 await 等结果 ——
    /// 既不在 UI 线程同步阻塞跑完整条注入，HTTP 线程也不再 .GetResult() 死等。
    /// </summary>
    private async Task<string> HandleSend(byte[] body)
    {
        try
        {
            if (body.Length == 0) return """{"ok":false,"reason":"empty body"}""";
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
                return """{"ok":false,"reason":"missing id or text"}""";
            if (text.Length > 2000)
                return """{"ok":false,"reason":"text too long"}""";

            var app = System.Windows.Application.Current;
            if (app == null) return """{"ok":false,"reason":"app shutting down"}""";

            // InvokeAsync 在 UI 线程启动 SendQuickReplyAsync；.Task 是 Task<Task<...>>，
            // Unwrap() 摊平成 Task<...>，await 它 —— 注入的 await 续体由 Dispatcher 排队，
            // 不抢占也不阻塞 UI；HTTP 线程在此挂起等完成，不占线程池忙等。
            var result = await app.Dispatcher
                .InvokeAsync(() => _sessionManager.SendQuickReplyAsync(id!, text!))
                .Task.Unwrap().ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                reason = result.Reason ?? "",
                reasonText = result.Ok ? "" : ReasonText(result.Reason)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, reason = ex.Message, reasonText = ex.Message });
        }
    }

    /// <summary>注入失败原因 → 网页上能看懂的人话（与灵动岛 QuickReplyReasonText 同一套原因码）。</summary>
    private static string ReasonText(string? reason) => reason switch
    {
        "empty" => "内容为空",
        "too long" => "内容过长",
        "no-session" => "会话不存在",
        "no-terminal" or "no-terminal-match" => "没找到该会话的终端（同目录开了多个会话且无法用标题区分时，为安全起见不发送）",
        "foreground-mismatch" => "没能把终端切到前台，请在电脑上重试",
        "foreground-lost" => "终端失焦，文字已粘贴但未提交",
        "inject-error" => "注入出错，已取消",
        "desktop-activate-failed" or "no-desktop-window" => "没能激活 Claude Desktop 窗口",
        "session-nav-failed" => "没能在 Claude Desktop 切到目标会话，已取消（避免发错会话），请在客户端手动打开该会话",
        "clipboard-failed" => "电脑剪贴板被占用，请重试",
        _ => "发送失败"
    };

    /// <summary>
    /// POST /api/approve：{"id":"会话id","digit":"1"|"2"|"3"} → 复用灵动岛权限按钮的通路
    /// （SessionManager.RespondToPermissionAsync 内部已分流 CLI 注入 / 桌面 UIA）。
    /// 与 /api/send 同模式（bug ④）：InvokeAsync(...).Task.Unwrap() + await，UI 线程不被阻塞、
    /// HTTP 线程不 .GetResult() 死等。
    /// </summary>
    private async Task<string> HandleApprove(byte[] body)
    {
        try
        {
            if (body.Length == 0) return """{"ok":false}""";
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var digit = root.TryGetProperty("digit", out var dEl) ? dEl.GetString() : null;
            // 只接受 1/2/3 —— 与 Claude 终端权限提示的三个选项一一对应，其它一律拒绝
            if (string.IsNullOrWhiteSpace(id) || digit is not ("1" or "2" or "3"))
                return """{"ok":false}""";

            var app = System.Windows.Application.Current;
            if (app == null) return """{"ok":false}""";

            var ok = await app.Dispatcher
                .InvokeAsync(() => _sessionManager.RespondToPermissionAsync(id!, digit[0]))
                .Task.Unwrap().ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok });
        }
        catch
        {
            return """{"ok":false}""";
        }
    }

    /// <summary>
    /// POST /api/setmode：{"id":"会话id","mode":"default"|"acceptEdits"|"plan"} →
    /// 精确切换该会话的权限模式（SessionManager.SetPermissionModeAsync：按 hook 上报的
    /// 当前模式算 Shift+Tab 循环步数，聚焦终端连发）。与 /api/send 同模式（bug ④）：
    /// InvokeAsync(...).Task.Unwrap() + await，UI 线程不被阻塞、HTTP 线程不 .GetResult() 死等。
    /// </summary>
    private async Task<string> HandleSetMode(byte[] body)
    {
        try
        {
            if (body.Length == 0) return """{"ok":false,"reason":"empty","reasonText":"请求无效"}""";
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var mode = root.TryGetProperty("mode", out var mEl) ? mEl.GetString() : null;
            // 白名单四态（Shift+Tab 实测循环：default→acceptEdits→plan→auto）——
            // bypassPermissions 故意不收（不在循环里，切不过去）
            if (string.IsNullOrWhiteSpace(id) || mode is not ("default" or "acceptEdits" or "plan" or "auto"))
                return """{"ok":false,"reason":"bad-request","reasonText":"请求无效"}""";

            var app = System.Windows.Application.Current;
            if (app == null) return """{"ok":false,"reason":"shutdown","reasonText":"应用正在退出"}""";

            var (ok, reason) = await app.Dispatcher
                .InvokeAsync(() => _sessionManager.SetPermissionModeAsync(id!, mode!))
                .Task.Unwrap().ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok,
                reason,
                reasonText = ok ? (reason == "already" ? "已在该模式" : "") : ModeReasonText(reason)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, reason = ex.Message, reasonText = "切换出错" });
        }
    }

    /// <summary>
    /// POST /api/answer：{"id":"会话id","option":2} 或 {"id":"会话id","skip":true} →
    /// 回答 AskUserQuestion（SessionManager.AnswerQuestionAsync：CLI 注入 "{n}\r"，
    /// 桌面端 UIA 点选项行；skip = Esc / Skip 按钮）。与 /api/send 同模式（bug ④）：
    /// InvokeAsync(...).Task.Unwrap() + await。
    /// </summary>
    private async Task<string> HandleAnswer(byte[] body)
    {
        try
        {
            if (body.Length == 0) return """{"ok":false,"reasonText":"请求无效"}""";
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var skip = root.TryGetProperty("skip", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            int option = 0;
            if (root.TryGetProperty("option", out var oEl) && oEl.ValueKind == JsonValueKind.Number)
                oEl.TryGetInt32(out option);
            // 选项编号 1..20 防呆（Claude Code 的 AskUserQuestion 最多 4 个选项，留余量）
            if (string.IsNullOrWhiteSpace(id) || (!skip && option is < 1 or > 20))
                return """{"ok":false,"reasonText":"请求无效"}""";

            var app = System.Windows.Application.Current;
            if (app == null) return """{"ok":false,"reasonText":"应用正在退出"}""";

            var ok = await app.Dispatcher
                .InvokeAsync(() => _sessionManager.AnswerQuestionAsync(id!, option, skip))
                .Task.Unwrap().ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                ok,
                reasonText = ok ? "" : "没能把答案送进终端，请稍后重试或直接在终端选择"
            });
        }
        catch
        {
            return """{"ok":false,"reasonText":"回答出错"}""";
        }
    }

    /// <summary>模式切换失败原因 → 网页人话。</summary>
    private static string ModeReasonText(string? reason) => reason switch
    {
        "no-session" => "会话不存在",
        "desktop-unsupported" => "桌面端会话暂不支持网页切换模式，请在 Claude 客户端里操作",
        "busy-prompt" => "该会话有待处理的权限/提问弹窗，先处理掉再切换模式",
        "unknown-mode" => "还不知道该会话当前处于什么模式（会话需要先有一次交互），暂时无法精确切换",
        "bypass-mode" => "该会话以跳过全部权限的模式运行，不支持切换",
        "no-terminal" => "没找到该会话的终端（同目录开了多个会话且无法区分时，为安全起见不发送）",
        "inject-failed" => "没能把终端切到前台，请稍后重试",
        _ => "切换失败"
    };

    /// <summary>
    /// GET /icon.png 用的图标字节：从 WPF 资源里抠 face.png（手机"添加到主屏幕"的图标）。
    /// GetResourceStream 必须在 UI 线程调用（依赖 Application 资源系统），结果缓存一次 ——
    /// 失败也缓存空数组（调用方据此回 404），避免每次请求都白跑一趟 Dispatcher。
    /// </summary>
    private byte[] GetIconPng()
    {
        var cached = _iconPng;
        if (cached != null) return cached;

        var data = Array.Empty<byte>();
        try
        {
            var app = System.Windows.Application.Current;
            if (app != null)
            {
                data = app.Dispatcher.Invoke(() =>
                {
                    var sri = System.Windows.Application.GetResourceStream(
                        new Uri("pack://application:,,,/OpenIsland;component/Assets/face.png"));
                    if (sri == null) return Array.Empty<byte>();
                    using var ms = new MemoryStream();
                    sri.Stream.CopyTo(ms);
                    return ms.ToArray();
                }) ?? Array.Empty<byte>();
            }
        }
        catch
        {
            data = Array.Empty<byte>();
        }
        _iconPng = data;
        return data;
    }

    /// <summary>GET /api/models：内置 Claude 模型档（带 /model 可用的 slug），网页功能栏的模型下拉用。</summary>
    private static string BuildModelsJson()
    {
        try
        {
            var items = ModelPresets.BuiltInClaude
                .Where(p => !string.IsNullOrEmpty(p.ClaudeModelSlug))
                .Select(p => new { name = p.Name, slug = p.ClaudeModelSlug })
                .ToList();
            return JsonSerializer.Serialize(items);
        }
        catch { return "[]"; }
    }

    /// <summary>写一个完整的 HTTP/1.1 响应（Content-Length 必须是 UTF8 字节数，不是字符数）。</summary>
    private static void WriteResponse(NetworkStream stream, string status, string contentType, string body)
        => WriteResponse(stream, status, contentType, Encoding.UTF8.GetBytes(body));

    /// <summary>byte[] 重载：/icon.png 等二进制响应用（Content-Length 即字节数）。</summary>
    private static void WriteResponse(NetworkStream stream, string status, string contentType, byte[] bodyBytes)
    {
        var header = $"HTTP/1.1 {status}\r\n"
                   + $"Content-Type: {contentType}\r\n"
                   + $"Content-Length: {bodyBytes.Length}\r\n"
                   + "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    /// <summary>
    /// /api/sessions：响应是对象 { stats, sessions }（曾经是裸数组 —— 前端要 stats 概览，
    /// 顺带升级）。Claude 会话（有 transcript 的）默认信息流取 8 条、各 12 条气泡；
    /// 所有数据从 transcript 现读（ReadTail 只读尾部 256KB），不维护任何缓存。
    /// </summary>
    private string BuildSessionsJson()
    {
        try
        {
            var selected = SelectedSnapshot(); // 快照一次，整个构建过程用同一份
            var candidates = _sessionManager.GetAllSessions()
                .Where(s => s.Tool == AgentTool.ClaudeCode
                            && !string.IsNullOrEmpty(s.ClaudeMetadata?.TranscriptPath))
                .Select(s => new { Session = s, Path = s.ClaudeMetadata!.TranscriptPath! })
                .Select(x => new { x.Session, x.Path, Mtime = File.GetLastWriteTimeUtc(x.Path) });

            // 选中模式：**只**返回被点了圆点的会话（多选并行），每个带 60 条完整历史；
            // 未选任何会话：默认信息流 —— 最近 8 条、各 12 条。
            bool filterMode = selected.Length > 0;
            if (filterMode)
                candidates = candidates.Where(x => selected.Contains(x.Session.Id));

            // 排序：默认信息流把"等人"的会话（待批准/待回答）排最前 —— 手机端打开第一眼
            // 就该看到要处理的事；选中模式用户已手动指定看什么，保持纯 mtime 降序即可。
            var items = candidates
                .OrderByDescending(x => !filterMode
                    && x.Session.Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer)
                .ThenByDescending(x => x.Mtime)
                .Take(filterMode ? 6 : 8)
                .Select(x => new
                {
                    id = x.Session.Id,
                    title = x.Session.Title,
                    phase = x.Session.Phase.ToString(),
                    entry = x.Session.ClaudeMetadata?.Entrypoint ?? "",
                    // Unix 毫秒而非格式化字符串 —— 前端自己算"x 分钟前"，还能跨时区正确
                    updatedMs = new DateTimeOffset(x.Mtime).ToUnixTimeMilliseconds(),
                    tokens = (ulong)(x.Session.ClaudeMetadata?.TotalTokens ?? 0u),
                    featured = filterMode,
                    // 当前权限模式（hook 上报；null = 未知）。前端显示徽章 + 模式下拉据此防呆
                    mode = x.Session.PermissionMode,
                    permission = BuildPermission(x.Session.PermissionRequest),
                    question = BuildQuestion(x.Session),
                    // 单会话视图要滚动看历史：默认 20 条、选中 60 条
                    messages = ReadTail(x.Path, filterMode ? 60 : 20)
                })
                .ToList();
            return JsonSerializer.Serialize(new { stats = BuildStats(), sessions = items });
        }
        catch
        {
            return """{"stats":{"running":0,"attention":0,"idle":0,"total":0,"usageLeft":-1,"usageReset":"","usageApi":false,"usageTokens":0},"sessions":[]}""";
        }
    }

    /// <summary>
    /// 会话权限请求 → 网页端 JSON（null = 该会话没有待批准的权限）。
    /// desc 截断 200 字符：Bash 命令描述可能整页长，气泡里只需要个大概。
    /// btn2 去掉 ToButtonLabel 自带的 "2." 编号前缀 —— 网页按钮自己画 "2 "，不去重会显示 "2 2. Yes…"。
    /// </summary>
    private static object? BuildPermission(PermissionRequest? pr)
    {
        if (pr == null) return null;
        // AskUserQuestion 是"提问"不是"权限"：让位给 question 块（选项按钮），
        // 不然网页会同时弹出"允许/拒绝"——对提问而言语义完全是错的。
        if (string.Equals(pr.ToolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase))
            return null;
        var btn2 = pr.SuggestedAlwaysAllow?.ToButtonLabel();
        if (btn2 != null)
            btn2 = System.Text.RegularExpressions.Regex.Replace(btn2, @"^\s*2\s*[\.、]?\s*", "");
        return new
        {
            tool = pr.ToolName,
            desc = pr.Description.Length > 200 ? pr.Description[..200] : pr.Description,
            btn2
        };
    }

    /// <summary>
    /// 提问块 JSON：AskUserQuestion 挂起（或 hook 报了 QuestionPrompt）时返回
    /// { prompt, options:[{n,label,desc}] }，否则 null。options 从
    /// PermissionRequest.ToolInput 的 questions[0].options[] 解析（Claude Code 实际格式：
    /// {"questions":[{"question":"…","options":[{"label":"…","description":"…"},…]}]}），
    /// 与岛上 ParseAskUserQuestion 同一份契约；解析失败只回 prompt —— 网页退化为
    /// "输入框回复编号"，不至于整块消失。
    /// </summary>
    private static object? BuildQuestion(AgentSession s)
    {
        var pr = s.PermissionRequest;
        var isQuestion = string.Equals(pr?.ToolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase);
        var prompt = s.QuestionPrompt?.Prompt ?? "";
        if (!isQuestion && prompt.Length == 0) return null;

        var options = new List<object>();
        if (isQuestion && pr!.ToolInput is { Count: > 0 } input)
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
                if (doc.RootElement.TryGetProperty("questions", out var qs)
                    && qs.ValueKind == JsonValueKind.Array && qs.GetArrayLength() > 0)
                {
                    var q = qs[0];
                    if (prompt.Length == 0 && q.TryGetProperty("question", out var qt))
                        prompt = qt.GetString() ?? "";
                    if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    {
                        int n = 1;
                        foreach (var opt in opts.EnumerateArray())
                        {
                            var label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                            var desc = opt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                            if (desc.Length > 160) desc = desc[..160];
                            options.Add(new { n = n++, label, desc });
                        }
                    }
                }
            }
            catch { /* 格式变了 → 只回 prompt，网页用输入框兜底 */ }
        }
        return new { prompt, options };
    }

    /// <summary>stats 的 JSON 形状。属性名刻意小写 —— 直接成为 JSON 键（同 TailMessage）。</summary>
    private sealed class StatsDto
    {
        public int running { get; set; }
        public int attention { get; set; }
        public int idle { get; set; }
        public int total { get; set; }
        // ── 5h 订阅余额（底部工具行）：usageLeft = 剩余百分比，-1 = 未知/未取到 ──
        public int usageLeft { get; set; } = -1;
        public string usageReset { get; set; } = "";   // "0h54m"，空 = 不显示重置时间
        public bool usageApi { get; set; }              // API 模式：只报终身累计 token
        public ulong usageTokens { get; set; }
    }

    /// <summary>
    /// 统计所有 Claude 会话（有 transcript 的）的阶段分布：Running→running，
    /// 待批准/待回答→attention，其余（Idle/Completed）→idle。
    /// /api/sessions 响应头部和 SSE update 帧共用，保证两边数字一致。
    /// </summary>
    private StatsDto BuildStats()
    {
        var stats = new StatsDto();
        try
        {
            foreach (var s in _sessionManager.GetAllSessions())
            {
                if (s.Tool != AgentTool.ClaudeCode
                    || string.IsNullOrEmpty(s.ClaudeMetadata?.TranscriptPath)) continue;
                switch (s.Phase)
                {
                    case SessionPhase.Running: stats.running++; break;
                    case SessionPhase.WaitingForApproval:
                    case SessionPhase.WaitingForAnswer: stats.attention++; break;
                    default: stats.idle++; break;
                }
            }
            stats.total = stats.running + stats.attention + stats.idle;
        }
        catch
        {
            // 枚举会话失败 —— 回零值，别让一帧 stats 拖垮整个响应
        }

        // 5h 余额：与灵动岛同一口径（剩余 = 100 - 已用%；Indeterminate 时显示 "--" 不伪造）
        var u = _lastUsage;
        if (u != null)
        {
            if (u.IsApi)
            {
                stats.usageApi = true;
                stats.usageTokens = u.UsedTokens;
            }
            else if (!u.Indeterminate)
            {
                stats.usageLeft = Math.Clamp(100 - u.Percent, 0, 100);
                if (u.ResetIn is { } r && r > TimeSpan.Zero)
                    stats.usageReset = $"{(int)r.TotalHours}h{r.Minutes:D2}m";
            }
        }
        return stats;
    }

    /// <summary>网页消息条目。属性名刻意小写 —— 直接成为 JSON 键，省一套命名策略配置。</summary>
    private sealed class TailMessage
    {
        public string role { get; set; } = "";
        public string text { get; set; } = "";
    }

    /// <summary>
    /// 只读 transcript 尾部最多 256KB，解析出最近 12 条 user/assistant 文本消息。
    /// FileShare.ReadWrite|Delete：Claude 正在写 / 轮转该文件时也能读，不互相阻塞。
    /// Seek 落点大概率在一行中间 —— 丢弃第一段不完整的行；坏行（写到一半的 JSON）逐行跳过。
    /// </summary>
    private static List<TailMessage> ReadTail(string path, int maxMessages = 12)
    {
        var result = new List<TailMessage>();
        try
        {
            const int MaxBytes = 256 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - MaxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');

            var collected = new List<TailMessage>();
            // start > 0 时第一段可能从行中间开始，不是合法 JSON，直接丢
            for (int i = start > 0 ? 1 : 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    if (type != "user" && type != "assistant") continue;
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content)) continue;

                    string text = "";
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        // 旧格式 / 简单消息：content 直接是字符串
                        text = content.GetString() ?? "";
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        // 新格式：content 是块数组，只拼 text 块。
                        // user 行里的 tool_result（工具回包，动辄几十 KB）跳过 —— 网页只看对话。
                        var sb = new StringBuilder();
                        foreach (var part in content.EnumerateArray())
                        {
                            if (!part.TryGetProperty("type", out var pt)) continue;
                            var kind = pt.GetString();
                            if (kind == "tool_result") continue;
                            if (kind == "text" && part.TryGetProperty("text", out var t))
                                sb.Append(t.GetString());
                        }
                        text = sb.ToString();
                    }

                    text = text.Trim();
                    if (text.Length == 0) continue; // 纯 tool_use / thinking 行没有正文，不上墙
                    if (text.Length > 1500) text = text[..1500];
                    collected.Add(new TailMessage { role = type!, text = text });
                }
                catch
                {
                    // 写到一半的行 / 非 JSON 行 —— 跳过继续
                }
            }

            // 只取最后 maxMessages 条（普通 12 / 置顶 60，控制 JSON 体积）
            for (int i = Math.Max(0, collected.Count - maxMessages); i < collected.Count; i++)
                result.Add(collected[i]);
        }
        catch
        {
            // 文件被删 / 占用 / 编码异常 —— 兜底空列表，网页显示无消息即可
        }
        return result;
    }

    /// <summary>
    /// 内嵌的同步页面 —— 聊天应用式单会话视图：顶部玻璃头（标题/统计/铃铛/主题）+
    /// 会话标签行（标签 = /api/sessions 返回的会话；灵动岛选中谁就生成谁，点击切换），
    /// 中间会话区高度固定（flex 撑满视口）内部滚轮翻历史（贴底跟随新消息、翻历史保位），
    /// 底部 dock：输入框在上、工具行（模型切换 / 当前会话 tokens / 5h 余额）在下。
    /// 数据链路：SSE（/api/events）推送 + 150ms 去抖拉取 /api/sessions，断线降级 5s 轮询；
    /// 每会话草稿独立保存，切标签不丢。esc() 对所有动态文本做 HTML 转义 —— transcript
    /// 内容不可信（可能含尖括号代码）。注意：C# 原始字符串里不能出现连续三个双引号，
    /// HTML 属性内嵌 JS 字符串一律用 &amp;quot; 实体规避。
    /// </summary>
    private const string IndexHtml = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="theme-color" content="#0c0c0e">
        <meta name="apple-mobile-web-app-capable" content="yes">
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
        <link rel="apple-touch-icon" href="/icon.png">
        <link rel="icon" href="/icon.png">
        <title>Open Island · Web Sync</title>
        <style>
          * { box-sizing:border-box; }
          html,body { height:100%; }
          body { margin:0; height:100vh; height:100dvh; display:flex; flex-direction:column;
                 overflow:hidden; background:#0c0c0e; color:#e7e7ea;
                 font-family:-apple-system,'Segoe UI',Roboto,sans-serif; }
          /* ── 玻璃头部：标题行 + 会话标签行（标签 = 灵动岛选中的会话） ── */
          .hdr { flex:none; background:rgba(12,12,14,.78);
                 backdrop-filter:blur(14px); -webkit-backdrop-filter:blur(14px);
                 border-bottom:1px solid rgba(255,255,255,.06);
                 padding:calc(10px + env(safe-area-inset-top)) 0 0; }
          .topbar { display:flex; align-items:center; gap:8px; padding:0 16px;
                    max-width:860px; margin:0 auto; width:100%; }
          h1 { font-size:16px; margin:0; flex:none; }
          .statline { flex:1; font-size:11px; color:#8a8a90; overflow:hidden;
                      text-overflow:ellipsis; white-space:nowrap; }
          .iconbtn { width:32px; height:32px; border-radius:50%; flex:none; padding:0;
                     background:#1c1c1f; border:1px solid #2a2a2e; color:#e7e7ea;
                     font-size:14px; cursor:pointer; }
          .tabs { display:flex; gap:8px; overflow-x:auto; padding:9px 16px 10px;
                  max-width:860px; margin:0 auto; width:100%; scrollbar-width:none; }
          .tabs::-webkit-scrollbar { display:none; }
          .tab { display:flex; align-items:center; gap:6px; flex:none; cursor:pointer;
                 user-select:none; background:#1c1c1f; border:1px solid #2a2a2e;
                 border-radius:999px; padding:6px 12px; font-size:12px; color:#c9c9ce;
                 max-width:190px; }
          .tab .tt { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
          .tab.on { border-color:#CC785C; background:#2e2118; color:#fff; }
          .tab .td { width:7px; height:7px; border-radius:50%; flex:none; }
          .tab .star { color:#CC785C; font-size:10px; flex:none; }
          /* ── 会话区：高度固定（flex 撑满），内部滚轮浏览历史 ── */
          .chat { flex:1 1 0; overflow-y:auto; -webkit-overflow-scrolling:touch; }
          /* min-height + 首子元素 margin-top:auto：消息少时贴底（聊天 app 观感），
             多时正常从顶往下排、容器内滚动 */
          .chatin { max-width:860px; margin:0 auto; padding:14px 16px 10px;
                    display:flex; flex-direction:column; gap:7px; min-height:100%; }
          .chatin > :first-child { margin-top:auto; }
          .msg { max-width:86%; padding:8px 11px; border-radius:12px; font-size:13.5px;
                 white-space:pre-wrap; word-break:break-word; line-height:1.5; }
          .user { align-self:flex-end; background:#3a2a1f; }
          .assistant { align-self:flex-start; background:#1f1f23; }
          .msg.ghost { opacity:.55; }   /* 乐观回显气泡：下次刷新被真实数据替换 */
          .msg pre.code { background:#101013; border:1px solid #2a2a2e; border-radius:8px;
                          padding:8px 10px; margin:6px 0; white-space:pre-wrap;
                          word-break:break-all; font-family:ui-monospace,Consolas,monospace;
                          font-size:12px; line-height:1.5; }
          .code .dadd { color:#5DCAA5; }
          .code .ddel { color:#F09595; }
          .msg code { background:#101013; border-radius:4px; padding:1px 5px;
                      font-family:ui-monospace,Consolas,monospace; font-size:12px; }
          .more { color:#CC785C; cursor:pointer; font-size:12px; user-select:none; }
          .empty { color:#6e6e74; font-size:12.5px; text-align:center; padding:40px 20px;
                   line-height:1.8; }
          /* 审批 / 提问块（会话流底部，跟着最新消息走） */
          .perm,.qbox { background:rgba(255,152,0,.08); border:1px solid rgba(255,152,0,.45);
                        border-radius:12px; padding:10px 12px; align-self:stretch; }
          .permhead { color:#FF9800; font-weight:700; font-size:13px; margin-bottom:6px; }
          .permdesc { font-family:ui-monospace,Consolas,monospace; font-size:11.5px;
                      color:#b9bcc2; white-space:pre-wrap; word-break:break-word;
                      margin-bottom:9px; }
          .permbtns { display:flex; gap:8px; flex-wrap:wrap; }
          .pb { border:none; border-radius:9px; padding:8px 14px; font-size:13px;
                cursor:pointer; color:#fff; }
          .pb:disabled { opacity:.5; }
          .pb.allow { background:#2e7d4f; }
          .pb.always { background:#3a3a40; }
          .pb.deny { background:#7a2e2e; }
          .pb.qopt { background:#33536b; }   /* 提问选项：蓝系，与"允许/拒绝"区分 */
          .permok { color:#5DCAA5; font-size:13px; font-weight:600; }
          .qtext { font-size:13px; white-space:pre-wrap; word-break:break-word; }
          .qhint { font-size:11px; color:#FF9800; margin-top:6px; }
          /* ── 底部 dock：输入框在上，模型 / tokens / 5h 余额在下 ── */
          .dock { flex:none; background:rgba(12,12,14,.85);
                  backdrop-filter:blur(14px); -webkit-backdrop-filter:blur(14px);
                  border-top:1px solid rgba(255,255,255,.06);
                  padding:10px 0 calc(10px + env(safe-area-inset-bottom)); }
          .dockin { max-width:860px; margin:0 auto; padding:0 16px; }
          .composer { display:flex; gap:8px; position:relative; }
          .composer input { flex:1; background:#101013; color:#e7e7ea; font-size:14px;
                  border:1px solid #2a2a2e; border-radius:11px; padding:10px 13px;
                  outline:none; min-width:0; }
          .composer input:focus { border-color:#CC785C; }
          .composer button { background:#CC785C; color:#fff; border:none; border-radius:11px;
                  padding:10px 17px; font-size:13px; flex:none; cursor:pointer; }
          .composer button:disabled { opacity:.5; }
          .acmenu { display:none; position:absolute; bottom:100%; left:0; margin-bottom:6px;
                    min-width:260px; max-height:230px; overflow:auto; z-index:9;
                    background:#1c1c1f; border:1px solid #3a3a3e; border-radius:10px; }
          .acmenu div { padding:7px 11px; font-size:13px; cursor:pointer; }
          .acmenu div:hover { background:#26262b; }
          .acmenu b { color:#CC785C; font-weight:600; }
          .acmenu span { color:#8a8a90; margin-left:8px; font-size:12px; }
          .toolbar { display:flex; align-items:center; gap:8px 12px; margin-top:8px;
                     font-size:11.5px; color:#8a8a90;
                     flex-wrap:wrap; }            /* 手机宽度下下拉/计数/余额换行而不是竖排溢出 */
          .toolbar span { white-space:nowrap; }
          .toolbar select { background:transparent; color:#9aa0a6; border:1px solid #2a2a2e;
                  border-radius:8px; padding:4px 7px; font-size:11.5px; max-width:150px; }
          .quota { display:flex; align-items:center; gap:5px; margin-left:auto; flex:none; }
          .qd { width:7px; height:7px; border-radius:50%; background:#5A5A5E; flex:none; }
          .sendmsg { font-size:11px; color:#9aa0a6; margin-top:4px; min-height:13px; }
          .sendmsg.err { color:#e07a6a; }
          /* ── 白天模式（所有组件都要有对应覆盖） ── */
          body.light { background:#f2f3f5; color:#1c1c1f; }
          .light .hdr { background:rgba(243,244,246,.85); border-bottom-color:rgba(0,0,0,.07); }
          .light .iconbtn { background:#fff; border-color:#d8dade; color:#1c1c1f; }
          .light .statline { color:#7a7f87; }
          .light .tab { background:#fff; border-color:#d8dade; color:#5a5f66; }
          .light .tab.on { background:#f7e8df; border-color:#CC785C; color:#1c1c1f; }
          .light .msg.assistant { background:#ffffff; }
          .light .msg.user { background:#f7e3d6; }
          .light .msg pre.code { background:#f0f1f4; border-color:#e1e2e6; color:#1c1c1f; }
          .light .code .dadd { color:#1f7a4d; }
          .light .code .ddel { color:#c0392b; }
          .light .msg code { background:#eceef1; }
          .light .perm,.light .qbox { background:rgba(255,152,0,.1);
                                      border-color:rgba(230,126,0,.5); }
          .light .permhead,.light .qhint { color:#c46a00; }
          .light .permdesc { color:#5a5f66; }
          .light .pb.always { background:#5a5f66; }
          .light .pb.qopt { background:#3d6e91; }
          .light .permok { color:#1f7a4d; }
          .light .dock { background:rgba(243,244,246,.9); border-top-color:rgba(0,0,0,.07); }
          .light .composer input { background:#fff; color:#1c1c1f; border-color:#d8dade; }
          .light .toolbar { color:#7a7f87; }
          .light .toolbar select { color:#5a5f66; border-color:#d8dade; }
          .light .acmenu { background:#fff; border-color:#d8dade; }
          .light .acmenu div:hover { background:#f0f1f4; }
          .light .empty { color:#9aa0a6; }
        </style>
        </head>
        <body>
        <div class="hdr">
          <div class="topbar">
            <h1>Open Island</h1>
            <span class="statline" id="statline"></span>
            <button class="iconbtn" id="soundBtn" onclick="toggleSound()" title="声音提醒">&#128277;</button>
            <button class="iconbtn" id="themeBtn" onclick="toggleTheme()" title="日夜主题">&#127769;</button>
          </div>
          <div class="tabs" id="tabs"></div>
        </div>
        <div class="chat" id="chat"><div class="chatin" id="chatin"><div class="empty">连接中&#8230;</div></div></div>
        <div class="dock">
          <div class="dockin">
            <div class="composer">
              <div class="acmenu"></div>
              <input type="text" id="msgIn" placeholder="回复&#8230; 输入 / 出命令"
               oninput="onType(this)" onblur="hideAc(this)"
               onkeydown="if(event.key===&quot;Enter&quot;&amp;&amp;!event.isComposing&amp;&amp;event.keyCode!==229)sendActive()">
              <button onclick="sendActive()">发送</button>
            </div>
            <div class="toolbar">
              <select id="mdl" onchange="modelChange(this)"><option value="">&#9881; 切换模型&#8230;</option></select>
              <select id="pmd" onchange="permModeChange(this)">
                <option value="">&#128737; 权限模式&#8230;</option>
                <option value="default">默认（逐项确认）</option>
                <option value="acceptEdits">自动接受编辑</option>
                <option value="plan">计划模式</option>
                <option value="auto">自动模式（auto）</option>
              </select>
              <span id="tokline"></span>
              <span class="quota"><span class="qd" id="qdot"></span><span id="quota">5h 余 --</span></span>
            </div>
            <div class="sendmsg" id="sendmsg"></div>
          </div>
        </div>
        <script>
        // ── 访问令牌（鉴权）：URL 的 ?t= 带进来，存 localStorage 持久化（刷新/扫码后免重传）。
        //    所有 /api/* 的 fetch 都带 X-OI-Token 头；SSE（EventSource 不能带自定义头）改用 ?t= 查询参数。──
        var OI_TOKEN=(function(){
          try{
            var m=/[?&]t=([^&]+)/.exec(location.search);
            if(m&&m[1]){var t=decodeURIComponent(m[1]);localStorage.setItem('oi-token',t);return t;}
          }catch(e){}
          try{return localStorage.getItem('oi-token')||'';}catch(e){return '';}
        })();
        // 统一带 token 的 fetch：自动注入 X-OI-Token 头（GET/POST 通用）。
        function apiFetch(url,opts){
          opts=opts||{};
          var h=opts.headers?Object.assign({},opts.headers):{};
          if(OI_TOKEN)h['X-OI-Token']=OI_TOKEN;
          opts.headers=h;
          return fetch(url,opts);
        }
        // ── 基础工具 ──
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;')
          .replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function dotColor(p){
          if(p==='Running')return '#2196F3';
          if(p==='WaitingForApproval'||p==='WaitingForAnswer')return '#FF9800';
          if(p==='Completed')return '#9E9E9E';
          return '#4CAF50';
        }
        function modeText(m){
          if(m==='acceptEdits')return '自动接受编辑';
          if(m==='plan')return '计划模式';
          if(m==='auto')return '自动模式';
          if(m==='bypassPermissions')return '跳过权限';
          if(m==='default')return '默认询问';
          return m;
        }
        function humanTokens(n){
          n=Number(n)||0;
          if(n>=1e6)return (n/1e6).toFixed(1)+'M tok';
          if(n>=1000)return Math.round(n/1000)+'K tok';
          return n+' tok';
        }
        function relTime(ms){
          var diff=Date.now()-ms;
          if(diff<60000)return '刚刚';
          if(diff<3600000)return Math.floor(diff/60000)+' 分钟前';
          var d=new Date(ms);
          return (d.getHours()<10?'0':'')+d.getHours()+':'
                +(d.getMinutes()<10?'0':'')+d.getMinutes();
        }
        function h32(s){var h=0;for(var i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))|0;return h;}
        // ── 主题：localStorage 记住；首次无记录按系统 prefers-color-scheme ──
        function applyTheme(){
          var saved=localStorage.getItem('oi-theme');
          var light=saved?saved==='light'
            :!!(window.matchMedia&&window.matchMedia('(prefers-color-scheme: light)').matches);
          document.body.classList.toggle('light',light);
          document.getElementById('themeBtn').innerHTML=light?'&#9728;':'&#127769;';
          var tc=document.querySelector('meta[name=theme-color]');
          if(tc)tc.setAttribute('content',light?'#f2f3f5':'#0c0c0e');
        }
        function toggleTheme(){
          localStorage.setItem('oi-theme',
            document.body.classList.contains('light')?'dark':'light');
          applyTheme();
        }
        // ── 声音提醒（WebAudio 两短声）：必须用户手势内解锁 AudioContext ──
        var soundOn=localStorage.getItem('oi-sound')==='on';
        var audioCtx=null;
        function updateSoundBtn(){
          var b=document.getElementById('soundBtn');
          b.innerHTML=soundOn?'&#128276;':'&#128277;';
          b.style.opacity=soundOn?'1':'.55';
        }
        function ensureAudio(){
          try{
            if(!audioCtx)audioCtx=new (window.AudioContext||window.webkitAudioContext)();
            if(audioCtx.state==='suspended')audioCtx.resume();
          }catch(e){audioCtx=null;}
        }
        function toggleSound(){
          soundOn=!soundOn;
          localStorage.setItem('oi-sound',soundOn?'on':'off');
          if(soundOn){ensureAudio();beep();}
          updateSoundBtn();
        }
        function beep(){
          if(!soundOn)return;
          ensureAudio();
          if(!audioCtx)return;
          try{var t=audioCtx.currentTime;tone(t);tone(t+0.2);}catch(e){}
        }
        function tone(start){
          var o=audioCtx.createOscillator(),g=audioCtx.createGain();
          o.type='sine';o.frequency.value=880;
          g.gain.setValueAtTime(0.0001,start);
          g.gain.linearRampToValueAtTime(0.25,start+0.01);
          g.gain.exponentialRampToValueAtTime(0.0001,start+0.12);
          o.connect(g);g.connect(audioCtx.destination);
          o.start(start);o.stop(start+0.16);
        }
        // 刷新后 soundOn 恢复为开时，第一次触屏顺手解锁 AudioContext（自动播放策略）
        document.addEventListener('pointerdown',function unlockAudio(){
          if(soundOn)ensureAudio();
          document.removeEventListener('pointerdown',unlockAudio);
        });
        // ── 标题闪烁：有待批准且页面不在前台时提醒 ──
        var baseTitle=document.title,flashTimer=null,flashOn=false,curAttention=0;
        function updateFlash(){
          if(curAttention>0&&(document.hidden||!document.hasFocus())){
            if(!flashTimer)flashTimer=setInterval(function(){
              flashOn=!flashOn;
              document.title=flashOn?'● 待批准 '+curAttention+' · Open Island':baseTitle;
            },1000);
          }else{stopFlash();}
        }
        function stopFlash(){
          if(flashTimer){clearInterval(flashTimer);flashTimer=null;}
          flashOn=false;
          document.title=baseTitle;
        }
        window.addEventListener('focus',function(){stopFlash();scheduleRefresh();});
        window.addEventListener('blur',updateFlash);
        document.addEventListener('visibilitychange',updateFlash);
        // ── 顶部统计 + 底部 5h 余额（与灵动岛同一口径） ──
        var lastAttention=null;
        function applyStats(st){
          if(!st)return;
          curAttention=st.attention||0;
          var el=document.getElementById('statline');
          if(el)el.textContent='运行 '+(st.running||0)+' · 待批准 '+(st.attention||0)
            +' · 空闲 '+(st.idle||0);
          renderQuota(st);
          if(lastAttention!==null&&curAttention>lastAttention&&soundOn)beep();
          lastAttention=curAttention;
          updateFlash();
        }
        function renderQuota(st){
          var q=document.getElementById('quota'),d=document.getElementById('qdot');
          if(!q||!d)return;
          if(st.usageApi){
            q.textContent=humanTokens(st.usageTokens||0)+' 累计';
            d.style.background='#5A5A5E';
            return;
          }
          var left=(st.usageLeft==null?-1:st.usageLeft);
          if(left<0){q.textContent='5h 余 --';d.style.background='#5A5A5E';return;}
          var t='5h 余 '+left+'%';
          if(st.usageReset)t+=' · '+st.usageReset+' 后重置';
          q.textContent=t;
          d.style.background=left<=10?'#E74C3C':left<=30?'#FF9F0A':'#30D158';
        }
        // ── 模型列表（底部工具行下拉），失败 5s 重试 ──
        function loadModels(){
          apiFetch('/api/models').then(function(r){return r.json();}).then(function(ms){
            var sel=document.getElementById('mdl');
            var opts='<option value="">&#9881; 切换模型&#8230;</option>';
            for(var i=0;i<ms.length;i++)
              opts+='<option value="'+esc(ms[i].slug)+'">'+esc(ms[i].name)+'</option>';
            sel.innerHTML=opts;
          }).catch(function(){setTimeout(loadModels,5000);});
        }
        function modelChange(sel){
          var slug=sel.value;
          if(!slug)return;
          sel.value='';
          if(activeSid)sendText(activeSid,'/model '+slug,null);
        }
        // ── / 命令自动补全 ──
        var SLASH=[
          ['/add-dir','添加工作目录'],['/agents','管理子代理'],['/clear','清空对话'],
          ['/compact','压缩上下文'],['/config','打开设置'],['/context','上下文占用'],
          ['/cost','本次花费'],['/doctor','诊断安装'],['/exit','退出'],
          ['/export','导出对话'],['/help','帮助'],['/hooks','管理 hooks'],
          ['/ide','连接 IDE'],['/init','生成 CLAUDE.md'],['/login','登录'],
          ['/logout','登出'],['/mcp','管理 MCP'],['/memory','编辑记忆'],
          ['/model','切换模型'],['/output-style','输出风格'],['/permissions','权限设置'],
          ['/pr-comments','PR 评论'],['/resume','恢复会话'],['/review','代码评审'],
          ['/rewind','回退检查点'],['/status','状态'],['/statusline','状态栏设置'],
          ['/terminal-setup','终端回车设置'],['/todos','待办列表'],['/usage','用量'],
          ['/vim','Vim 模式']
        ];
        function onType(input){
          var menu=input.parentElement.querySelector('.acmenu');
          var v=input.value;
          if(v.indexOf('/')===0&&v.indexOf(' ')<0){
            var hits=SLASH.filter(function(c){return c[0].indexOf(v)===0;}).slice(0,9);
            if(hits.length){
              menu.innerHTML=hits.map(function(c){
                return '<div onmousedown="pick(event,this)" data-cmd="'+c[0]+'"><b>'
                      +c[0]+'</b><span>'+c[1]+'</span></div>';
              }).join('');
              menu.style.display='block';
              return;
            }
          }
          menu.style.display='none';
        }
        function pick(ev,item){
          ev.preventDefault();
          var menu=item.parentElement;
          var input=menu.parentElement.querySelector('input');
          input.value=item.getAttribute('data-cmd')+' ';
          menu.style.display='none';
          input.focus();
        }
        function hideAc(input){
          setTimeout(function(){
            var m=input.parentElement.querySelector('.acmenu');
            if(m)m.style.display='none';
          },150);
        }
        // ── 消息富渲染：先 esc 全文，再切 ``` 围栏与 `内联代码` ──
        function renderBody(t){
          var s=esc(t);
          var parts=s.split('```');
          var out='';
          for(var i=0;i<parts.length;i++){
            if(i%2===1){
              var lines=parts[i].split('\n');
              if(lines.length>1&&/^[A-Za-z0-9_+-]{0,20}$/.test(lines[0]))lines.shift();
              if(lines.length>1&&lines[lines.length-1]==='')lines.pop();
              var lh='';
              for(var j=0;j<lines.length;j++){
                var ln=lines[j],cls='';
                if(ln.indexOf('+ ')===0)cls=' class="dadd"';
                else if(ln.indexOf('- ')===0)cls=' class="ddel"';
                lh+='<span'+cls+'>'+ln+'</span>';
                if(j<lines.length-1)lh+='\n';
              }
              out+='<pre class="code">'+lh+'</pre>';
            }else{
              out+=parts[i].replace(/`([^`]+)`/g,'<code>$1</code>');
            }
          }
          return out;
        }
        function collapsedHtml(t){
          return renderBody(t.slice(0,300))+'&#8230; <span class="more">展开</span>';
        }
        // 展开状态记在 JS 侧（键 = 会话id:文本hash），整块重建后仍保留
        var expKeys=new Set();
        function msgHtml(m,sid){
          var cls=m.role==='user'?'user':'assistant';
          var t=m.text||'';
          if(t.length>320){
            var k=sid+':'+h32(t);
            if(expKeys.has(k))
              return '<div class="msg '+cls+'" data-full="'+esc(t)+'" data-k="'+k+'" data-exp="1">'
                    +renderBody(t)+' <span class="more">收起</span></div>';
            return '<div class="msg '+cls+'" data-full="'+esc(t)+'" data-k="'+k+'" data-exp="0">'
                  +collapsedHtml(t)+'</div>';
          }
          return '<div class="msg '+cls+'">'+renderBody(t)+'</div>';
        }
        document.addEventListener('click',function(ev){
          var t=ev.target;
          if(!t||!t.classList||!t.classList.contains('more'))return;
          var msg=t.closest('.msg');
          if(!msg)return;
          var full=msg.getAttribute('data-full')||'';
          var k=msg.getAttribute('data-k')||'';
          if(msg.getAttribute('data-exp')==='1'){
            msg.setAttribute('data-exp','0');
            if(k)expKeys.delete(k);
            msg.innerHTML=collapsedHtml(full);
          }else{
            msg.setAttribute('data-exp','1');
            if(k)expKeys.add(k);
            msg.innerHTML=renderBody(full)+' <span class="more">收起</span>';
          }
        });
        // ── 会话标签 + 单会话视图 ──
        var lastSessions=[],drafts={},lastChatKey='';
        var activeSid=localStorage.getItem('oi-active')||'';
        function pickActive(){
          if(lastSessions.length===0)return '';
          for(var i=0;i<lastSessions.length;i++)
            if(lastSessions[i].id===activeSid)return activeSid;
          // 原 active 不在列表里了：优先跳到待批准的，其次第一个
          for(var i=0;i<lastSessions.length;i++){
            var p=lastSessions[i].phase;
            if(p==='WaitingForApproval'||p==='WaitingForAnswer')return lastSessions[i].id;
          }
          return lastSessions[0].id;
        }
        function switchTab(id){
          if(id===activeSid)return;
          var inp=document.getElementById('msgIn');
          if(inp)drafts[activeSid]=inp.value;   // 每个会话的未发送草稿独立保存
          activeSid=id;
          localStorage.setItem('oi-active',id);
          if(inp)inp.value=drafts[id]||'';
          lastChatKey='';
          renderTabs();
          renderActive(true);
        }
        function renderTabs(){
          var box=document.getElementById('tabs');
          var sl=box.scrollLeft;
          var h='';
          for(var i=0;i<lastSessions.length;i++){
            var s=lastSessions[i];
            h+='<div class="tab'+(s.id===activeSid?' on':'')
              +'" onclick="switchTab(&quot;'+esc(s.id)+'&quot;)">'
              +'<span class="td" style="background:'+dotColor(s.phase)+'"></span>'
              +(s.featured?'<span class="star">&#9733;</span>':'')
              +'<span class="tt">'+esc(s.title||s.id)+'</span>'
              +'</div>';
          }
          box.innerHTML=h;
          box.scrollLeft=sl;
        }
        function findActive(){
          for(var i=0;i<lastSessions.length;i++)
            if(lastSessions[i].id===activeSid)return lastSessions[i];
          return null;
        }
        function updateTokline(){
          var el=document.getElementById('tokline');
          if(!el)return;
          var s=findActive();
          el.textContent=s?humanTokens(s.tokens)+' · '+relTime(s.updatedMs||0)
            +(s.mode?' · '+modeText(s.mode):''):'';
        }
        // ── 权限模式切换：精确切（后端按 hook 上报的当前模式算 Shift+Tab 步数） ──
        function permModeChange(sel){
          var mode=sel.value;
          if(!mode)return;
          sel.value='';
          if(!activeSid)return;
          var sm=document.getElementById('sendmsg');
          if(sm){sm.textContent='切换模式中…';sm.className='sendmsg';}
          sel.disabled=true;
          apiFetch('/api/setmode',{method:'POST',
            headers:{'Content-Type':'application/json'},
            body:JSON.stringify({id:activeSid,mode:mode})})
          .then(function(r){return r.json();})
          .then(function(res){
            sel.disabled=false;
            if(res.ok){
              if(sm)sm.textContent=res.reasonText||('已切换：'+modeText(mode));
              scheduleRefresh();
            }else{
              if(sm){sm.textContent='失败：'+(res.reasonText||res.reason||'');
                     sm.className='sendmsg err';}
            }
          })
          .catch(function(){
            sel.disabled=false;
            if(sm){sm.textContent='网络错误';sm.className='sendmsg err';}
          });
        }
        function renderActive(force){
          var chat=document.getElementById('chat');
          var chatin=document.getElementById('chatin');
          var sid=pickActive();
          if(sid!==activeSid){
            activeSid=sid;
            if(sid)localStorage.setItem('oi-active',sid);
            lastChatKey='';
            force=true;
          }
          if(!sid){
            chatin.innerHTML='<div class="empty">暂无会话<br>在灵动岛展开列表，点会话状态圆点选择要同步的对话；<br>选了几个，这里就有几个标签</div>';
            document.getElementById('tokline').textContent='';
            lastChatKey='';
            return;
          }
          var s=findActive();
          if(!s)return;
          updateTokline();
          var key=sid+'|'+s.updatedMs+':'+(s.messages?s.messages.length:0)+':'+s.phase
            +':'+(s.permission?h32(String(s.permission.tool||'')+'|'+(s.permission.desc||'')):0)
            +':'+(s.question?h32(JSON.stringify(s.question)):0);
          if(!force&&key===lastChatKey)return;
          lastChatKey=key;
          // 粘底判定：本来就在底部附近 → 重建后继续贴底；在上面翻历史 → 保持原位置
          var nearBottom=chat.scrollHeight-chat.scrollTop-chat.clientHeight<90;
          var prevTop=chat.scrollTop;
          var h='';
          var arr=s.messages||[];
          if(arr.length===0)h+='<div class="empty">(暂无消息)</div>';
          for(var i=0;i<arr.length;i++)h+=msgHtml(arr[i],sid);
          if(s.permission){
            var p=s.permission;
            h+='<div class="perm">'
              +'<div class="permhead">&#9888; 权限请求 &#183; '+esc(p.tool||'')+'</div>'
              +'<div class="permdesc">'+esc(p.desc||'')+'</div>'
              +'<div class="permbtns">'
              +'<button class="pb allow" data-d="1" onclick="approve(this)">1 允许</button>'
              +'<button class="pb always" data-d="2" onclick="approve(this)">2 '
              +esc(p.btn2||'本次都允许')+'</button>'
              +'<button class="pb deny" data-d="3" onclick="approve(this)">3 拒绝</button>'
              +'</div></div>';
          }
          if(s.question){
            var qp=s.question,qo=qp.options||[];
            h+='<div class="qbox"><div class="permhead">&#10067; Claude 在等你回答</div>'
              +'<div class="qtext">'+esc(qp.prompt||'')+'</div>';
            if(qo.length){
              h+='<div class="permbtns" style="margin-top:9px">';
              for(var qi=0;qi<qo.length;qi++){
                h+='<button class="pb qopt" data-n="'+qo[qi].n+'" onclick="answerQ(this)"'
                  +' title="'+esc(qo[qi].desc||'')+'">'+qo[qi].n+' '+esc(qo[qi].label||'')+'</button>';
              }
              h+='<button class="pb deny" data-n="0" onclick="answerQ(this)">跳过</button></div>'
                +'<div class="qhint">点选项直接回答；也可在下方输入框自由回复</div>';
            }else{
              h+='<div class="qhint">在下方输入框回复编号或内容</div>';
            }
            h+='</div>';
          }
          chatin.innerHTML=h;
          if(force||nearBottom)chat.scrollTop=chat.scrollHeight;
          else chat.scrollTop=prevTop;
        }
        // 30s 刷一次 tokens 行里的相对时间
        setInterval(updateTokline,30000);
        // ── 回答提问：POST /api/answer {id,option} / {id,skip}（id = 当前标签会话） ──
        function answerQ(btn){
          var n=parseInt(btn.getAttribute('data-n'),10);
          var box=btn.parentElement;
          var bs=box.querySelectorAll('button');
          for(var i=0;i<bs.length;i++)bs[i].disabled=true;
          apiFetch('/api/answer',{method:'POST',
            headers:{'Content-Type':'application/json'},
            body:JSON.stringify(n>0?{id:activeSid,option:n}:{id:activeSid,skip:true})})
          .then(function(r){return r.json();})
          .then(function(res){
            if(res.ok){
              box.innerHTML='<span class="permok">已回答 &#10003;</span>';
              scheduleRefresh();
            }else{
              for(var i=0;i<bs.length;i++)bs[i].disabled=false;
              var sm=document.getElementById('sendmsg');
              if(sm){sm.textContent='失败：'+(res.reasonText||'');sm.className='sendmsg err';}
            }
          })
          .catch(function(){
            for(var i=0;i<bs.length;i++)bs[i].disabled=false;
          });
        }
        // ── 审批：POST /api/approve {id,digit}（id = 当前标签会话） ──
        function approve(btn){
          var digit=btn.getAttribute('data-d');
          var sid=activeSid;
          var box=btn.parentElement;
          var bs=box.querySelectorAll('button');
          for(var i=0;i<bs.length;i++)bs[i].disabled=true;
          apiFetch('/api/approve',{method:'POST',
            headers:{'Content-Type':'application/json'},
            body:JSON.stringify({id:sid,digit:digit})})
          .then(function(r){return r.json();})
          .then(function(res){
            if(res.ok){
              box.innerHTML='<span class="permok">已发送 &#10003;</span>';
              scheduleRefresh();
            }else{
              for(var i=0;i<bs.length;i++)bs[i].disabled=false;
            }
          })
          .catch(function(){
            for(var i=0;i<bs.length;i++)bs[i].disabled=false;
          });
        }
        // ── 发送：POST /api/send，成功后乐观回显 ──
        async function sendText(id,text,inputToClear){
          var sm=document.getElementById('sendmsg');
          if(sm){sm.textContent='发送中…';sm.className='sendmsg';}
          try{
            var r=await apiFetch('/api/send',{method:'POST',
              headers:{'Content-Type':'application/json'},
              body:JSON.stringify({id:id,text:text})});
            var res=await r.json();
            if(res.ok){
              if(inputToClear)inputToClear.value='';
              drafts[id]='';
              if(sm)sm.innerHTML='已发送 &#10003;';
              if(id===activeSid){
                var chatin=document.getElementById('chatin');
                var chat=document.getElementById('chat');
                var d=document.createElement('div');
                d.className='msg user ghost';
                d.textContent=text;
                chatin.appendChild(d);
                chat.scrollTop=chat.scrollHeight;
              }
              scheduleRefresh();
            }else{
              if(sm){sm.textContent='失败：'+(res.reasonText||res.reason||'');
                     sm.className='sendmsg err';}
            }
          }catch(e){
            if(sm){sm.textContent='网络错误';sm.className='sendmsg err';}
          }
        }
        function sendActive(){
          var inp=document.getElementById('msgIn');
          var text=inp.value.trim();
          if(!text||!activeSid)return;
          sendText(activeSid,text,inp);
        }
        // ── SSE 推送 + 150ms 去抖拉取；断线降级 5s 轮询；乱序响应丢弃 ──
        var pollTimer=null,refreshTimer=null,fetchSeq=0;
        function scheduleRefresh(){
          if(refreshTimer)clearTimeout(refreshTimer);
          refreshTimer=setTimeout(refresh,150);
        }
        function startSse(){
          try{
            // EventSource 不能带自定义请求头：token 走查询参数 ?t=
            var es=new EventSource('/api/events?t='+encodeURIComponent(OI_TOKEN));
            es.onopen=function(){
              if(pollTimer){clearInterval(pollTimer);pollTimer=null;}
            };
            es.onmessage=function(ev){
              try{
                var d=JSON.parse(ev.data);
                if(d&&d.stats)applyStats(d.stats);
              }catch(e){}
              scheduleRefresh();
            };
            es.onerror=function(){
              if(!pollTimer)pollTimer=setInterval(refresh,5000);
            };
          }catch(e){
            pollTimer=setInterval(refresh,5000);
          }
        }
        async function refresh(){
          var my=++fetchSeq;
          try{
            var r=await apiFetch('/api/sessions');
            var data=await r.json();
            if(my!==fetchSeq)return;
            if(data.stats)applyStats(data.stats);
            lastSessions=data.sessions||[];
            renderTabs();
            renderActive(false);
          }catch(e){/* 服务端重启 / 网络抖动 —— 下次推送或轮询再试 */}
        }
        // ── 启动 ──
        applyTheme();
        updateSoundBtn();
        refresh();
        loadModels();
        startSse();
        </script>
        </body>
        </html>
        """;
}
