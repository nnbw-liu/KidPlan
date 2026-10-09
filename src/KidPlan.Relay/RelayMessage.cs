using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidPlan.Relay;

/// <summary>
/// 中继与服务器之间的报文。
///
/// 这份定义必须和服务器端的 <c>KidPlan.Web.Services.Ai.RelayMessage</c> 保持一致。
/// 刻意复制一份而不是共享类型：中继要能独立编译、独立发布，
/// 不该为了一个 DTO 去引用整个 Web 项目。
/// </summary>
public sealed class RelayMessage
{
    public string Type { get; set; } = string.Empty;

    /// <summary>请求标识，客户端回 <c>http-result</c> 时要原样带回。</summary>
    public string? Id { get; set; }

    public string? Method { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Body { get; set; }

    public int? Status { get; set; }

    public string? Message { get; set; }

    public string? MachineName { get; set; }

    public string? AgentVersion { get; set; }

    public int? NodeId { get; set; }

    public string? NodeName { get; set; }
}

/// <summary>协议常量。版本对不上时两端都会明确提示，而不是行为诡异。</summary>
public static class RelayProtocol
{
    /// <summary>必须和服务器端 <c>RelayEndpoint.Path</c> 一致。</summary>
    public const string ConnectPath = "/api/relay/connect";

    /// <summary>必须和服务器端 <c>RelayEndpoint.ProtocolVersion</c> 一致。</summary>
    public const string Version = "1";

    /// <summary>两端共用的 JSON 设置。压缩输出即可——没人在意 WebSocket 帧好不好看。</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
