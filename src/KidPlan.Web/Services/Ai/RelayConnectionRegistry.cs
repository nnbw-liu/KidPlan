using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>中继与服务器之间的报文。字段精简，因为要过公网。</summary>
public sealed class RelayMessage
{
    /// <summary>http / http-result / hello / heartbeat / server-hello / error</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("agentVersion")]
    public string? AgentVersion { get; set; }

    [JsonPropertyName("nodeId")]
    public int? NodeId { get; set; }

    [JsonPropertyName("nodeName")]
    public string? NodeName { get; set; }
}

/// <summary>
/// 一条已经建立的中继长连接。
/// 服务器通过它把 HTTP 请求发给客户端电脑，等对方把响应送回来。
/// </summary>
public sealed class RelayConnection : IDisposable
{
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AiHttpResponse>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public RelayConnection(int nodeId, string ownerId, string nodeName, WebSocket socket, ILogger logger)
    {
        NodeId = nodeId;
        OwnerId = ownerId;
        NodeName = nodeName;
        _socket = socket;
        _logger = logger;
    }

    public int NodeId { get; }

    public string OwnerId { get; }

    public string NodeName { get; }

    public string? MachineName { get; set; }

    public string? AgentVersion { get; set; }

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;

    /// <summary>发出一个请求并等响应。超时或连接断开都会返回失败响应，不抛异常。</summary>
    public async Task<AiHttpResponse> SendAsync(
        AiHttpRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        var completion = new TaskCompletionSource<AiHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var payload = JsonSerializer.Serialize(new RelayMessage
            {
                Type = "http",
                Id = id,
                Method = request.Method,
                Url = request.Url,
                Headers = new Dictionary<string, string>(request.Headers),
                Body = request.Body,
            }, AiJson.Compact);

            if (!await SendRawAsync(payload, ct))
            {
                return new AiHttpResponse(0, string.Empty, "中继连接已断开。");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await using var registration = timeoutCts.Token.Register(() =>
                completion.TrySetResult(new AiHttpResponse(0, string.Empty,
                    ct.IsCancellationRequested ? "请求已取消。" : "中继等待超时（模型可能还在加载）。")));

            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>收到中继回传的响应。</summary>
    public void Complete(string id, AiHttpResponse response)
    {
        if (_pending.TryGetValue(id, out var completion))
        {
            completion.TrySetResult(response);
        }
    }

    /// <summary>断开时把所有还在等的请求一次性放掉，别让界面一直转圈。</summary>
    public void FailAll(string reason)
    {
        foreach (var (_, completion) in _pending)
        {
            completion.TrySetResult(new AiHttpResponse(0, string.Empty, reason));
        }

        _pending.Clear();
    }

    public async Task<bool> SendRawAsync(string payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向中继节点 {NodeId} 发送报文失败。", NodeId);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct)
        => _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

    public Task CloseOutputAsync(CancellationToken ct)
        => _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);

    /// <summary>
    /// 断开这条连接。停用 / 删除节点、以及同一节点重连时都会走到这里。
    ///
    /// **必须真的动 socket**：早先这里只做了「把还挂着的请求放掉」，
    /// 结果服务端的接收循环还在原来那条 socket 上等着，客户端也一直以为自己连着，
    /// 于是「停用节点」就变成了只改一个数据库字段——界面上提示「连接已经断开」，
    /// 实际上长连接还好好的（这是端到端验证时才发现的，单元测试当时只看了注册表）。
    ///
    /// 用 Abort 而不是温和的 CloseAsync：接收循环会立刻抛 WebSocketException，
    /// 顺着 PumpAsync 的 catch / finally 正常注销；中继那边也会看到连接断了，
    /// 按退避重连时因为节点已停用而拿到 401，从而彻底停下。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        FailAll("中继连接已关闭。");

        try
        {
            _socket.Abort();
        }
        catch (Exception ex)
        {
            // Abort 失败不影响「这条连接已经作废」这件事，注册表里已经没有了。
            _logger.LogDebug(ex, "关闭中继节点 {NodeId} 的连接时出错。", NodeId);
        }

        // 刻意不 Dispose() 这个 SemaphoreSlim：发送循环可能正握着它在路上，
        // 释放它只会把 ObjectDisposedException 抛到别处去。它没有申请过
        // AvailableWaitHandle，不释放也不会留下系统句柄。
    }
}

/// <summary>
/// 在线中继连接的注册表（单例，跨请求共享）。
/// </summary>
public sealed class RelayConnectionRegistry(ILogger<RelayConnectionRegistry> logger)
{
    private readonly ConcurrentDictionary<int, RelayConnection> _connections = new();

    public void Register(RelayConnection connection)
    {
        // 同一个节点重连时把旧的换掉，并让旧连接上还在等的请求立刻失败。
        if (_connections.TryGetValue(connection.NodeId, out var previous))
        {
            previous.FailAll("中继重新连接了。");
            previous.Dispose();
        }

        _connections[connection.NodeId] = connection;
        logger.LogInformation("中继节点 {NodeId}（{NodeName}）已上线。", connection.NodeId, connection.NodeName);
    }

    public void Unregister(int nodeId, RelayConnection connection)
    {
        if (_connections.TryGetValue(nodeId, out var current) && ReferenceEquals(current, connection))
        {
            _connections.TryRemove(nodeId, out _);
        }

        logger.LogInformation("中继节点 {NodeId} 已下线。", nodeId);
    }

    /// <summary>按节点 Id 找在线连接。</summary>
    public RelayConnection? Find(int nodeId)
        => _connections.TryGetValue(nodeId, out var connection) ? connection : null;

    /// <summary>
    /// 找某个账号下可用的连接。指定了节点就用指定的，否则取最近活跃的那个。
    /// </summary>
    public RelayConnection? FindForOwner(string ownerId, int? preferredNodeId = null)
    {
        if (preferredNodeId is { } id)
        {
            var exact = Find(id);
            return exact is not null && exact.OwnerId == ownerId ? exact : null;
        }

        return _connections.Values
            .Where(c => c.OwnerId == ownerId)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefault();
    }

    public IReadOnlyList<RelayConnection> ListForOwner(string ownerId)
        => _connections.Values.Where(c => c.OwnerId == ownerId).ToList();

    public bool IsOnline(int nodeId) => _connections.ContainsKey(nodeId);

    /// <summary>踢掉某个节点的连接（禁用 / 删除节点时调用）。</summary>
    public void Kick(int nodeId)
    {
        if (_connections.TryRemove(nodeId, out var connection))
        {
            connection.FailAll("这个中继已被停用。");
            connection.Dispose();
            logger.LogInformation("已主动断开中继节点 {NodeId}。", nodeId);
        }
    }
}
