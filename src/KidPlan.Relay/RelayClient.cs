using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace KidPlan.Relay;

/// <summary>
/// 中继的主循环：连上服务器 → 收请求 → 本地发出去 → 回结果。
///
/// 几个刻意的设计：
/// ① 断线永远重连，指数退避但有上限，这样服务器重启 / 网络抖动都能自己恢复；
/// ② 收和发用两把锁分开：转发一个慢请求（本地大模型动辄几十秒）时，
///    心跳和别的请求不能被卡住；
/// ③ 任何异常都不让进程退出——这是要挂在家里电脑上长期跑的东西，
///    崩一次家长不一定知道，而「不工作」比「报错」更难排查。
/// </summary>
public sealed class RelayClient
{
    private readonly RelayOptions _options;
    private readonly LocalForwarder _forwarder;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public RelayClient(RelayOptions options, LocalForwarder forwarder, Action<string> log)
    {
        _options = options;
        _forwarder = forwarder;
        _log = log;
    }

    /// <summary>一直跑，直到进程被杀。返回的 Task 不会正常完成。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(ct);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;

                // 1s、2s、4s…最多 60s。开局退避短一点，服务器重启后能很快连回来；
                // 上限 60s 是为了不让日志被刷爆，同时又不会长时间掉线。
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

                _log($"连接中断：{Describe(ex)}　{delay.TotalSeconds:0} 秒后重连（第 {attempt} 次）");

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        var builder = new UriBuilder(_options.ServerUri)
        {
            Query = "token=" + Uri.EscapeDataString(_options.Token),
        };

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _log($"正在连接 {builder.Uri.GetLeftPart(UriPartial.Path)} …");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(builder.Uri, connectCts.Token);
        }

        _log($"已连接（本机 {Environment.MachineName}，中继版本 {Program.Version}）。");

        await SendAsync(socket, new RelayMessage
        {
            Type = "hello",
            MachineName = Environment.MachineName,
            AgentVersion = Program.Version,
            Message = RelayProtocol.Version,
        }, ct);

        // 心跳单独一条任务：主循环在 await 一个慢请求时，心跳不能跟着停。
        // 服务器靠连接活着 + 心跳来显示「在线」，停了就会被判断成掉线。
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(socket, heartbeatCts.Token);

        try
        {
            var buffer = new byte[64 * 1024];
            var pending = new List<Task>();

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReceiveTextAsync(socket, buffer, ct);
                if (text is null)
                {
                    _log("服务器关闭了连接。");
                    break;
                }

                RelayMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<RelayMessage>(text, RelayProtocol.Json);
                }
                catch (JsonException ex)
                {
                    _log($"收到无法解析的报文（{ex.Message}），已忽略。");
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                switch (message.Type)
                {
                    case "server-hello":
                        if (!string.Equals(message.Message, RelayProtocol.Version, StringComparison.Ordinal))
                        {
                            _log($"⚠️ 服务器协议版本是 {message.Message}，本中继是 {RelayProtocol.Version}，"
                                + "行为可能不一致，建议更新中继程序。");
                        }

                        _log($"服务器已确认节点「{message.NodeName}」（Id {message.NodeId}）。");
                        break;

                    case "http":
                        // 不 await：并发处理，且慢请求不能挡住后面报文的收取。
                        pending.Add(HandleRequestAsync(socket, message, ct));
                        pending.RemoveAll(t => t.IsCompleted);
                        break;

                    default:
                        _log($"收到未识别的报文类型 {message.Type}，已忽略。");
                        break;
                }
            }

            // 退出前把在飞的请求收干净，免得进程退出时日志停在半句话上。
            if (pending.Count > 0)
            {
                await Task.WhenAll(pending);
            }
        }
        finally
        {
            heartbeatCts.Cancel();

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // 正常收尾。
            }
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatSeconds, 5, 300));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            if (!await TrySendAsync(socket, new RelayMessage { Type = "heartbeat" }, ct))
            {
                return;
            }
        }
    }

    private async Task HandleRequestAsync(ClientWebSocket socket, RelayMessage message, CancellationToken ct)
    {
        if (message.Id is not { } id)
        {
            _log("收到没有 id 的 http 报文，已忽略。");
            return;
        }

        ForwardResult result;

        try
        {
            result = await _forwarder.ForwardAsync(message, ct);
        }
        catch (Exception ex)
        {
            result = ForwardResult.Fail($"中继内部错误：{ex.Message}");
        }

        await TrySendAsync(socket, new RelayMessage
        {
            Type = "http-result",
            Id = id,
            Status = result.Status,
            Body = result.Body,
            Message = result.Error,
        }, ct);
    }

    // ------------------------------------------------------------------
    // 收发原语
    // ------------------------------------------------------------------

    private Task SendAsync(ClientWebSocket socket, RelayMessage message, CancellationToken ct)
        => socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, RelayProtocol.Json)),
            WebSocketMessageType.Text,
            true,
            ct);

    /// <summary>发送前拿锁：多条任务（心跳 + 多个响应）同时写一个 WebSocket 是非法的。</summary>
    private async Task<bool> TrySendAsync(ClientWebSocket socket, RelayMessage message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await SendAsync(socket, message, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log($"发送报文失败：{Describe(ex)}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>收一条完整文本消息。连接关闭返回 null。</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var builder = new StringBuilder();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException => ex.Message,
        // 401 会以「服务器返回 401」这种形式出现，翻译成家长看得懂的话。
        _ when ex.Message.Contains("401", StringComparison.Ordinal) =>
            "令牌无效或已停用，请到网页的「本机中继」里重新生成一个",
        _ => ex.Message,
    };
}
