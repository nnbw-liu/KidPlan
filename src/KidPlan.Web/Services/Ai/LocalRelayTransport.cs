using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>
/// 通过客户端电脑上的 <c>KidPlan.Relay</c> 发请求。
///
/// 这是「云端服务器 + 家里模型」唯一真正通用的解法，原因值得写清楚：
/// 服务端在云上，你家的模型服务监听 127.0.0.1 或 192.168.x.x，
/// 公网根本路由不过去 —— 这不是配置问题，是网络拓扑决定的。
/// 所以方向反过来：由你电脑上的中继**主动**连到服务器挂一条长连接，
/// 服务器把请求顺着这条连接发下去，中继在你电脑本地发起 HTTP 调用。
///
/// 顺带解决了两件事：
/// 1. 手机在外面用 4G 也能用——手机只跟云端说话，不关心模型在哪；
/// 2. 不用给本地模型服务配 HTTPS 证书，也不受浏览器混合内容 / 跨域限制。
/// </summary>
public sealed class LocalRelayTransport(
    RelayConnectionRegistry registry,
    IAiSettingsStore settings,
    ILogger<LocalRelayTransport> logger) : IAiTransport
{
    public AiTransport Kind => AiTransport.LocalRelay;

    public async Task<AiHttpResponse> SendAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        string ownerId,
        CancellationToken ct)
    {
        var connection = registry.FindForOwner(ownerId, provider.RelayNodeId);

        if (connection is null)
        {
            var hint = registry.ListForOwner(ownerId).Count == 0
                ? "还没有任何中继在线。请在你自己的电脑上启动 KidPlan.Relay。"
                : "指定的中继节点当前不在线。";

            return new AiHttpResponse(0, string.Empty, hint);
        }

        var effective = await settings.GetEffectiveAsync(ownerId, ct);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(effective.Recommendation?.TimeoutSeconds ?? 180, 5, 900));

        logger.LogDebug(
            "经中继节点 {NodeId}（{NodeName}）转发 {Method} {Url}。",
            connection.NodeId,
            connection.NodeName,
            request.Method,
            request.Url);

        return await connection.SendAsync(request, timeout, ct);
    }
}
