using System.Text;
using System.Text.Json;
using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>一条对话消息。</summary>
public sealed record AiChatMessage(string Role, string Content)
{
    public static AiChatMessage System(string content) => new("system", content);

    public static AiChatMessage User(string content) => new("user", content);
}

/// <summary>一次模型调用的采样参数。</summary>
public sealed record AiCallOptions(double Temperature, int MaxOutputTokens, int TimeoutSeconds)
{
    /// <summary>
    /// 默认 8192 而不是一个「够用就好」的小数字，原因是推理模型（DeepSeek-R1 这一类、
    /// 以及很多网关背后的 reasoning 模型）会把 <c>max_tokens</c> 先花在**思考过程**上。
    /// 给 2500 时实测会出现：2500 个 token 全是 reasoning，<c>content</c> 是空字符串，
    /// <c>finish_reason=length</c> —— 一次推荐直接失败，而用户看到的只是「输出不对」。
    /// 上限给宽一点对非推理模型没有副作用（它只是天花板，不会主动写满）。
    /// </summary>
    public static AiCallOptions Default { get; } = new(0.4, 8192, 180);
}

/// <summary>
/// 一次对话补全的结果。失败也走这里，方便界面统一显示。
///
/// <paramref name="RawResponse"/> 特意**不是**原始的 HTTP 报文，而是一份人能看懂的诊断
/// （见 <see cref="OpenAiChatClient.DescribeResponse"/>）。原因是踩过一次：
/// 推理模型把 max_tokens 用光时，响应体里是一整包 <c>choices[0].message.reasoning_content</c>
/// 的英文思考过程 + usage + service_tier，直接铺在页面上就是「推理输出的内容好像不太对」。
/// 原始报文仍然会进日志，排查时去日志里看。
/// </summary>
public sealed record AiChatResult(
    bool Success,
    string Content,
    string RawResponse,
    string? Error = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? FinishReason = null);

/// <summary>
/// OpenAI 兼容的接口客户端。
///
/// 只依赖 <c>POST {base}/chat/completions</c> 和 <c>GET {base}/models</c> 两个约定，
/// 这样 DeepSeek、通义、Kimi、OpenAI 官方，以及 LM Studio / Ollama / vLLM / one-api
/// 这类本地或自建服务都能直接接。
/// </summary>
public interface IAiChatClient
{
    Task<AiChatResult> CompleteAsync(
        AiProviderConfig provider,
        string model,
        IReadOnlyList<AiChatMessage> messages,
        AiCallOptions options,
        string ownerId,
        CancellationToken ct = default);

    /// <summary>拉模型列表。很多本地服务不实现这个接口，所以它只是「方便」，不是必走路径。</summary>
    Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(
        AiProviderConfig provider,
        string ownerId,
        CancellationToken ct = default);

    /// <summary>连通性自检：没给模型名就只拉列表。</summary>
    Task<(bool Ok, string Message)> TestAsync(
        AiProviderConfig provider,
        string? model,
        string ownerId,
        CancellationToken ct = default);
}

