using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services.Ai;

/// <summary>
/// 中继长连接的落地端点。
///
/// 语义很简单：你电脑上的 <c>KidPlan.Relay</c> 拿令牌连上来，
/// 服务器之后就可以把 HTTP 请求顺着这条连接发下去，由中继在本地发起真实请求。
/// 云服务器永远不需要能访问你家网络，这是整个方案能成立的关键。
/// </summary>
public static class RelayEndpoint
{
    public const string Path = "/api/relay/connect";

    /// <summary>协议版本。中继与服务器对不上时，中继会提示升级，而不是行为诡异。</summary>
    public const string ProtocolVersion = "1";

    public static void MapRelayEndpoint(this WebApplication app)
    {
        app.Map(Path, async (
            HttpContext context,
            IRelayNodeService nodes,
            RelayConnectionRegistry registry,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("KidPlan.Relay");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("这个地址只接受 WebSocket 连接（KidPlan.Relay 会自动用它）。", ct);
                return;
            }

            var token = context.Request.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(token))
            {
                token = ExtractBearer(context.Request.Headers.Authorization.ToString());
            }

            var node = await nodes.AuthenticateAsync(token, ct);
            if (node is null)
            {
                logger.LogWarning("有中继用无效或已停用的令牌尝试连接。");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("令牌无效或已停用。请在「模型设置 → 本地中继」里重新生成。", ct);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            var connection = new RelayConnection(node.Id, node.OwnerId, node.Name, socket, logger);
            registry.Register(connection);

            try
            {
                await connection.SendRawAsync(JsonSerializer.Serialize(new RelayMessage
                {
                    Type = "server-hello",
                    NodeId = node.Id,
                    NodeName = node.Name,
                    Message = ProtocolVersion,
                }, AiJson.Compact), ct);

                await PumpAsync(context, connection, nodes, dbFactory, logger, ct);
            }
            catch (OperationCanceledException)
            {
                // 客户端断开 / 服务器关闭，正常路径。
            }
            catch (WebSocketException ex)
            {
                logger.LogInformation("中继节点 {NodeId} 的连接中断：{Message}", node.Id, ex.Message);
            }
            finally
            {
                registry.Unregister(node.Id, connection);
                connection.Dispose();
            }
        });
    }

    private static async Task PumpAsync(
        HttpContext context,
        RelayConnection connection,
        IRelayNodeService nodes,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();

        // 心跳落库要节流：中继每 20 秒一次心跳，每次都写 MySQL 太浪费。
        var lastPersisted = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested && context.WebSockets.IsWebSocketRequest)
        {
            builder.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await connection.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.CloseOutputAsync(ct);
                    return;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            connection.LastSeenAt = DateTimeOffset.Now;

            RelayMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<RelayMessage>(builder.ToString(), AiJson.Compact);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "中继节点 {NodeId} 发来的报文无法解析。", connection.NodeId);
                continue;
            }

            if (message is null)
            {
                continue;
            }

            switch (message.Type)
            {
                case "hello":
                    connection.MachineName = message.MachineName;
                    connection.AgentVersion = message.AgentVersion;

                    if (!string.Equals(message.Message, ProtocolVersion, StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            "中继节点 {NodeId} 的协议版本是 {Remote}，服务器是 {Local}，建议升级中继。",
                            connection.NodeId, message.Message, ProtocolVersion);
                    }

                    await PersistAsync();
                    logger.LogInformation(
                        "中继节点 {NodeId}（{NodeName}）上报：机器 {Machine}，版本 {Version}。",
                        connection.NodeId, connection.NodeName, message.MachineName, message.AgentVersion);
                    break;

                case "heartbeat":
                    if (DateTimeOffset.Now - lastPersisted > TimeSpan.FromSeconds(60))
                    {
                        await PersistAsync();
                    }

                    break;

                case "http-result":
                    if (message.Id is { } resultId)
                    {
                        connection.Complete(resultId, new AiHttpResponse(
                            message.Status ?? 0,
                            message.Body ?? string.Empty,
                            message.Status is null or 0 ? message.Message ?? "中继没有拿到响应。" : null));
                    }

                    break;

                default:
                    logger.LogDebug("中继节点 {NodeId} 发来未识别的报文类型 {Type}。", connection.NodeId, message.Type);
                    break;
            }

            continue;

            async Task PersistAsync()
            {
                lastPersisted = DateTimeOffset.Now;

                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var entity = await db.RelayNodes.FirstOrDefaultAsync(n => n.Id == connection.NodeId, ct);
                    if (entity is null)
                    {
                        return;
                    }

                    entity.LastSeenAt = DateTimeOffset.Now;
                    entity.MachineName = Truncate(connection.MachineName, 100);
                    entity.AgentVersion = Truncate(connection.AgentVersion, 30);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    // 心跳写库失败不能把连接搞断，否则数据库抖一下中继就全掉线了。
                    logger.LogWarning(ex, "更新中继节点 {NodeId} 的心跳失败。", connection.NodeId);
                }
            }
        }
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static string ExtractBearer(string? header)
        => header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : string.Empty;
}
