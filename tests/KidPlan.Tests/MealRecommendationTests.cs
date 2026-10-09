using KidPlan.Web.Models;
using KidPlan.Web.Services;
using KidPlan.Web.Services.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// AI 饮食推荐。
///
/// 这里最要紧的一条不是「模型能不能给出菜谱」，而是
/// **模型的输出绝对不能绕过敏原**：提示词里写「不要牛奶」只是请求，
/// 模型（尤其是本地小模型）会时不时忽略它。所以有一整套用例专门盯着
/// 「无论模型说什么，本地都要再查一遍」这个约束。
/// </summary>
public class MealRecommendationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 15, 0, TimeSpan.FromHours(8));
    private static readonly DateOnly Today = new(2026, 10, 4);

    private static MealRecommendationRequest Request(
        MealRecommendationScope scope = MealRecommendationScope.Day,
        int lookbackDays = 7,
        MealType? mealType = null,
        string? extra = null)
        => new(0, scope, Today, mealType, lookbackDays, IncludeAdHoc: true, null, null, extra);

    /// <summary>把配置好的假客户端和一个已登记过敏原的小孩装配起来。</summary>
    private static async Task<(TestHost Host, string UserId, int ChildId)> ArrangeAsync(
        string? allergicFoods = "牛奶、鸡蛋、花生",
        AllergySeverity severity = AllergySeverity.Severe)
    {
        var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods, severity: severity);

        // 全局配置里放一个端点 + 一个模型，推荐才有地方可去。
        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "fake",
            DefaultModel = "fake-model",
            Providers =
            [
                new AiProviderConfig
                {
                    Id = "fake",
                    Name = "假端点",
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Enabled = true,
                    Models = ["fake-model"],
                },
            ],
        });

        return (host, userId, childId);
    }

    private static MealRecommendationRequest For(int childId, MealRecommendationScope scope = MealRecommendationScope.Day)
        => new(childId, scope, Today, null, 7, IncludeAdHoc: true, null, null, null);

    // ------------------------------------------------------------------
    // JSON 容错解析
    // ------------------------------------------------------------------

    [Fact]
    public void ParseSuggestions_ReadsPlainJson()
    {
        var suggestions = MealRecommendationService.ParseSuggestions(
            """
            {
              "summary": "以易消化为主",
              "meals": [
                { "date": "2026-10-04", "mealType": "Breakfast", "time": "07:30",
                  "menu": "小米南瓜粥 + 水煮蛋黄", "foods": "小米、南瓜", "calories": 260,
                  "reason": "养胃" }
              ]
            }
            """,
            Request(mealType: MealType.Lunch),
            out var error,
            out var summary);

        Assert.Null(error);
        Assert.Equal("以易消化为主", summary);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(Today, suggestion.Date);
        Assert.Equal(MealType.Breakfast, suggestion.MealType);
        Assert.Equal(new TimeOnly(7, 30), suggestion.Time);
        Assert.Equal(260, suggestion.EstimatedCalories);
    }

    [Fact]
    public void ParseSuggestions_StripsMarkdownFenceAndSurroundingChatter()
    {
        // 模型经常会先寒暄一句，再用 ```json 包起来——这是最常见的两种「格式不合规」。
        var suggestions = MealRecommendationService.ParseSuggestions(
            """
            好的，我按您的要求安排如下：

            ```json
            {
              "summary": "清淡为主",
              "meals": [
                { "date": "2026-10-04", "mealType": "午餐", "time": "12:00", "menu": "番茄牛肉面" }
              ]
            }
            ```

            希望对您有帮助！
            """,
            Request(),
            out var error,
            out _);

        Assert.Null(error);
        Assert.Equal(MealType.Lunch, Assert.Single(suggestions).MealType);
    }

    [Fact]
    public void ParseSuggestions_BackfillsTimeForFixedMealsOnly()
    {
        var suggestions = MealRecommendationService.ParseSuggestions(
            """
            {
              "meals": [
                { "date": "2026-10-04", "mealType": "Dinner", "menu": "清蒸鲈鱼 + 米饭" },
                { "date": "2026-10-04", "mealType": "Snack", "menu": "一小把蓝莓" }
              ]
            }
            """,
            Request(),
            out var error,
            out _);

        Assert.Null(error);
        Assert.Equal(2, suggestions.Count);

        // 三餐没给时间就补默认值（否则落库会被校验拦下）；
        // 加餐没给时间就保持「不定时」，这正是加餐该有的样子。
        var dinner = suggestions.Single(s => s.MealType == MealType.Dinner);
        Assert.Equal(new TimeOnly(18, 0), dinner.Time);

        var snack = suggestions.Single(s => s.MealType == MealType.Snack);
        Assert.Null(snack.Time);
    }

    [Fact]
    public void ParseSuggestions_FallsBackToTargetDateAndMealType()
    {
        // 单餐推荐时模型常常只给 menu，日期和餐次就按请求补上。
        var suggestions = MealRecommendationService.ParseSuggestions(
            """{ "meals": [ { "menu": "番茄鸡蛋面" } ] }""",
            Request(scope: MealRecommendationScope.Meal, mealType: MealType.Lunch),
            out var error,
            out _);

        Assert.Null(error);
        var suggestion = Assert.Single(suggestions);
        Assert.Equal(Today, suggestion.Date);
        Assert.Equal(MealType.Lunch, suggestion.MealType);
        Assert.Equal(new TimeOnly(12, 0), suggestion.Time);
    }

    [Fact]
    public void ParseSuggestions_DropsDuplicateDateAndMealType()
    {
        // 去重规则必须和落库那套一致，否则会出现「界面显示 2 条、只写进去 1 条」。
        var suggestions = MealRecommendationService.ParseSuggestions(
            """
            {
              "meals": [
                { "date": "2026-10-04", "mealType": "Lunch", "menu": "方案一" },
                { "date": "2026-10-04", "mealType": "Lunch", "menu": "方案二" }
              ]
            }
            """,
            Request(),
            out _,
            out _);

        Assert.Equal("方案一", Assert.Single(suggestions).Menu);
    }

    [Fact]
    public void ParseSuggestions_SkipsEntriesWithoutMenu()
    {
        var suggestions = MealRecommendationService.ParseSuggestions(
            """{ "meals": [ { "date": "2026-10-04", "mealType": "Lunch", "menu": "  " } ] }""",
            Request(),
            out var error,
            out _);

        Assert.Empty(suggestions);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseSuggestions_ReportsErrorWhenThereIsNoJsonAtAll()
    {
        var suggestions = MealRecommendationService.ParseSuggestions(
            "抱歉，我无法完成这个请求。",
            Request(),
            out var error,
            out _);

        Assert.Empty(suggestions);
        Assert.Contains("JSON", error);
    }

    [Fact]
    public void ExtractJsonObject_IgnoresBracesInsideStrings()
    {
        // 花括号配对如果不管字符串，菜单里写个「{小份}」就会把 JSON 截断，
        // 报出来的错会是「JSON 解析失败」，查起来很费时间。
        var json = MealRecommendationService.ExtractJsonObject(
            """{ "summary": "带 } 的说明", "meals": [] }""");

        Assert.Equal("""{ "summary": "带 } 的说明", "meals": [] }""", json);
    }

    [Fact]
    public void ExtractJsonObject_HandlesEscapedQuotes()
    {
        var json = MealRecommendationService.ExtractJsonObject(
            """{ "menu": "他说\"不要牛奶\"" }""");

        Assert.Equal("""{ "menu": "他说\"不要牛奶\"" }""", json);
    }

    // ------------------------------------------------------------------
    // 走完整链路的推荐
    // ------------------------------------------------------------------

    [Fact]
    public async Task Recommend_BlocksSevereAllergenHits_EvenIfModelSaysItIsSafe()
    {
        var (host, userId, childId) = await ArrangeAsync("牛奶、鸡蛋、花生", AllergySeverity.Anaphylaxis);
        await using var _ = host;

        // 模型明知有过敏还给了含牛奶和鸡蛋的菜——这正是必须本地复核的场景。
        host.Ai.Response = """
            {
              "summary": "营养均衡",
              "meals": [
                { "date": "2026-10-04", "mealType": "Breakfast", "time": "07:30", "menu": "牛奶燕麦粥 + 蒸蛋" },
                { "date": "2026-10-04", "mealType": "Lunch", "time": "12:00", "menu": "番茄牛肉饭" }
              ]
            }
            """;

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.True(result.Success);

        // 安全的那条留下，危险的进「已拦截」，绝不会出现在可写入列表里。
        var kept = Assert.Single(result.Suggestions);
        Assert.Equal("番茄牛肉饭", kept.Menu);

        var blocked = Assert.Single(result.Blocked);
        Assert.True(blocked.Blocked);
        Assert.Contains("牛奶", blocked.BlockReason + string.Join("、", blocked.AllergenMatches.Select(m => m.Allergen)));
        Assert.True(result.HasBlocked);
    }

    [Fact]
    public async Task Recommend_KeepsModerateHitsButMarksThem()
    {
        var (host, userId, childId) = await ArrangeAsync("牛奶", AllergySeverity.Moderate);
        await using var _ = host;

        host.Ai.Response = """
            {
              "meals": [
                { "date": "2026-10-04", "mealType": "Lunch", "time": "12:00", "menu": "牛奶布丁" }
              ]
            }
            """;

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        // 「中等」不直接丢弃——家长可能有自己的判断；但要带上命中信息，
        // 写入时还会再被 MealService 的确认关卡拦住。
        var kept = Assert.Single(result.Suggestions);
        Assert.True(kept.HasAllergen);
        Assert.Equal(AllergySeverity.Moderate, kept.MaxSeverity);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public async Task Recommend_PromptCarriesAllergensAndLookbackWindow()
    {
        var (host, userId, childId) = await ArrangeAsync("牛奶");
        await using var _ = host;

        host.Ai.Response = """{ "meals": [ { "date": "2026-10-04", "mealType": "Lunch", "menu": "番茄面" } ] }""";

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today.AddDays(-3),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "清汤面",
        });

        var request = new MealRecommendationRequest(
            childId, MealRecommendationScope.Day, Today, null, 5, true, null, null, "不要辣");

        await recommender.RecommendAsync(userId, request);

        var prompt = host.Ai.LastMessages.Single(m => m.Role == "user").Content;

        // 过敏原必须进提示词：让模型有机会避开，才能减少「生成完再被拦掉」的浪费。
        Assert.Contains("牛奶", prompt);

        // 前置周期：5 天应该是近 5 天（10-03 到 09-29），不能偷偷扩大到 30 天。
        Assert.Contains("2026-10-03", prompt);
        Assert.Contains("2026-09-29", prompt);
        Assert.DoesNotContain("2026-09-28", prompt);

        // 历史菜单要带上，模型才能避免重复。
        Assert.Contains("清汤面", prompt);

        // 家长额外要求原样传递。
        Assert.Contains("不要辣", prompt);

        // 输出格式必须写清楚，否则小模型会回一大段散文。
        Assert.Contains("\"mealType\"", prompt);
    }

    [Fact]
    public async Task Recommend_ClampsLookbackToThirtyDays()
    {
        var (host, userId, childId) = await ArrangeAsync("牛奶");
        await using var _ = host;

        host.Ai.Response = """{ "meals": [ { "date": "2026-10-04", "mealType": "Lunch", "menu": "番茄面" } ] }""";

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var request = new MealRecommendationRequest(
            childId, MealRecommendationScope.Day, Today, null, 3650, true, null, null, null);

        var prompt = await recommender.BuildPromptPreviewAsync(userId, request);

        // 硬上限 30 天：不然一次把十年的记录塞进提示词，模型抓不住重点还会爆上下文。
        Assert.Contains(MealRecommendationService.MaxLookbackDays.ToString(), prompt);
        Assert.Contains(Today.AddDays(-30).ToString("yyyy-MM-dd"), prompt);
        Assert.DoesNotContain(Today.AddDays(-31).ToString("yyyy-MM-dd"), prompt);
    }

    [Fact]
    public async Task Recommend_FailsWithAReadableMessageWhenNoProviderConfigured()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.False(result.Success);
        Assert.Contains("模型设置", result.Error);
        Assert.Equal(0, host.Ai.CallCount);
    }

    [Fact]
    public async Task Recommend_RequiresAModelName()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        // 端点有了，但没填任何模型名——这时应该给出「手工填一个」的提示，
        // 而不是发一个 model="" 的请求过去换来一句莫名其妙的 400。
        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "fake",
            Providers = [new AiProviderConfig { Id = "fake", Name = "假端点", Enabled = true }],
        });

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.False(result.Success);
        Assert.Contains("模型", result.Error);
        Assert.Equal(0, host.Ai.CallCount);
    }

    [Fact]
    public async Task Recommend_SurfacesTransportFailureWithTheRawResponse()
    {
        var (host, userId, childId) = await ArrangeAsync();
        await using var _ = host;

        host.Ai.Fail = true;
        host.Ai.ErrorMessage = "连不上 127.0.0.1:1234";

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.False(result.Success);
        Assert.Contains("连不上", result.Error);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task Recommend_ImportsOnlySafeSuggestions()
    {
        var (host, userId, childId) = await ArrangeAsync("花生", AllergySeverity.Severe);
        await using var _ = host;

        host.Ai.Response = """
            {
              "meals": [
                { "date": "2026-10-04", "mealType": "Lunch", "time": "12:00", "menu": "花生酱拌面" },
                { "date": "2026-10-04", "mealType": "Dinner", "time": "18:00", "menu": "清蒸鲈鱼 + 米饭" }
              ]
            }
            """;

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var result = await recommender.RecommendAsync(userId, For(childId));
        var imported = await meals.ImportAsync(userId, childId, result.Suggestions.Select(s => s.ToMealPlan()));

        Assert.Single(imported.Created.Select(m => m));
        Assert.Empty(imported.Rejected);

        var day = await meals.GetDayAsync(userId, childId, Today);
        var written = Assert.Single(day.Meals);
        Assert.Equal("清蒸鲈鱼 + 米饭", written.Meal.Menu);
        Assert.True(written.Meal.CreatedFromAiSuggestion);
    }

    [Fact]
    public async Task Recommend_SaysTheOutputWasTruncatedWhenTheModelRanOutOfTokens()
    {
        // 真实翻车场景：推理模型把 max_tokens 用光，JSON 写到一半就断了。
        // 这时的报错不能只是「找不到 JSON 对象」——那是模型不会写；
        // 要说清是「没写完」，并指向「最大输出 token」这个配置项。
        var (host, userId, childId) = await ArrangeAsync();
        await using var _ = host;

        host.Ai.Response = """{"summary":"清淡为主","meals":[{"mealType":"Breakfast","menu":"小米粥""";
        host.Ai.FinishReason = "length";

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.False(result.Success);
        Assert.Contains("截断", result.Error);
        Assert.Contains("最大输出 token", result.Error);
        Assert.Contains("8000", result.Error);
    }

    [Fact]
    public async Task Recommend_KeepsTheModelDiagnosticWhenParsingFails()
    {
        // 解析失败时界面要能看到模型到底说了什么，而不是一片空白。
        var (host, userId, childId) = await ArrangeAsync();
        await using var _ = host;

        host.Ai.Response = "今天天气不错，我不想写 JSON。";

        using var scope = host.CreateScope();
        var recommender = scope.ServiceProvider.GetRequiredService<IMealRecommendationService>();

        var result = await recommender.RecommendAsync(userId, For(childId));

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
        Assert.Contains("今天天气不错", result.RawResponse);
        Assert.Equal("fake-model", result.Model);
    }

    [Fact]
    public async Task Import_RejectsSevereHitsEvenIfCalledDirectly()
    {
        // 「只在推荐服务里过滤」是不够的：导入是一个公开动作，
        // 只要有人绕过界面直接调它，就必须同样拦得住。
        var (host, userId, childId) = await ArrangeAsync("花生", AllergySeverity.Severe);
        await using var _ = host;

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var imported = await meals.ImportAsync(userId, childId,
        [
            new MealPlan
            {
                Date = Today,
                MealType = MealType.Lunch,
                PlannedTime = new TimeOnly(12, 0),
                Menu = "花生酱拌面",
                CreatedFromAiSuggestion = true,
            },
            new MealPlan
            {
                Date = Today,
                MealType = MealType.Dinner,
                PlannedTime = new TimeOnly(18, 0),
                Menu = "番茄鸡蛋面",
                CreatedFromAiSuggestion = true,
            },
        ]);

        Assert.Single(imported.Created.Select(m => m));
        var rejection = Assert.Single(imported.Rejected);
        Assert.Contains("花生", rejection.Reason);
        Assert.True(rejection.Meal.CreatedFromAiSuggestion);
    }

    [Fact]
    public async Task Import_AcceptsUntimedSnacks()
    {
        var (host, userId, childId) = await ArrangeAsync(null);
        await using var _ = host;

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var imported = await meals.ImportAsync(userId, childId,
        [
            new MealPlan
            {
                Date = Today,
                MealType = MealType.Snack,
                PlannedTime = null,
                Menu = "一小把蓝莓",
                CreatedFromAiSuggestion = true,
            },
        ]);

        Assert.Single(imported.Created);
        Assert.Empty(imported.Rejected);
    }
}
