namespace KidPlan.Web.Models;

/// <summary>
/// 模型接口的调用方式。
/// </summary>
public enum AiTransport
{
    /// <summary>
    /// 服务端直接连。适用于公网接口（DeepSeek / OpenAI / 通义 等），
    /// 也适用于模型服务和应用跑在同一台机器或同一内网的情况。
    /// </summary>
    Direct = 0,

    /// <summary>
    /// 走本机中继小程序。
    ///
    /// 应用部署在云端时，服务端**永远**连不到你家里的 127.0.0.1，
    /// 这是网络拓扑决定的，不是配置问题。所以改成反向：你电脑上的
    /// <c>KidPlan.Relay</c> 主动连到云端服务器挂一条长连接，
    /// 服务端把模型请求顺着这条连接发下去，由中继在你电脑上本地发起请求。
    ///
    /// 用这种方式的端点，<c>BaseUrl</c> 是「中继那一端看到」的地址
    /// （通常是 http://127.0.0.1:1234/v1），与服务器无关。
    /// </summary>
    LocalRelay = 1,
}

/// <summary>一个模型服务端点（一家厂商 / 一个本地服务）。</summary>
public sealed class AiProviderConfig
{
    /// <summary>稳定标识，用于「默认端点」引用与覆盖合并。改 Id 等于变成另一个端点。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面上显示的名字。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI 兼容的 API 根地址，必须以 <c>/v1</c> 这类版本段结尾，
    /// 应用会拼 <c>/chat/completions</c> 和 <c>/models</c>。
    /// 例如 https://api.deepseek.com/v1 、http://127.0.0.1:1234/v1 。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key。直连公网接口时必填；本地服务一般留空即可。</summary>
    public string? ApiKey { get; set; }

    /// <summary>调用方式，见 <see cref="AiTransport"/>。</summary>
    public AiTransport Transport { get; set; } = AiTransport.Direct;

    /// <summary>
    /// 限定用哪个中继节点（<c>RelayNodes.Id</c>）。留空表示「当前账号下任意一个在线的节点」，
    /// 对只有一个中继的人来说更省事。
    /// </summary>
    public int? RelayNodeId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 可用的模型名。**允许手工输入**，不依赖 /models 接口：
    /// 有些本地服务（或网关）不实现 /models，或者名字和实际部署的名字不一致。
    /// </summary>
    public List<string> Models { get; set; } = [];

    /// <summary>额外的请求头，某些网关需要（如 OpenRouter 的 HTTP-Referer）。</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];

    /// <summary>备注，写给自己看。</summary>
    public string? Note { get; set; }
}

/// <summary>饮食推荐的模型调用参数。</summary>
public sealed class AiRecommendationOptions
{
    /// <summary>前置周期上限（天）。默认 30，需求里写死的就是这个数。</summary>
    public int MaxLookbackDays { get; set; } = 30;

    /// <summary>界面上滑块 / 输入框的初始值。</summary>
    public int DefaultLookbackDays { get; set; } = 7;

    /// <summary>采样温度。推荐要稳定可复现，默认偏低。</summary>
    public double Temperature { get; set; } = 0.4;

    /// <summary>单次请求超时（秒）。本地小模型吐字慢，默认给得比较宽松。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// 期望的最大输出 token 数。默认给 8192：推理模型会先把额度花在思考过程上，
    /// 给 2500 时实测会出现「token 全是 reasoning、content 是空的」，
    /// 一次推荐直接失败（详见 <see cref="Services.Ai.AiCallOptions.Default"/>）。
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;
}

/// <summary>
/// 全局 AI 配置文档。序列化成 <c>config/ai.json</c>，也用于网页上的导入 / 导出。
/// 每账号的覆盖（<see cref="AiUserSettings"/>）用同一批类型表达。
/// </summary>
public sealed class AiSettingsDocument
{
    /// <summary>文档版本，便于以后改结构时做兼容。</summary>
    public int Version { get; set; } = 1;

    /// <summary>默认用哪个端点（<see cref="AiProviderConfig.Id"/>）。</summary>
    public string? DefaultProviderId { get; set; }

    /// <summary>默认用哪个模型名。</summary>
    public string? DefaultModel { get; set; }

    public AiRecommendationOptions Recommendation { get; set; } = new();

    public List<AiProviderConfig> Providers { get; set; } = [];
}

/// <summary>
/// 某个账号对全局 AI 配置的覆盖（存在数据库里）。
/// 只写「和全局不一样的地方」，全局改了以后没被覆盖的部分会跟着变。
/// </summary>
public sealed class AiSettingsOverride
{
    public string? DefaultProviderId { get; set; }

    public string? DefaultModel { get; set; }

    public AiRecommendationOptions? Recommendation { get; set; }

    /// <summary>按 <see cref="AiProviderConfig.Id"/> 覆盖或新增的端点。</summary>
    public List<AiProviderConfig> Providers { get; set; } = [];

    /// <summary>要屏蔽掉的全局端点 Id。</summary>
    public List<string> RemovedProviderIds { get; set; } = [];
}
