using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 被"置顶同步"的会话 Id（点灵动岛卡片状态圆点设置，再点取消）。网页上该会话
    /// 排第一、带 ⭐ 标识、携带更长的历史（60 条 vs 普通 12 条）。null = 无置顶。
    /// 读写都是引用赋值（原子），无需加锁。
    /// </summary>
    public string? FeaturedSessionId { get; set; }

    public WebSyncService(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>开始监听。端口被占用等失败会抛异常，调用方负责把消息呈现给用户。</summary>
    public void Start()
    {
        if (IsRunning) return;

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start(); // 首次监听 0.0.0.0 可能触发防火墙"允许访问"弹窗（见类注释）
        var cts = new CancellationTokenSource();
        _listener = listener;
        _cts = cts;
        IsRunning = true;

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
            // 非 Stop() 路径的异常退出：同步 IsRunning，让下次 Start() 能重新拉起监听
            //（否则状态卡 true，必须手动先关再开才能恢复）。
            if (!cts.Token.IsCancellationRequested) IsRunning = false;
        });
    }

    /// <summary>停止监听并断开所有处理中的连接。重复调用安全。</summary>
    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts = null;
        _listener = null;
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
    /// </summary>
    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
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
                else if (method == "POST" && path == "/api/send")
                    WriteResponse(stream, "200 OK", "application/json; charset=utf-8", HandleSend(body));
                else
                    WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not Found");
            }
        }
        catch
        {
            // 客户端中断 / 写失败 —— 静默关连接即可
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
            return JsonSerializer.Serialize(new { ok = result.Ok, reason = result.Reason ?? "" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, reason = ex.Message });
        }
    }

    /// <summary>写一个完整的 HTTP/1.1 响应（Content-Length 必须是 UTF8 字节数，不是字符数）。</summary>
    private static void WriteResponse(NetworkStream stream, string status, string contentType, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
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
    /// /api/sessions：Claude 会话（有 transcript 的）按转录文件 mtime 降序取 8 条，
    /// 每条带最近 12 条对话气泡。所有数据从 transcript 现读（ReadTail 只读尾部 256KB），
    /// 不维护任何缓存 —— 2.5s 轮询 × 8 文件 × 256KB 的 I/O 对桌面机可忽略。
    /// </summary>
    private string BuildSessionsJson()
    {
        try
        {
            var featuredId = FeaturedSessionId; // 快照一次，整个构建过程用同一个值
            var items = _sessionManager.GetAllSessions()
                .Where(s => s.Tool == AgentTool.ClaudeCode
                            && !string.IsNullOrEmpty(s.ClaudeMetadata?.TranscriptPath))
                .Select(s => new { Session = s, Path = s.ClaudeMetadata!.TranscriptPath! })
                .Select(x => new { x.Session, x.Path, Mtime = File.GetLastWriteTimeUtc(x.Path) })
                .OrderByDescending(x => x.Session.Id == featuredId) // 置顶会话排第一
                .ThenByDescending(x => x.Mtime)
                .Take(8)
                .Select(x => new
                {
                    id = x.Session.Id,
                    title = x.Session.Title,
                    phase = x.Session.Phase.ToString(),
                    entry = x.Session.ClaudeMetadata?.Entrypoint ?? "",
                    updated = x.Mtime.ToLocalTime().ToString("HH:mm:ss"),
                    featured = x.Session.Id == featuredId,
                    // 置顶会话带更长历史（60 条），普通会话 12 条
                    messages = ReadTail(x.Path, x.Session.Id == featuredId ? 60 : 12)
                })
                .ToList();
            return JsonSerializer.Serialize(items);
        }
        catch
        {
            return "[]";
        }
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
    /// 内嵌的同步页面：深色卡片流，2.5s 轮询 /api/sessions。
    /// esc() 对所有动态文本做 HTML 转义 —— transcript 内容不可信（可能含尖括号代码）。
    /// </summary>
    private const string IndexHtml = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Open Island · Web Sync</title>
        <style>
          body { margin:0; padding:16px; background:#0c0c0e; color:#e7e7ea;
                 font-family:-apple-system,'Segoe UI',Roboto,sans-serif; }
          .wrap { max-width:760px; margin:0 auto; }
          h1 { font-size:18px; margin:0 0 2px 0; }
          .sub { font-size:12px; color:#8a8a90; margin-bottom:14px; }
          .card { background:#1c1c1f; border:1px solid #2a2a2e; border-radius:14px;
                  padding:12px 14px; margin-bottom:12px; }
          .card.featured { border-color:#CC785C; }
          .head { display:flex; align-items:center; gap:8px; margin-bottom:8px; }
          .dot { width:9px; height:9px; border-radius:50%; flex:none; }
          .title { font-size:14px; font-weight:600; overflow:hidden;
                   text-overflow:ellipsis; white-space:nowrap; }
          .badge { font-size:10px; color:#9aa0a6; background:#26262b;
                   border-radius:8px; padding:1px 7px; flex:none; }
          .star { font-size:10px; color:#CC785C; background:#2e2118;
                  border-radius:8px; padding:1px 7px; flex:none; }
          .time { font-size:11px; color:#6e6e74; margin-left:auto; flex:none; }
          .msgs { display:flex; flex-direction:column; gap:6px; }
          .msg { max-width:86%; padding:7px 10px; border-radius:11px; font-size:13px;
                 white-space:pre-wrap; word-break:break-word; line-height:1.45; }
          .user { align-self:flex-end; background:#3a2a1f; }
          .assistant { align-self:flex-start; background:#26262b; }
          .empty { color:#6e6e74; font-size:12px; }
          .composer { display:flex; gap:8px; margin-top:10px; }
          .composer input { flex:1; background:#101013; color:#e7e7ea; font-size:14px;
                  border:1px solid #2a2a2e; border-radius:10px; padding:9px 12px; outline:none; }
          .composer input:focus { border-color:#CC785C; }
          .composer button { background:#CC785C; color:#fff; border:none; border-radius:10px;
                  padding:9px 16px; font-size:13px; flex:none; }
          .composer button:disabled { opacity:.5; }
          .sendmsg { font-size:11px; color:#9aa0a6; margin-top:5px; min-height:14px; }
          .sendmsg.err { color:#e07a6a; }
        </style>
        </head>
        <body>
        <div class="wrap">
          <h1>Open Island · Web Sync</h1>
          <div class="sub">自动刷新中，可直接回复对话 · auto-refreshing, reply inline</div>
          <div id="list"><div class="empty">Loading…</div></div>
        </div>
        <script>
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;')
          .replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function dotColor(p){
          if(p==='Running')return '#2196F3';
          if(p==='WaitingForApproval'||p==='WaitingForAnswer')return '#FF9800';
          if(p==='Completed')return '#9E9E9E';
          return '#4CAF50';
        }
        // 输入框聚焦/有内容时跳过整页重绘，避免打字被轮询打断
        function anyComposerBusy(){
          const a=document.activeElement;
          if(a&&a.tagName==='INPUT')return true;
          for(const i of document.querySelectorAll('.composer input'))
            if(i.value)return true;
          return false;
        }
        async function refresh(){
          if(anyComposerBusy())return;
          try{
            const r=await fetch('/api/sessions');
            const data=await r.json();
            let html='';
            for(const s of data){
              let msgs='';
              for(const m of s.messages){
                msgs+='<div class="msg '+(m.role==='user'?'user':'assistant')+'">'
                     +esc(m.text)+'</div>';
              }
              if(!msgs)msgs='<div class="empty">(no messages)</div>';
              html+='<div class="card'+(s.featured?' featured':'')+'">'
                +'<div class="head">'
                +'<div class="dot" style="background:'+dotColor(s.phase)+'"></div>'
                +'<div class="title">'+esc(s.title)+'</div>'
                +(s.featured?'<div class="star">&#9733; synced</div>':'')
                +(s.entry?'<div class="badge">'+esc(s.entry)+'</div>':'')
                +'<div class="time">'+esc(s.updated)+'</div>'
                +'</div>'
                +'<div class="msgs">'+msgs+'</div>'
                +'<div class="composer">'
                +'<input type="text" placeholder="回复… / Reply…" data-id="'+esc(s.id)+'"'
                +' onkeydown="if(event.key===&quot;Enter&quot;)sendMsg(this)">'
                +'<button onclick="sendMsg(this.previousElementSibling)">发送</button>'
                +'</div>'
                +'<div class="sendmsg" id="sm-'+esc(s.id)+'"></div>'
                +'</div>';
            }
            document.getElementById('list').innerHTML=html
              ||'<div class="empty">No sessions</div>';
          }catch(e){/* 服务端刚关 / 网络抖动 —— 下个 tick 再试 */}
        }
        async function sendMsg(input){
          const id=input.getAttribute('data-id');
          const text=input.value.trim();
          if(!text)return;
          const btn=input.parentElement.querySelector('button');
          const sm=document.getElementById('sm-'+id);
          btn.disabled=true; if(sm){sm.textContent='发送中… sending';sm.className='sendmsg';}
          try{
            const r=await fetch('/api/send',{method:'POST',
              headers:{'Content-Type':'application/json'},
              body:JSON.stringify({id:id,text:text})});
            const res=await r.json();
            if(res.ok){
              input.value='';
              if(sm)sm.textContent='已发送 ✓ sent';
              setTimeout(refresh,1200);
            }else{
              if(sm){sm.textContent='失败 failed: '+(res.reason||'');sm.className='sendmsg err';}
            }
          }catch(e){
            if(sm){sm.textContent='网络错误 network error';sm.className='sendmsg err';}
          }
          btn.disabled=false;
        }
        refresh();
        setInterval(refresh,2500);
        </script>
        </body>
        </html>
        """;
}
