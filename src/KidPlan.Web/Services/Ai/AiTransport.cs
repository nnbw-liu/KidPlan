using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>一次要发出去的 HTTP 请求。</summary>
public sealed record AiHttpRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>一次 HTTP 响应。失败也走这里（<see cref="StatusCode"/> 为 0），避免异常穿过整条链路。</summary>
public sealed record AiHttpResponse(int StatusCode, string Body, string? Error = null)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// 把模型请求真正发出去的方式。目前两种：
/// <see cref="DirectHttpTransport"/>（服务端自己发）和
/// <see cref="LocalRelayTransport"/>（顺着客户端电脑挂上来的长连接发）。
/// </summary>
public interface IAiTransport
{
    AiTransport Kind { get; }

    Task<AiHttpResponse> SendAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        string ownerId,
        CancellationToken ct);
}

/// <summary>按端点配置选运输方式。</summary>
public interface IAiTransportRouter
{
    Task<AiHttpResponse> SendAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        string ownerId,
        CancellationToken ct);
}

public sealed class AiTransportRouter(
    DirectHttpTransport direct,
    LocalRelayTransport relay) : IAiTransportRouter
{
    public Task<AiHttpResponse> SendAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        string ownerId,
        CancellationToken ct)
        => provider.Transport == AiTransport.LocalRelay
            ? relay.SendAsync(provider, request, ownerId, ct)
            : direct.SendAsync(provider, request, ownerId, ct);
}
