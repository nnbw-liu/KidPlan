using System.Text.Json;
using KidPlan.Web.Models;
using KidPlan.Web.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidPlan.Tests;

/// <summary>
/// OpenAI 兼容客户端的响应解析。
///
/// 这一组用例来自一次真实的翻车：用户接了一个**推理模型**，默认 2500 的
/// <c>max_tokens</c> 被思考过程吃光，响应里 <c>content</c> 是空字符串、
/// <c>reasoning_content</c> 里是 2500 个 token 的英文自我推理、<c>finish_reason=length</c>。
/// 应用当时把**整包 HTTP 报文**当成「模型的原始输出」铺在页面上，
/// 用户看到一堆英文思考链，反馈是「推理输出的内容好像不太对」。
///
/// 所以这里钉住三件事：
///   1. 空正文时的报错要说清是「额度被推理吃光」并给出可执行的动作；
///   2. 页面上拿到的诊断文本是可读的，不是整包报文（尤其不能把推理链全文铺出来）；
///   3. finish_reason=length 但正文非空时，要能被上层识别成「被截断」。
/// </summary>
public class OpenAiChatClientTests
{
    /// <summary>把一段响应体直接喂回去的运输层，不碰网络。</summary>
    private sealed class StubRouter(string body, int status = 200, string? error = null) : IAiTransportRouter
    {
        public AiHttpRequest? LastRequest { get; private set; }

