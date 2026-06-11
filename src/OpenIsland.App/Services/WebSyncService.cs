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

    public WebSyncService(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        // 会话有任何变化（阶段切换/权限请求/新会话…）就推 SSE —— 网页端不用再 2.5s 盲轮询。
        // IsRunning 守门：服务没开时不必启动去抖定时器。
        _sessionManager.SessionsChanged += (_, _) => { if (IsRunning) NotifyChanged(); };
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
                _ = Task.Run(() => HandleClient(client));
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
    /// </summary>
    public string GetUrl()
    {
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
                        return $"http://{addr.Address}:{Port}/";
                    }
                }
            }
        }
        catch { /* 网卡枚举失败走 localhost 兜底 */ }
        return $"http://localhost:{Port}/";
    }

    /// <summary>
    /// 处理单个客户端。头部按字节读到 \r\n\r\n（不能用带缓冲的 StreamReader —— 它会把
    /// POST body 的字节也吞进自己的缓冲区，后续读 body 就缺数据），body 按 Content-Length
    /// 精确读取。整个方法 try/catch 全包 —— 手机锁屏断开 / 半截请求都静默关连接，不影响别人。
    ///
    /// 不再 using(client) 包整个生命周期：/api/events（SSE）是长连接，所有权移交给
    /// _sseClients 后这里必须**不关**；其余短请求在 finally 里关。
    /// </summary>
    private void HandleClient(TcpClient client)
    {
        NetworkStream? stream = null;
        bool handedOff = false; // true = SSE 已接管连接，finally 不许 Dispose
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

            // SSE 路由提前判断：不读 body（GET 没有）、写完头和 hello 帧就移交所有权返回
            if (method == "GET" && (path == "/api/events" || path.StartsWith("/api/events?")))
            {
                handedOff = TryStartSse(client, stream);
                return;
            }

            int contentLength = 0;
            foreach (var hl in lines)
            {
                if (hl.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(hl["Content-Length:".Length..].Trim(), out contentLength);
            }

            byte[] body = Array.Empty<byte>();
            if (contentLength > 0 && contentLength <= 64 * 1024)
            {
                body = new byte[contentLength];
                int off = 0;
                while (off < contentLength)
                {
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
            else if (method == "POST" && path == "/api/send")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", HandleSend(body));
            else if (method == "POST" && path == "/api/approve")
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", HandleApprove(body));
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
    /// /api/events：写 SSE 响应头 + hello 帧后把连接放进长连接池。
    /// 成功返回 true（调用方不许再关这条连接）；任何一步写失败返回 false 走正常清理。
    /// </summary>
    private bool TryStartSse(TcpClient client, NetworkStream stream)
    {
        try
        {
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
    /// 给所有 SSE 客户端写同一帧字节。写失败 = 客户端已断（锁屏/切网/关页面），
    /// 移除并 Dispose。锁按连接粒度（见 _sseClients 注释）：同一条流上心跳与广播
    /// 仍串行（帧不交错），但一个写超时卡 10s 的客户端只阻塞自己。
    /// </summary>
    private void SseBroadcast(byte[] frame)
    {
        foreach (var kv in _sseClients)
        {
            try
            {
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
        }
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
    /// 窗口/动剪贴板，必须在 UI 线程执行；本方法在 HTTP 线程上同步等结果（不阻塞 UI）。
    /// </summary>
    private string HandleSend(byte[] body)
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

            // Dispatcher.Invoke 返回 UI 线程上启动的 Task；在 HTTP 线程阻塞等它完成
            //（Task 的延续跑在 UI Dispatcher 上，与这里的阻塞互不相干，无死锁）。
            var task = app.Dispatcher.Invoke(() => _sessionManager.SendQuickReplyAsync(id!, text!));
            var result = task.GetAwaiter().GetResult();
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
        "clipboard-failed" => "电脑剪贴板被占用，请重试",
        _ => "发送失败"
    };

    /// <summary>
    /// POST /api/approve：{"id":"会话id","digit":"1"|"2"|"3"} → 复用灵动岛权限按钮的通路
    /// （SessionManager.RespondToPermissionAsync 内部已分流 CLI 注入 / 桌面 UIA）。
    /// 与 /api/send 同模式：注入必须在 UI 线程跑，HTTP 线程阻塞等结果。
    /// </summary>
    private string HandleApprove(byte[] body)
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

            // Dispatcher.Invoke 返回 UI 线程上启动的 Task；HTTP 线程阻塞等它完成
            //（Task 延续跑在 UI Dispatcher 上，与这里的阻塞互不相干，无死锁）。
            var task = app.Dispatcher.Invoke(() => _sessionManager.RespondToPermissionAsync(id!, digit[0]));
            var ok = task.GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new { ok });
        }
        catch
        {
            return """{"ok":false}""";
        }
    }

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
                    permission = BuildPermission(x.Session.PermissionRequest),
                    question = x.Session.QuestionPrompt?.Prompt,
                    messages = ReadTail(x.Path, filterMode ? 60 : 12)
                })
                .ToList();
            return JsonSerializer.Serialize(new { stats = BuildStats(), sessions = items });
        }
        catch
        {
            return """{"stats":{"running":0,"attention":0,"idle":0,"total":0},"sessions":[]}""";
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

    /// <summary>stats 的 JSON 形状。属性名刻意小写 —— 直接成为 JSON 键（同 TailMessage）。</summary>
    private sealed class StatsDto
    {
        public int running { get; set; }
        public int attention { get; set; }
        public int idle { get; set; }
        public int total { get; set; }
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
    /// 内嵌的同步页面：SSE（/api/events）推送 + 150ms 去抖拉取 /api/sessions，
    /// 按 sig 比对做增量 DOM 渲染（只重建变化的卡，滚动位置天然保留），SSE 断开时
    /// 自动降级 5s 轮询。esc() 对所有动态文本做 HTML 转义 —— transcript 内容不可信
    /// （可能含尖括号代码）。注意：C# 原始字符串里不能出现连续三个双引号，HTML 属性
    /// 内嵌 JS 字符串一律用 &amp;quot; 实体规避。
    /// </summary>
    private const string IndexHtml = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="theme-color" content="#0c0c0e">
        <meta name="apple-mobile-web-app-capable" content="yes">
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
        <link rel="apple-touch-icon" href="/icon.png">
        <link rel="icon" href="/icon.png">
        <title>Open Island · Web Sync</title>
        <style>
          body { margin:0; padding:0 16px calc(16px + env(safe-area-inset-bottom));
                 background:#0c0c0e; color:#e7e7ea;
                 font-family:-apple-system,'Segoe UI',Roboto,sans-serif; }
          /* ── 吸顶毛玻璃头部：第一行标题+圆钮，第二行状态 chips ── */
          .hdr { position:sticky; top:0; z-index:20; margin:0 -16px 12px;
                 padding:calc(10px + env(safe-area-inset-top)) 16px 8px;
                 background:rgba(12,12,14,.78);
                 backdrop-filter:blur(14px); -webkit-backdrop-filter:blur(14px);
                 border-bottom:1px solid rgba(255,255,255,.06); }
          .hwrap { max-width:760px; margin:0 auto; }
          .topbar { display:flex; align-items:center; gap:8px; }
          h1 { font-size:17px; margin:0; flex:1; overflow:hidden;
               text-overflow:ellipsis; white-space:nowrap; }
          .iconbtn { width:34px; height:34px; border-radius:50%; flex:none; padding:0;
                     background:#1c1c1f; border:1px solid #2a2a2e; color:#e7e7ea;
                     font-size:15px; cursor:pointer; }
          .chips { display:flex; gap:8px; margin-top:9px; overflow-x:auto; }
          .chip { display:flex; align-items:center; gap:6px; flex:none; cursor:pointer;
                  background:#1c1c1f; border:1px solid #2a2a2e; border-radius:999px;
                  padding:4px 12px; font-size:12px; color:#9aa0a6; user-select:none; }
          .chip b { font-weight:600; color:#e7e7ea; }
          .chip.on { border-color:#CC785C; background:#2e2118; color:#e7e7ea; }
          .cd { width:8px; height:8px; border-radius:50%; flex:none; }
          .wrap { max-width:760px; margin:0 auto; }
          .wrap.wide { max-width:1560px; }   /* 多选并行时放宽页面 */
          #list.grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(340px,1fr));
                       gap:12px; align-items:start; }
          #list.grid .card { margin-bottom:0; }
          .card { background:#1c1c1f; border:1px solid #2a2a2e; border-radius:14px;
                  padding:12px 14px; margin-bottom:12px; }
          .card.featured { border-color:#CC785C; }
          .card.attention { border-color:#FF9800; }   /* 有待批准权限：橙色描边 */
          .head { display:flex; align-items:center; gap:8px; margin-bottom:2px; }
          .dot { width:9px; height:9px; border-radius:50%; flex:none; }
          .title { font-size:14px; font-weight:600; overflow:hidden;
                   text-overflow:ellipsis; white-space:nowrap; }
          .badge { font-size:10px; color:#9aa0a6; background:#26262b;
                   border-radius:8px; padding:1px 7px; flex:none; }
          .star { font-size:10px; color:#CC785C; background:#2e2118;
                  border-radius:8px; padding:1px 7px; flex:none; }
          .time { font-size:11px; color:#6e6e74; margin-left:auto; flex:none; }
          .meta { font-size:11px; color:#8a8a90; margin:0 0 8px 17px; }
          /* 审批块 / 提问块（橙色系，卡内顶部） */
          .perm,.qbox { background:rgba(255,152,0,.08); border:1px solid rgba(255,152,0,.45);
                        border-radius:10px; padding:10px 12px; margin-bottom:10px; }
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
          .permok { color:#5DCAA5; font-size:13px; font-weight:600; }
          .qtext { font-size:13px; white-space:pre-wrap; word-break:break-word; }
          .qhint { font-size:11px; color:#FF9800; margin-top:6px; }
          .msgs { display:flex; flex-direction:column; gap:6px; }
          .msg { max-width:86%; padding:7px 10px; border-radius:11px; font-size:13px;
                 white-space:pre-wrap; word-break:break-word; line-height:1.45; }
          .user { align-self:flex-end; background:#3a2a1f; }
          .assistant { align-self:flex-start; background:#26262b; }
          .msg.ghost { opacity:.55; }   /* 乐观回显气泡：下次重建被真实数据替换 */
          .msg pre.code { background:#101013; border:1px solid #2a2a2e; border-radius:8px;
                          padding:8px 10px; margin:6px 0; white-space:pre-wrap;
                          word-break:break-all; font-family:ui-monospace,Consolas,monospace;
                          font-size:12px; line-height:1.5; }
          .code .dadd { color:#5DCAA5; }
          .code .ddel { color:#F09595; }
          .msg code { background:#101013; border-radius:4px; padding:1px 5px;
                      font-family:ui-monospace,Consolas,monospace; font-size:12px; }
          .more { color:#CC785C; cursor:pointer; font-size:12px; user-select:none; }
          .empty { color:#6e6e74; font-size:12px; }
          /* 功能栏：输入栏上方（模型切换下拉等） */
          .toolbar { display:flex; gap:8px; margin-top:10px; }
          .toolbar select { background:#101013; color:#9aa0a6; border:1px solid #2a2a2e;
                  border-radius:8px; padding:5px 8px; font-size:12px; }
          .composer { display:flex; gap:8px; margin-top:8px; position:relative; }
          .composer input { flex:1; background:#101013; color:#e7e7ea; font-size:14px;
                  border:1px solid #2a2a2e; border-radius:10px; padding:9px 12px; outline:none; }
          .composer input:focus { border-color:#CC785C; }
          .composer button { background:#CC785C; color:#fff; border:none; border-radius:10px;
                  padding:9px 16px; font-size:13px; flex:none; cursor:pointer; }
          .composer button:disabled { opacity:.5; }
          /* / 命令自动补全面板（悬浮在输入框上方） */
          .acmenu { display:none; position:absolute; bottom:100%; left:0; margin-bottom:6px;
                    min-width:260px; max-height:230px; overflow:auto; z-index:9;
                    background:#1c1c1f; border:1px solid #3a3a3e; border-radius:10px; }
          .acmenu div { padding:7px 11px; font-size:13px; cursor:pointer; }
          .acmenu div:hover { background:#26262b; }
          .acmenu b { color:#CC785C; font-weight:600; }
          .acmenu span { color:#8a8a90; margin-left:8px; font-size:12px; }
          .sendmsg { font-size:11px; color:#9aa0a6; margin-top:5px; min-height:14px; }
          .sendmsg.err { color:#e07a6a; }
          /* ── 白天模式（所有组件都要有对应覆盖） ── */
          body.light { background:#f2f3f5; color:#1c1c1f; }
          .light .hdr { background:rgba(243,244,246,.8); border-bottom-color:rgba(0,0,0,.07); }
          .light .iconbtn { background:#fff; border-color:#d8dade; color:#1c1c1f; }
          .light .chip { background:#fff; border-color:#d8dade; color:#5a5f66; }
          .light .chip b { color:#1c1c1f; }
          .light .chip.on { background:#f7e8df; border-color:#CC785C; color:#1c1c1f; }
          .light .card { background:#ffffff; border-color:#e1e2e6; }
          .light .card.featured { border-color:#CC785C; }
          .light .card.attention { border-color:#FF9800; }
          .light .badge { background:#eceef1; color:#5a5f66; }
          .light .star { background:#f7e8df; }
          .light .meta { color:#7a7f87; }
          .light .perm,.light .qbox { background:rgba(255,152,0,.1);
                                      border-color:rgba(230,126,0,.5); }
          .light .permhead,.light .qhint { color:#c46a00; }
          .light .permdesc { color:#5a5f66; }
          .light .pb.always { background:#5a5f66; }
          .light .permok { color:#1f7a4d; }
          .light .msg.assistant { background:#eef0f3; }
          .light .msg.user { background:#f7e3d6; }
          .light .msg pre.code { background:#f0f1f4; border-color:#e1e2e6; color:#1c1c1f; }
          .light .code .dadd { color:#1f7a4d; }
          .light .code .ddel { color:#c0392b; }
          .light .msg code { background:#eceef1; }
          .light .toolbar select { background:#fff; color:#5a5f66; border-color:#d8dade; }
          .light .composer input { background:#fff; color:#1c1c1f; border-color:#d8dade; }
          .light .acmenu { background:#fff; border-color:#d8dade; }
          .light .acmenu div:hover { background:#f0f1f4; }
          .light .time { color:#9aa0a6; }
          .light .empty { color:#9aa0a6; }
        </style>
        </head>
        <body>
        <div class="hdr">
          <div class="hwrap">
            <div class="topbar">
              <h1>Open Island · Web Sync</h1>
              <button class="iconbtn" id="soundBtn" onclick="toggleSound()" title="声音提醒">&#128277;</button>
              <button class="iconbtn" id="themeBtn" onclick="toggleTheme()" title="日夜主题">&#127769;</button>
            </div>
            <div class="chips">
              <div class="chip on" id="chip-all" onclick="setFilter('all')">全部 <b id="c-total">0</b></div>
              <div class="chip" id="chip-run" onclick="setFilter('run')"><span class="cd" style="background:#2196F3"></span>运行 <b id="c-run">0</b></div>
              <div class="chip" id="chip-att" onclick="setFilter('att')"><span class="cd" style="background:#FF9800"></span>待批准 <b id="c-att">0</b></div>
              <div class="chip" id="chip-idle" onclick="setFilter('idle')"><span class="cd" style="background:#9E9E9E"></span>空闲 <b id="c-idle">0</b></div>
            </div>
          </div>
        </div>
        <div class="wrap">
          <div id="list"><div id="emptyMsg" class="empty">加载中&#8230;</div></div>
        </div>
        <script>
        // ── 基础工具 ──
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;')
          .replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function dotColor(p){
          if(p==='Running')return '#2196F3';
          if(p==='WaitingForApproval'||p==='WaitingForAnswer')return '#FF9800';
          if(p==='Completed')return '#9E9E9E';
          return '#4CAF50';
        }
        function phaseText(p){
          if(p==='Running')return '运行中';
          if(p==='WaitingForApproval')return '待批准';
          if(p==='WaitingForAnswer')return '待回答';
          if(p==='Completed')return '完成';
          return '空闲';
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
        // 每 30s 刷一遍所有相对时间 —— 卡片没重建时时间也要往前走
        setInterval(function(){
          var els=document.querySelectorAll('.reltime');
          for(var i=0;i<els.length;i++){
            var ms=parseInt(els[i].getAttribute('data-ms'),10);
            if(ms)els[i].textContent=relTime(ms);
          }
        },30000);
        // ── 主题：localStorage 记住；首次无记录按系统 prefers-color-scheme ──
        function applyTheme(){
          var saved=localStorage.getItem('oi-theme');
          var light=saved?saved==='light'
            :!!(window.matchMedia&&window.matchMedia('(prefers-color-scheme: light)').matches);
          document.body.classList.toggle('light',light);
          document.getElementById('themeBtn').innerHTML=light?'&#9728;':'&#127769;';
          // theme-color 跟着主题走 —— 否则浅色模式下手机地址栏/状态栏还是深色，和页面割裂
          var tc=document.querySelector('meta[name=theme-color]');
          if(tc)tc.setAttribute('content',light?'#f2f3f5':'#0c0c0e');
        }
        function toggleTheme(){
          localStorage.setItem('oi-theme',
            document.body.classList.contains('light')?'dark':'light');
          applyTheme();
        }
        // ── 声音提醒：局域网 http 拿不到 Notification 权限，用 WebAudio 两短声替代 ──
        var soundOn=localStorage.getItem('oi-sound')==='on';  // 默认关
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
          // 必须在用户点击手势内创建/resume AudioContext，否则移动端拒绝出声；
          // 顺带响一声当即时反馈
          if(soundOn){ensureAudio();beep();}
          updateSoundBtn();
        }
        function beep(){
          if(!soundOn)return;
          ensureAudio();
          if(!audioCtx)return;
          try{
            var t=audioCtx.currentTime;
            tone(t);tone(t+0.2);  // 880Hz 两短声
          }catch(e){}
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
        // 刷新页面后 soundOn 从 localStorage 恢复为开，但 AudioContext 只能在用户手势内
        // 创建/resume（自动播放策略）—— 第一次触屏/点击顺手解锁，否则铃铛亮着却永远无声。
        document.addEventListener('pointerdown',function unlockAudio(){
          if(soundOn)ensureAudio();
          document.removeEventListener('pointerdown',unlockAudio);
        });
        // ── 标题闪烁：有待批准且页面不在前台时提醒，focus/归零即复原 ──
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
        // 最常见路径是"先看到待批准、再切走"—— 此后不会有新 stats 帧来触发 updateFlash，
        // 必须在失焦/切后台的瞬间基于 curAttention 立即重估，否则闪烁永远不会开始。
        window.addEventListener('blur',updateFlash);
        document.addEventListener('visibilitychange',updateFlash);
        // ── 状态 chips + phase 过滤（client 端过滤渲染） ──
        var FILTER='all',lastAttention=null;
        function applyStats(st){
          if(!st)return;
          curAttention=st.attention||0;
          setNum('c-total',st.total);setNum('c-run',st.running);
          setNum('c-att',st.attention);setNum('c-idle',st.idle);
          // attention 比上次增大 → 有新的待批准/待回答；首帧只记录不响
          if(lastAttention!==null&&curAttention>lastAttention&&soundOn)beep();
          lastAttention=curAttention;
          updateFlash();
        }
        function setNum(id,v){var e=document.getElementById(id);if(e)e.textContent=String(v||0);}
        function passFilter(s){
          if(FILTER==='run')return s.phase==='Running';
          if(FILTER==='att')return s.phase==='WaitingForApproval'||s.phase==='WaitingForAnswer';
          if(FILTER==='idle')return s.phase!=='Running'
            &&s.phase!=='WaitingForApproval'&&s.phase!=='WaitingForAnswer';
          return true;
        }
        function setFilter(f){
          FILTER=f;
          var ids=['all','run','att','idle'];
          for(var i=0;i<ids.length;i++){
            var c=document.getElementById('chip-'+ids[i]);
            if(c)c.classList.toggle('on',ids[i]===f);
          }
          renderSessions(lastSessions);  // 用上次数据立即重渲染，不等下次推送
        }
        // ── 模型列表（功能栏下拉）。可能晚于首帧渲染返回：成功后清掉所有 sig 强制重建，
        // 把下拉补进已有卡片（否则 sig 不变卡片不重建，下拉长期只有占位项）；失败 5s 重试。
        var MODEL_OPTS='';
        function loadModels(){
          fetch('/api/models').then(function(r){return r.json();}).then(function(ms){
            MODEL_OPTS=ms.map(function(m){
              return '<option value="'+esc(m.slug)+'">'+esc(m.name)+'</option>';
            }).join('');
            cardMap.forEach(function(v){v.sig='';});
            renderSessions(lastSessions);
          }).catch(function(){setTimeout(loadModels,5000);});
        }
        function modelChange(sel){
          var slug=sel.value;
          if(!slug)return;
          sel.value='';
          sendText(sel.getAttribute('data-id'),'/model '+slug);
        }
        // ── / 命令自动补全（与 Claude Code 客户端一致：/r 出 r 开头命令，点击选用） ──
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
          ev.preventDefault(); // mousedown 抢在 blur 前；阻止默认避免输入框失焦
          var menu=item.parentElement;
          var input=menu.parentElement.querySelector('input');
          input.value=item.getAttribute('data-cmd')+' ';
          menu.style.display='none';
          input.focus();
        }
        function hideAc(input){
          // blur 稍等一拍再收 —— 让 acmenu 的 mousedown 先处理
          setTimeout(function(){
            var m=input.parentElement.querySelector('.acmenu');
            if(m)m.style.display='none';
          },150);
        }
        // ── 消息富渲染：安全第一 —— 先 esc 全文，再切 ``` 围栏与 `内联代码` ──
        function renderBody(t){
          var s=esc(t);
          var parts=s.split('```');
          var out='';
          for(var i=0;i<parts.length;i++){
            if(i%2===1){
              // 奇数段 = 围栏代码块：首行若是语言标记（如 js / diff）则不上墙
              var lines=parts[i].split('\n');
              if(lines.length>1&&/^[A-Za-z0-9_+-]{0,20}$/.test(lines[0]))lines.shift();
              if(lines.length>1&&lines[lines.length-1]==='')lines.pop();
              var lh='';
              for(var j=0;j<lines.length;j++){
                var ln=lines[j],cls='';
                if(ln.indexOf('+ ')===0)cls=' class="dadd"';       // diff 新增行
                else if(ln.indexOf('- ')===0)cls=' class="ddel"';  // diff 删除行
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
        // 长文折叠：>320 字符显示前 300 字 + 展开；原文存 data-full（esc 过，属性安全）
        function collapsedHtml(t){
          return renderBody(t.slice(0,300))+'&#8230; <span class="more">展开</span>';
        }
        // 展开状态记在 JS 侧（键 = 会话id:文本hash）—— 状态若只存在 DOM 节点上，
        // 任何 sig 变化引起的整卡重建都会把读到一半的长文打回折叠态。
        var expKeys=new Set();
        function h32(s){var h=0;for(var i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))|0;return h;}
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
        // 展开/收起用事件委托 —— 消息节点随整卡重建，逐个绑事件不划算
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
        // ── 卡片构建（整卡 innerHTML，一次性拼好） ──
        function buildCard(node,s){
          var cls='card';
          if(s.permission)cls+=' attention';
          if(s.featured)cls+=' featured';
          node.className=cls;
          var h='<div class="head">'
            +'<div class="dot" style="background:'+dotColor(s.phase)+'"></div>'
            +'<div class="title">'+esc(s.title)+'</div>'
            +(s.featured?'<div class="star">&#9733; 已选</div>':'')
            +(s.entry?'<div class="badge">'+esc(s.entry)+'</div>':'')
            +'<div class="time reltime" data-ms="'+(s.updatedMs||0)+'">'
            +relTime(s.updatedMs||0)+'</div>'
            +'</div>';
          h+='<div class="meta">'+phaseText(s.phase)+' &#183; '+humanTokens(s.tokens)+'</div>';
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
            h+='<div class="qbox"><div class="qtext">'+esc(s.question)+'</div>'
              +'<div class="qhint">在下方输入框回复编号或内容</div></div>';
          }
          var msgs='',arr=s.messages||[];
          for(var i=0;i<arr.length;i++)msgs+=msgHtml(arr[i],s.id);
          if(!msgs)msgs='<div class="empty">(暂无消息)</div>';
          h+='<div class="msgs">'+msgs+'</div>';
          h+='<div class="toolbar">'
            +'<select class="mdl" data-id="'+esc(s.id)+'" onchange="modelChange(this)">'
            +'<option value="">&#9881; 切换模型&#8230;</option>'+MODEL_OPTS+'</select>'
            +'</div>';
          h+='<div class="composer">'
            +'<div class="acmenu"></div>'
            +'<input type="text" placeholder="回复&#8230; 输入 / 出命令" data-id="'+esc(s.id)+'"'
            +' oninput="onType(this)" onblur="hideAc(this)"'
            // isComposing/229 守卫：拼音输入法按 Enter 确认候选词时 key 也是 Enter，
            // 不挡住会把打到一半的内容提前发出去（中文用户必踩）
            +' onkeydown="if(event.key===&quot;Enter&quot;&amp;&amp;!event.isComposing&amp;&amp;event.keyCode!==229)sendMsg(this)">'
            +'<button onclick="sendMsg(this.parentElement.querySelector(&quot;input&quot;))">发送</button>'
            +'</div>';
          h+='<div class="sendmsg" id="sm-'+esc(s.id)+'"></div>';
          node.innerHTML=h;
        }
        // ── 增量渲染：Map sid→{node,sig}，sig 不变跳过、变了只重建该卡 ──
        var cardMap=new Map(),lastSessions=[];
        function cardBusy(node){
          // 细粒度 busy-guard：该卡内 input/select 有焦点或有未发送文字 → 本次不动它
          var a=document.activeElement;
          if(a&&node.contains(a)&&(a.tagName==='INPUT'||a.tagName==='SELECT'))return true;
          var ins=node.querySelectorAll('.composer input');
          for(var i=0;i<ins.length;i++)if(ins[i].value)return true;
          return false;
        }
        function renderSessions(sessions){
          lastSessions=sessions;
          var list=document.getElementById('list');
          var shown=[];
          for(var i=0;i<sessions.length;i++)
            if(passFilter(sessions[i]))shown.push(sessions[i]);
          var multi=shown.length>1&&!!shown[0].featured;  // 选中模式多卡 → grid 并行
          document.querySelector('.wrap').classList.toggle('wide',multi);
          list.classList.toggle('grid',multi);
          if(shown.length===0){
            // 清场同样要尊重 busy-guard：正在输入的卡（含未发送草稿、可能正聚焦）
            // 不许删 —— 电脑端批准导致 phase 变化掉出过滤时，草稿会随卡一起丢
            var deadE=[];
            cardMap.forEach(function(v,k){if(!cardBusy(v.node))deadE.push(k);});
            for(var i=0;i<deadE.length;i++){
              cardMap.get(deadE[i]).node.remove();
              cardMap.delete(deadE[i]);
            }
            var e=document.getElementById('emptyMsg');
            if(cardMap.size===0){
              if(!e){
                e=document.createElement('div');
                e.id='emptyMsg';e.className='empty';
                list.appendChild(e);
              }
              e.textContent=sessions.length===0?'暂无会话':'当前过滤条件下没有会话';
            }else if(e){e.remove();}
            return;
          }
          var e0=document.getElementById('emptyMsg');
          if(e0)e0.remove();
          var seen={},prev=null;
          for(var i=0;i<shown.length;i++){
            var s=shown[i];
            seen[s.id]=1;
            var entry=cardMap.get(s.id);
            if(!entry){
              entry={node:document.createElement('div'),sig:''};
              entry.node.setAttribute('data-sid',s.id);  // approve 等从卡上取会话 id
              cardMap.set(s.id,entry);
            }
            // sig 必须覆盖卡上**所有**可见字段：title 来自会话元数据，可在 transcript
            // 不落盘（updatedMs 不变）的情况下单独变化；perm/question 取内容哈希而非有无
            var sig=s.updatedMs+':'+(s.messages?s.messages.length:0)+':'+s.phase
                   +':'+(s.featured?1:0)+':'+(s.tokens||0)
                   +':'+h32(String(s.title||'')+'|'+(s.entry||''))
                   +':'+(s.permission?h32(String(s.permission.tool||'')+'|'+(s.permission.desc||'')):0)
                   +':'+(s.question?h32(String(s.question)):0);
            var busy=cardBusy(entry.node);
            // sig 没变跳过；busy 时不重建也不记 sig，下次空闲再补
            if(sig!==entry.sig&&!busy){
              buildCard(entry.node,s);
              entry.sig=sig;
            }
            // insertBefore 维持服务器顺序；busy 卡不挪位（DOM move 会让输入框失焦）
            if(entry.node.parentNode!==list){
              list.insertBefore(entry.node,prev?prev.nextSibling:list.firstChild);
            }else if(!busy){
              var want=prev?prev.nextSibling:list.firstChild;
              if(want!==entry.node)list.insertBefore(entry.node,want);
            }
            prev=entry.node;
          }
          // 服务器列表里消失的卡 → 移除；busy 的卡留到输入框清空/失焦后的渲染再删
          var dead=[];
          cardMap.forEach(function(v,k){if(!seen[k]&&!cardBusy(v.node))dead.push(k);});
          for(var i=0;i<dead.length;i++){
            cardMap.get(dead[i]).node.remove();
            cardMap.delete(dead[i]);
          }
        }
        // ── SSE 推送 + 150ms 去抖拉取；断线降级 5s 轮询 ──
        var pollTimer=null,refreshTimer=null;
        function scheduleRefresh(){
          if(refreshTimer)clearTimeout(refreshTimer);
          refreshTimer=setTimeout(refresh,150);
        }
        function startSse(){
          try{
            var es=new EventSource('/api/events');
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
              // EventSource 会自动重连；重连期间靠轮询兜底，onopen 时清掉
              if(!pollTimer)pollTimer=setInterval(refresh,5000);
            };
          }catch(e){
            pollTimer=setInterval(refresh,5000);
          }
        }
        var fetchSeq=0;  // SSE 去抖/轮询/focus 三个入口并发拉取，乱序返回时旧数据要丢弃
        async function refresh(){
          var my=++fetchSeq;
          try{
            var r=await fetch('/api/sessions');
            var data=await r.json();
            if(my!==fetchSeq)return; // 已有更新的请求发出 —— 这份是旧数据，不许覆盖渲染
            if(data.stats)applyStats(data.stats);
            renderSessions(data.sessions||[]);
          }catch(e){/* 服务端重启 / 网络抖动 —— 下次推送或轮询再试 */}
        }
        // ── 审批：POST /api/approve {id,digit}；等待中禁用，ok 后换成已发送 ──
        function approve(btn){
          var card=btn.closest('.card');
          if(!card)return;
          var id=card.getAttribute('data-sid');
          var digit=btn.getAttribute('data-d');
          var box=btn.parentElement;
          var bs=box.querySelectorAll('button');
          for(var i=0;i<bs.length;i++)bs[i].disabled=true;
          fetch('/api/approve',{method:'POST',
            headers:{'Content-Type':'application/json'},
            body:JSON.stringify({id:id,digit:digit})})
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
        // ── 发送：POST /api/send，成功后乐观回显一个半透明 user 气泡 ──
        async function sendText(id,text,inputToClear){
          var sm=document.getElementById('sm-'+id);
          if(sm){sm.textContent='发送中…';sm.className='sendmsg';}
          try{
            var r=await fetch('/api/send',{method:'POST',
              headers:{'Content-Type':'application/json'},
              body:JSON.stringify({id:id,text:text})});
            var res=await r.json();
            if(res.ok){
              if(inputToClear)inputToClear.value='';
              if(sm)sm.innerHTML='已发送 &#10003;';
              // 乐观回显：真实数据要等注入+transcript 落盘，先给用户一个气泡
              var entry=cardMap.get(id);
              if(entry){
                var box=entry.node.querySelector('.msgs');
                if(box){
                  var d=document.createElement('div');
                  d.className='msg user ghost';
                  d.textContent=text;
                  box.appendChild(d);
                }
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
        function sendMsg(input){
          var text=input.value.trim();
          if(!text)return;
          sendText(input.getAttribute('data-id'),text,input);
        }
        // ── 启动 ──
        applyTheme();
        updateSoundBtn();
        refresh();     // 首渲染不等模型表 —— loadModels 成功后会自己清 sig 重渲染补下拉
        loadModels();
        startSse();
        </script>
        </body>
        </html>
        """;
}
