using System.Net.Http.Headers;
using System.Text;
using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>
/// 服务端直接发起 HTTP 请求。
///
/// 适用于公网接口（DeepSeek / OpenAI / 通义 …），也适用于模型服务和应用在同一台机器、
/// 同一内网的情况。**不适用**于部署在云端而模型在家里电脑上的场景：
/// 那种情况服务端根本路由不到你家的 127.0.0.1，请改用 <see cref="AiTransport.LocalRelay"/>。
/// </summary>
public sealed class DirectHttpTransport(IHttpClientFactory httpClientFactory, ILogger<DirectHttpTransport> logger)
    : IAiTransport
{
    public AiTransport Kind => AiTransport.Direct;

    public async Task<AiHttpResponse> SendAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        string ownerId,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(AiHttpClient.Name);

            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

            if (request.Body is not null)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
            }

            foreach (var (key, value) in request.Headers)
            {
                if (!message.Headers.TryAddWithoutValidation(key, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(key, value);
                }
            }

            using var response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseContentRead,
                ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            return new AiHttpResponse((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "直连模型端点 {Provider}（{Url}）失败。", provider.Name, request.Url);
            return new AiHttpResponse(0, string.Empty, Describe(ex));
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "请求超时（模型可能在加载，或网络不通）。",
        HttpRequestException http => $"连不上或连接被中断：{http.Message}",
        _ => ex.Message,
    };
}

/// <summary>DI 名字与共享的 HttpClient 配置。</summary>
public static class AiHttpClient
{
    public const string Name = "kidplan-ai";

    public static void Configure(IServiceCollection services) => services
        .AddHttpClient(Name)
        .ConfigureHttpClient(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;   // 超时由每端的 TimeoutSeconds 通过 CTS 控制
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KidPlan", "1.0"));
        });

    /// <summary>按端点配置拼出请求头。</summary>
    public static Dictionary<string, string> BuildHeaders(AiProviderConfig provider)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            headers["Authorization"] = $"Bearer {provider.ApiKey.Trim()}";
        }

        foreach (var (key, value) in provider.ExtraHeaders)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                headers[key.Trim()] = value;
            }
        }

        return headers;
    }
}