        public Task<AiHttpResponse> SendAsync(
            AiProviderConfig provider,
            AiHttpRequest request,
            string ownerId,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AiHttpResponse(status, body, error));
        }
    }

    private static readonly AiProviderConfig Provider = new()
    {
        Id = "stub",
        Name = "假端点",
        BaseUrl = "http://127.0.0.1:1234/v1",
        Enabled = true,
    };

    private static Task<AiChatResult> CompleteAsync(
        string body,
        int maxTokens = 2500,
        int status = 200)
    {
        var client = new OpenAiChatClient(new StubRouter(body, status), NullLogger<OpenAiChatClient>.Instance);

        return client.CompleteAsync(
            Provider,
            "reasoning-model",
            [AiChatMessage.User("给我一份菜单")],
            new AiCallOptions(0.4, maxTokens, 30),
            "user-1");
    }

    /// <summary>用户实际遇到的那份响应（字段按真实抓包裁短了推理内容）。</summary>
    private static string ReasoningOnlyResponse(int completionTokens = 2500) => JsonSerializer.Serialize(new
    {
        id = "021791211224221c237340934591b89d6de6b6a9271c1b97fb7ba",
        @object = "chat.completion",
        created = 1791211238L,
        model = "deepseek-v4-1-flash-260910",
        service_tier = "default",
        choices = new object[]
        {
            new
            {
                index = 0,
                finish_reason = "length",
                message = new
                {
                    role = "assistant",
                    content = string.Empty,
                    reasoning_content =
                        "Need to produce JSON only. Need plan for 2026-10-05 full day breakfast lunch dinner. " +
                        "Child 3y4m, allergies milk mild, egg intolerance. Rule 1: 禁用食材无. " +
                        "Need output meals array. Include date 2026-10-05. Need no forbidden.",
                },
            },
        },
        usage = new
        {
            prompt_tokens = 957,
            completion_tokens = completionTokens,
            total_tokens = 957 + completionTokens,
            completion_tokens_details = new { reasoning_tokens = completionTokens },
        },
    });

    // ------------------------------------------------------------------
    // 空正文：额度被推理吃光
    // ------------------------------------------------------------------

    [Fact]
    public async Task Complete_ExplainsThatReasoningAteTheWholeBudget()
    {
        var result = await CompleteAsync(ReasoningOnlyResponse());

        Assert.False(result.Success);
        Assert.Empty(result.Content);
        Assert.Equal("length", result.FinishReason);
        Assert.Equal(2500, result.CompletionTokens);

        // 报错必须点名原因（推理）、给出用量、并告诉用户具体该做什么。
        Assert.Contains("推理", result.Error);
        Assert.Contains("2500", result.Error);
        Assert.Contains("8000", result.Error);
        Assert.Contains("最大输出 token", result.Error);
        // 而且这个「去做」对普通账号也要成立：全局配置那个页签只有管理员看得到，
        // 普通人能改的入口是「我的账号覆盖」。
        Assert.Contains("我的账号覆盖", result.Error);
        Assert.Contains("全局配置", result.Error);
    }

    [Fact]
    public async Task Complete_KeepsTheDiagnosticReadableInsteadOfDumpingTheEnvelope()
    {
        var result = await CompleteAsync(ReasoningOnlyResponse());

        // 可读诊断：关键字段 + 一小段推理开头。
        Assert.Contains("finish_reason=length", result.RawResponse);
        Assert.Contains("max_tokens=2500", result.RawResponse);
        Assert.Contains("全是推理过程，没有正文", result.RawResponse);
        Assert.Contains("Need to produce JSON only", result.RawResponse);

        // 不能把整包 HTTP 报文铺出去：这些信封字段对用户没有任何意义。
        Assert.DoesNotContain("service_tier", result.RawResponse);
        Assert.DoesNotContain("completion_tokens_details", result.RawResponse);
        Assert.DoesNotContain("chat.completion", result.RawResponse);

        // 推理链再长，页面上的诊断也要有界（这里给 5000 字，取前 600 字）。
        var longReasoning = await CompleteAsync(JsonSerializer.Serialize(new
        {
            choices = new object[]
            {
                new
                {
                    finish_reason = "length",
                    message = new { content = string.Empty, reasoning_content = new string('思', 5000) },
                },
            },
        }));

        Assert.True(
            longReasoning.RawResponse.Length < 1200,
            $"诊断文本应当有界，实际 {longReasoning.RawResponse.Length} 字。");
    }

    [Fact]
    public async Task Complete_SaysTheModelStoppedAtTheTokenCeilingWhenThereIsNoReasoning()
    {
        var body = """
            {"choices":[{"finish_reason":"length","message":{"role":"assistant","content":""}}],
             "usage":{"prompt_tokens":10,"completion_tokens":300}}
            """;

        var result = await CompleteAsync(body, maxTokens: 300);

        Assert.False(result.Success);
        Assert.Contains("最大输出 token", result.Error);
        Assert.Contains("300", result.Error);
        Assert.DoesNotContain("推理上", result.Error);
    }

    [Fact]
    public async Task Complete_StillGivesAUsableMessageWhenTheResponseIsJustEmpty()
    {
        var body = """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":""}}]}""";

        var result = await CompleteAsync(body);

        Assert.False(result.Success);
        Assert.Contains("空内容", result.Error);
        Assert.Contains("finish_reason=stop", result.Error);
        Assert.Contains("没有返回任何正文", result.RawResponse);
    }

    // ------------------------------------------------------------------
    // 有正文
    // ------------------------------------------------------------------

    [Fact]
    public async Task Complete_ReturnsContentAndMarksReasoningAsNotTheAnswer()
    {
        var body = """
            {"choices":[{"finish_reason":"stop","message":{"role":"assistant",
              "content":"{\"summary\":\"清淡为主\",\"meals\":[]}",
              "reasoning_content":"先看过敏原…"}}],
             "usage":{"prompt_tokens":900,"completion_tokens":120,
                      "completion_tokens_details":{"reasoning_tokens":40}}}
            """;

        var result = await CompleteAsync(body, maxTokens: 8192);

        Assert.True(result.Success);
        Assert.Contains("\"summary\"", result.Content);
        Assert.Equal("stop", result.FinishReason);

        // 正文才是答案，推理过程只提示存在、不铺开。
        Assert.Contains("正文：", result.RawResponse);
        Assert.Contains("不是它给你的答案", result.RawResponse);
        Assert.DoesNotContain("先看过敏原", result.RawResponse);
    }

    [Fact]
    public async Task Complete_FlagsTruncatedContentSoTheCallerCanExplainIt()
    {
        // 正文非空但被截断：解析 JSON 时会失败，上层要靠 finish_reason 区分
        // 「模型不会写 JSON」和「模型没写完」。
        var body = """
            {"choices":[{"finish_reason":"length","message":{"role":"assistant",
              "content":"{\"summary\":\"清淡\",\"meals\":[{\"menu\":\"小米粥\""}}],
             "usage":{"prompt_tokens":900,"completion_tokens":8192}}
            """;

        var result = await CompleteAsync(body, maxTokens: 8192);

        Assert.True(result.Success);
        Assert.Equal("length", result.FinishReason);
        Assert.Equal(8192, result.CompletionTokens);
    }

    [Fact]
    public async Task Complete_UsesTheConfiguredMaxTokensInTheRequest()
    {
        var router = new StubRouter("""{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiChatClient(router, NullLogger<OpenAiChatClient>.Instance);

        await client.CompleteAsync(
            Provider,
            "m",
            [AiChatMessage.User("你好")],
            AiCallOptions.Default,
            "user-1");

        Assert.NotNull(router.LastRequest);
        var payload = JsonDocument.Parse(router.LastRequest!.Body!);

        // 默认额度必须给推理模型留出余量：2500 会被思考过程吃光。
        Assert.Equal(AiCallOptions.Default.MaxOutputTokens, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(AiCallOptions.Default.MaxOutputTokens >= 8000);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", router.LastRequest.Url);
    }

    [Fact]
    public async Task Complete_ReportsHttpErrorsWithTheStatusCode()
    {
        var result = await CompleteAsync("""{"error":{"message":"invalid api key"}}""", status: 401);

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("invalid api key", result.Error);
    }

    [Fact]
    public void DefaultOptions_LeaveRoomForReasoning()
    {
        // 这是这次翻车的根因，单独钉一条：默认值不许再被改回 2500 这种小数字。
        Assert.True(AiCallOptions.Default.MaxOutputTokens >= 8000);
        Assert.Equal(8192, new AiRecommendationOptions().MaxOutputTokens);
    }
}
