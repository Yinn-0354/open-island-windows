using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenIsland.Core.Bridge;

/// <summary>
/// Named Pipe桥接客户端 - 供Hooks CLI使用
/// </summary>
[SupportedOSPlatform("windows")]
public class BridgeCommandClient : IAsyncDisposable
{
    private readonly ILogger<BridgeCommandClient>? _logger;
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private readonly TimeSpan _timeout;
    private readonly string _clientId;
    private bool _registerPending = true;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public BridgeCommandClient(TimeSpan? timeout = null, string? pipeName = null, ILogger<BridgeCommandClient>? logger = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        _pipeName = pipeName ?? GetDefaultPipeName();
        _logger = logger;
        _clientId = $"hooks_{Guid.NewGuid():N}"[..12];
    }

    private static string GetDefaultPipeName() => "OpenIsland_Pipe";

    /// <summary>
    /// 连接到桥接服务器（仅打开管道；register 在第一次发送时与正文一起一次性写入，
    /// 规避 .NET 8 NamedPipeClientStream 在同一 PipeStream 上做多次 WriteAsync 时
    /// 第二次写入会以 OperationCanceledException 失败的问题。）
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(ct);
            _logger?.LogDebug("Connected to bridge server on pipe: {PipeName}", _pipeName);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to connect to bridge server");
            return false;
        }
    }

    /// <summary>
    /// 发送一条消息。第一次发送时会把 RegisterClient 与正文消息拼成单次 WriteAsync。
    /// </summary>
    public async Task<bool> SendAsync(BridgeMessage message, CancellationToken ct = default)
    {
        if (_pipe == null || !_pipe.IsConnected)
        {
            return false;
        }

        try
        {
            var sb = new StringBuilder();
            if (_registerPending)
            {
                _registerPending = false;
                var registerMsg = new RegisterClientMessage
                {
                    ClientId = _clientId,
                    ClientType = "hooks"
                };
                sb.Append(BridgeCodec.Encode(registerMsg));
            }
            sb.Append(BridgeCodec.Encode(message));

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await _pipe.WriteAsync(bytes, ct);
            await _pipe.FlushAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send message");
            return false;
        }
    }

    /// <summary>
    /// 发送钩子事件并等待确认
    /// </summary>
    public async Task<AcknowledgedMessage?> SendHookEventAsync(string source, JsonElement eventData, CancellationToken ct = default)
    {
        var message = new ProcessHookMessage
        {
            Source = source,
            EventData = eventData
        };

        if (!await SendAsync(message, ct))
        {
            return null;
        }

        // 等待确认（简化版，实际应实现完整的请求-响应映射）
        return new AcknowledgedMessage { Success = true };
    }

    /// <summary>
    /// 发送 hook 事件后阻塞读 pipe 等 <see cref="HookDirectiveMessage"/> 回包。
    /// 仅用于 PreToolUse 这种"事前权限拦截"路径 —— hook 子进程必须等用户在
    /// Open Island 上点 Allow / Deny 才能继续，否则 Claude 那边 tool 调用挂着。
    ///
    /// sessionId 用于匹配回包：BridgeServer 在路由 HookDirectiveMessage 时按
    /// sessionId 找正在等待的 hook 子进程。同 session 同时只会有一个 PreToolUse
    /// 在飞，所以 sessionId 是单值键。
    ///
    /// 任何错误（pipe 断、超时、解码失败、ct 取消）都返回 null —— 调用方按
    /// fail-open 契约处理（exit 0 不输出 directive，等价于"放行让 Claude 走默认流程"）。
    /// </summary>
    public async Task<JsonElement?> SendHookAndAwaitDirectiveAsync(
        string source, JsonElement eventData, string? sessionId, CancellationToken ct = default)
    {
        var message = new ProcessHookMessage
        {
            Source = source,
            EventData = eventData
        };

        if (!await SendAsync(message, ct))
            return null;

        if (_pipe == null) return null;

        // 读 pipe 直到拿到 HookDirectiveMessage（sessionId 匹配）或被取消
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        while (!ct.IsCancellationRequested && _pipe.IsConnected)
        {
            int n;
            try { n = await _pipe.ReadAsync(buffer, ct); }
            catch { return null; }
            if (n == 0) return null; // EOF / pipe closed

            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            var lines = sb.ToString().Split('\n');
            sb.Clear();
            // 最后一段没收到 \n 的部分留到下一轮
            if (lines.Length > 0 && !sb.ToString().EndsWith("\n", StringComparison.Ordinal))
                sb.Append(lines[^1]);

            for (int i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var msg = BridgeCodec.Decode(line);
                if (msg is HookDirectiveMessage dir)
                {
                    // sessionId 为 null 时不挑剔（兼容 hook 自身没拿到 sessionId 的边界情况）
                    if (sessionId == null || string.Equals(dir.SessionId, sessionId, StringComparison.Ordinal))
                        return dir.Directive;
                }
                // 其他消息（acknowledged 等）忽略，继续等
            }
        }
        return null;
    }

    /// <summary>
    /// 发送钩子事件（fire and forget，失败不抛异常）
    /// </summary>
    public async Task SendHookEventFireAndForgetAsync(string source, JsonElement eventData)
    {
        try
        {
            using var cts = new CancellationTokenSource(_timeout);
            await SendHookEventAsync(source, eventData, cts.Token);
        }
        catch
        {
            // Fail open - 钩子失败不应阻塞代理
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe != null)
        {
            await _pipe.DisposeAsync();
        }
    }
}