public sealed class OpenAiChatClient(
    IAiTransportRouter transport,
    ILogger<OpenAiChatClient> logger) : IAiChatClient
{
    public async Task<AiChatResult> CompleteAsync(
        AiProviderConfig provider,
        string model,
        IReadOnlyList<AiChatMessage> messages,
        AiCallOptions options,
        string ownerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return new AiChatResult(false, string.Empty, string.Empty, "这个端点还没填 API 地址。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new AiChatResult(false, string.Empty, string.Empty, "没有指定模型名。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxOutputTokens,
            // 不开流式：Blazor Server 的交互式渲染下做逐字输出要额外的 SignalR 通道，
            // 而饮食推荐本来就不是「看着它打字」的场景，一次性返回更好做 JSON 解析。
            ["stream"] = false,
        };

        var request = new AiHttpRequest(
            "POST",
            BuildUrl(provider.BaseUrl, "chat/completions"),
            AiHttpClient.BuildHeaders(provider),
            JsonSerializer.Serialize(payload, AiJson.Compact));

        var response = await SendWithTimeoutAsync(provider, request, options.TimeoutSeconds, ownerId, ct);

        if (response.Error is not null)
        {
            return new AiChatResult(false, string.Empty, response.Body, response.Error);
        }

        if (!response.IsSuccess)
        {
            return new AiChatResult(false, string.Empty, response.Body,
                $"接口返回 {response.StatusCode}：{ExtractError(response.Body)}");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;

            var choice = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                    ? choices[0]
                    : default;

            var content = ReadString(choice, "message", "content") ?? string.Empty;

            // 推理模型（DeepSeek-R1 这类）把思考过程放在这里。它不是答案，
            // 但「有没有它」是判断「额度是不是被思考吃光」的关键线索。
            var reasoning = ReadString(choice, "message", "reasoning_content") ?? string.Empty;

            var finishReason = choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("finish_reason", out var finish)
                && finish.ValueKind == JsonValueKind.String
                    ? finish.GetString()
                    : null;

            int? promptTokens = null;
            int? completionTokens = null;
            int? reasoningTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");

                if (usage.TryGetProperty("completion_tokens_details", out var details)
                    && details.ValueKind == JsonValueKind.Object)
                {
                    reasoningTokens = ReadInt(details, "reasoning_tokens");
                }
            }

            var diagnostic = DescribeResponse(content, reasoning, finishReason, options.MaxOutputTokens, reasoningTokens);

            if (content.Length == 0)
            {
                // 原始报文留给日志：页面上的诊断是可读版本，不是让人去读英文思考链。
                logger.LogWarning(
                    "模型返回了空正文。finish_reason={FinishReason}，max_tokens={MaxTokens}，" +
                    "completion_tokens={CompletionTokens}，reasoning_tokens={ReasoningTokens}。原始响应：{Body}",
                    finishReason ?? "(无)",
                    options.MaxOutputTokens,
                    completionTokens,
                    reasoningTokens,
                    Truncate(response.Body, 4000));

                return new AiChatResult(
                    false,
                    string.Empty,
                    diagnostic,
                    EmptyContentError(finishReason, reasoning, options.MaxOutputTokens, completionTokens, reasoningTokens),
                    promptTokens,
                    completionTokens,
                    finishReason);
            }

            if (finishReason == "length")
            {
                logger.LogWarning(
                    "模型输出被 max_tokens={MaxTokens} 截断了（completion_tokens={CompletionTokens}）。",
                    options.MaxOutputTokens,
                    completionTokens);
            }

            return new AiChatResult(true, content, diagnostic, null, promptTokens, completionTokens, finishReason);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "模型返回的不是合法 JSON。");
            return new AiChatResult(false, string.Empty, response.Body,
                $"模型返回的不是合法 JSON（前 200 字）：{Truncate(response.Body, 200)}");
        }
    }

    public async Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(
        AiProviderConfig provider,
        string ownerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return (false, [], "这个端点还没填 API 地址。");
        }

        var request = new AiHttpRequest(
            "GET",
            BuildUrl(provider.BaseUrl, "models"),
            AiHttpClient.BuildHeaders(provider),
            null);

        var response = await SendWithTimeoutAsync(provider, request, 30, ownerId, ct);

        if (response.Error is not null)
        {
            return (false, [], response.Error);
        }

        if (!response.IsSuccess)
        {
            return (false, [], $"接口返回 {response.StatusCode}：{ExtractError(response.Body)}");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return (false, [], "返回里没有 data 数组，这个服务可能不实现 /models，请手工填写模型名。");
            }

            var models = data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (true, models, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "模型列表返回的不是合法 JSON。");
            return (false, [], $"返回的不是合法 JSON：{Truncate(response.Body, 200)}");
        }
    }

    public async Task<(bool Ok, string Message)> TestAsync(
        AiProviderConfig provider,
        string? model,
        string ownerId,
        CancellationToken ct = default)
    {
        var (ok, models, error) = await ListModelsAsync(provider, ownerId, ct);

        var listNote = ok
            ? $"拉到了 {models.Count} 个模型：{string.Join("、", models.Take(5))}{(models.Count > 5 ? " …" : string.Empty)}"
            : $"没能拉到模型列表（{error}）。";

        if (string.IsNullOrWhiteSpace(model))
        {
            return (ok, listNote + (ok ? string.Empty : "可以直接手工填模型名，不影响使用。"));
        }

        var result = await CompleteAsync(
            provider,
            model,
            [AiChatMessage.User("请只回复两个字：可以")],
            new AiCallOptions(0, 32, 60),
            ownerId,
            ct);

        if (!result.Success)
        {
            return (false, $"{listNote} 但试调用模型「{model}」失败：{result.Error}");
        }

        return (true, $"{listNote} 试调用「{model}」成功，回复：{Truncate(result.Content.Trim(), 40)}");
    }

    private async Task<AiHttpResponse> SendWithTimeoutAsync(
        AiProviderConfig provider,
        AiHttpRequest request,
        int timeoutSeconds,
        string ownerId,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 900)));

        try
        {
            return await transport.SendAsync(provider, request, ownerId, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new AiHttpResponse(0, string.Empty, $"请求超过 {timeoutSeconds} 秒还没返回。");
        }
    }

    internal static string BuildUrl(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path}";

    internal static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    /// <summary>按路径读一个字符串字段，任何一段缺失 / 类型不对都返回 null。</summary>
    internal static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>
    /// 空正文到底是因为什么。判据只有能观测到的那几个：finish_reason、有没有 reasoning_content、
    /// token 用量。写成一句可执行的话，而不是把英文思考链甩给用户。
    /// </summary>
    internal static string EmptyContentError(
        string? finishReason,
        string reasoning,
        int maxOutputTokens,
        int? completionTokens,
        int? reasoningTokens)
    {
        var hasReasoning = !string.IsNullOrWhiteSpace(reasoning);
        var used = completionTokens ?? reasoningTokens;
        var usedText = used is { } n ? $"，这次用掉了 {n} 个 token" : string.Empty;
        var reasoningText = reasoningTokens is { } r ? $"，其中 {r} 个是推理" : string.Empty;

        if (finishReason == "length" && hasReasoning)
        {
            return $"模型把输出额度全花在推理上了{usedText}{reasoningText}（finish_reason=length），" +
                   $"没轮到正文就停了。把「最大输出 token」从 {maxOutputTokens} 调大到 8000 以上" +
                   // 不写死「全局配置」：普通账号看不到那个页签，只能改自己的覆盖。
                   "（在「模型设置」里改：管理员改「全局配置」，其他账号在「我的账号覆盖 → 单独设置推荐参数」里改），" +
                   "或者换一个不需要推理的模型，再生成一次。";
        }

        if (finishReason == "length")
        {
            return $"模型达到最大输出 token（{maxOutputTokens}）就停了，正文是空的{usedText}。" +
                   "把「最大输出 token」调大，或者把推荐范围缩小（例如从「单周」改成「单天」）再试。";
        }

        if (hasReasoning)
        {
            return $"模型只返回了推理过程，没有正文（finish_reason={finishReason ?? "未知"}{usedText}{reasoningText}）。" +
                   "把「最大输出 token」调大再试；如果一直这样，说明这个模型不适合这种「只输出 JSON」的任务。";
        }

        return $"模型返回了空内容（finish_reason={finishReason ?? "未知"}{usedText}）。" +
               "可以换一个模型，或者去「模型设置」里点一次「测试连接」确认这个端点和模型名是能用的。";
    }

    /// <summary>
    /// 把响应体整理成一段人能读的诊断，替代原来的「整包报文直接铺在页面上」。
    /// 有正文就给正文（截断），没有正文就给推理过程的前一段 + 关键结论字段。
    /// </summary>
    internal static string DescribeResponse(
        string content,
        string reasoning,
        string? finishReason,
        int maxOutputTokens,
        int? reasoningTokens)
    {
        var builder = new StringBuilder();

        builder.Append("finish_reason=").Append(finishReason ?? "(缺省)");
        builder.Append("；max_tokens=").Append(maxOutputTokens);

        if (reasoningTokens is { } rt)
        {
            builder.Append("；推理用了 ").Append(rt).Append(" 个 token");
        }

        builder.AppendLine();

        if (content.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("正文：");
            builder.Append(Truncate(content, 4000));

            if (reasoning.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine("（这个模型还返回了推理过程，共 ")
                    .Append(reasoning.Length)
                    .Append(" 字，属于模型的思考内容，不是它给你的答案，这里就不铺开了。）");
            }

            return builder.ToString();
        }

        if (reasoning.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("模型这次的输出里全是推理过程，没有正文。推理过程开头是这样的：");
            builder.AppendLine();
            builder.Append(Truncate(reasoning, 600));
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("模型这次没有返回任何正文，响应体里也没有推理过程，只有上面这些字段。");
        }

        return builder.ToString();
    }

    /// <summary>从错误响应里抠出人能看懂的一句话。</summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "没有任何响应内容。";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }
            }

            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
            {
                return topMessage.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON（比如 Nginx 的 502 页面），原样截断显示。
        }

        return Truncate(body.Trim(), 200);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// 让中文在 JSON 里别被转义成 \uXXXX —— 提示词要发给模型，
/// 顺手也让人能直接看懂写进日志的报文。
/// </summary>
internal static class AiJsonEncoding
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
