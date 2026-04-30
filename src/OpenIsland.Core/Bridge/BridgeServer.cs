using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenIsland.Core.Bridge;

/// <summary>
/// Named Pipe桥接服务器 - 替代Unix Socket
/// </summary>
[SupportedOSPlatform("windows")]
public class BridgeServer : IAsyncDisposable
{
    private readonly ILogger<BridgeServer>? _logger;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, NamedPipeServerStream> _clients = new();
    private readonly ConcurrentDictionary<string, ClientInfo> _clientInfo = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    // 事件
    public event EventHandler<BridgeMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    public bool IsRunning { get; private set; }

    public BridgeServer(string? pipeName = null, ILogger<BridgeServer>? logger = null)
    {
        _pipeName = pipeName ?? GetDefaultPipeName();
        _logger = logger;
    }

    private static string GetDefaultPipeName()
    {
        // 使用固定名称，避免中文用户名等问题
        return "OpenIsland_Pipe";
    }

    /// <summary>
    /// 启动桥接服务器
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;

        _logger?.LogInformation("Starting BridgeServer on pipe: {PipeName}", _pipeName);
        IsRunning = true;
        _acceptTask = AcceptClientsLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 停止桥接服务器
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _logger?.LogInformation("Stopping BridgeServer");
        _cts.Cancel();

        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); } catch { }
        }
        _clients.Clear();

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }

        IsRunning = false;
    }

    private async Task AcceptClientsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    // Byte mode: 我们自己用 '\n' 分帧消息（见 HandleClientAsync 的拆分逻辑）。
                    // 之前用 Message mode 时，客户端的第二次 WriteAsync 在 .NET 8 NamedPipeClientStream
                    // overlapped-I/O 实现里会异常地标记 CancellationToken 为 cancelled，导致 hook 事件
                    // 永远到不了应用——register 总能过，但 processHook 立刻 OCE。
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _logger?.LogDebug("Waiting for client connection...");
                await pipeServer.WaitForConnectionAsync(ct);

                var clientId = Guid.NewGuid().ToString("N")[..8];
                _clients.TryAdd(clientId, pipeServer);

                _logger?.LogInformation("Client connected: {ClientId}", clientId);

                // 启动客户端处理任务
                _ = HandleClientAsync(clientId, pipeServer, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error accepting client connection");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task HandleClientAsync(string clientId, NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                int bytesRead = await pipe.ReadAsync(buffer, ct);

                if (bytesRead == 0)
                {
                    // 客户端断开
                    break;
                }

                var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(chunk);

                // 处理完整的消息行（以\n分隔）
                var messages = messageBuilder.ToString().Split('\n');
                messageBuilder.Clear();

                // 最后一个元素可能不完整，保留到下一次
                if (!messages[^1].EndsWith('\n') && messages.Length > 0)
                {
                    messageBuilder.Append(messages[^1]);
                }

                for (int i = 0; i < messages.Length - 1; i++)
                {
                    var message = messages[i].Trim();
                    if (!string.IsNullOrEmpty(message))
                    {
                        try { ProcessMessageAsync(clientId, message); }
                        catch (Exception ex) { _logger?.LogError(ex, "ProcessMessage threw"); }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _clientInfo.TryRemove(clientId, out var info);

            _logger?.LogInformation("Client disconnected: {ClientId}", clientId);
            ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(clientId, info?.ClientType));

            try { await pipe.DisposeAsync(); } catch { }
        }
    }

    private void ProcessMessageAsync(string clientId, string jsonMessage)
    {
        _logger?.LogDebug("Received from {ClientId}: {Message}", clientId, jsonMessage[..Math.Min(200, jsonMessage.Length)]);

        var message = BridgeCodec.Decode(jsonMessage);
        if (message == null)
        {
            _logger?.LogWarning("Failed to decode message from {ClientId}", clientId);
            // Fire-and-forget. Awaiting an ack write here can deadlock the read loop
            // when the client (hook subprocess) doesn't drain the inbound pipe — the
            // OS pipe buffer fills, the server's WriteAsync blocks, the read loop
            // never processes subsequent batched messages.
            _ = SendAcknowledgmentAsync(clientId, null, false, "Invalid message format");
            return;
        }

        // 处理注册消息
        if (message is RegisterClientMessage register)
        {
            _clientInfo[clientId] = new ClientInfo(register.ClientId, register.ClientType);
            ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientId, register.ClientType, register.ClientId));
            _ = SendAcknowledgmentAsync(clientId, null, true);
            return;
        }

        MessageReceived?.Invoke(this, new BridgeMessageReceivedEventArgs(clientId, message));
    }

    private async Task SendAcknowledgmentAsync(string clientId, string? requestId, bool success, string? error = null)
    {
        var ack = new AcknowledgedMessage
        {
            RequestId = requestId,
            Success = success,
            Error = error
        };

        await SendToClientAsync(clientId, ack);
    }

    /// <summary>
    /// 发送消息到指定客户端
    /// </summary>
    public async Task<bool> SendToClientAsync(string clientId, BridgeMessage message)
    {
        if (!_clients.TryGetValue(clientId, out var pipe) || !pipe.IsConnected)
        {
            return false;
        }

        try
        {
            var json = BridgeCodec.Encode(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send message to client {ClientId}", clientId);
            return false;
        }
    }

    /// <summary>
    /// 广播消息到所有客户端
    /// </summary>
    public async Task BroadcastAsync(BridgeMessage message, string? excludeClientId = null)
    {
        var tasks = _clients
            .Where(c => c.Key != excludeClientId && c.Value.IsConnected)
            .Select(c => SendToClientAsync(c.Key, message));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 获取已连接的客户端列表
    /// </summary>
    public IReadOnlyCollection<string> GetConnectedClients() => _clients.Keys.ToList();

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}

public record ClientInfo(string ClientId, string ClientType);

public class BridgeMessageReceivedEventArgs : EventArgs
{
    public string ClientId { get; }
    public BridgeMessage Message { get; }

    public BridgeMessageReceivedEventArgs(string clientId, BridgeMessage message)
    {
        ClientId = clientId;
        Message = message;
    }
}

public class ClientConnectedEventArgs : EventArgs
{
    public string ClientId { get; }
    public string ClientType { get; }
    public string? AppClientId { get; }

    public ClientConnectedEventArgs(string clientId, string clientType, string? appClientId)
    {
        ClientId = clientId;
        ClientType = clientType;
        AppClientId = appClientId;
    }
}

public class ClientDisconnectedEventArgs : EventArgs
{
    public string ClientId { get; }
    public string? ClientType { get; }

    public ClientDisconnectedEventArgs(string clientId, string? clientType)
    {
        ClientId = clientId;
        ClientType = clientType;
    }
}
