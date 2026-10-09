using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidPlan.Web.Services.Ai;

/// <summary>
/// AI 相关配置 / 报文的 JSON 约定，集中放一处。
///
/// 枚举写成字符串（"transport": "LocalRelay"）是有意为之：
/// <c>config/ai.json</c> 是给人手改的，写数字没人看得懂。
/// </summary>
public static class AiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>紧凑格式，用于走 WebSocket 的报文，省点带宽。</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
