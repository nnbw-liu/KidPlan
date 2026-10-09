using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KidPlan.Web.Models;

namespace KidPlan.Web.Services.Ai;

/// <summary>推荐的粒度。</summary>
public enum MealRecommendationScope
{
    /// <summary>单餐：指定某一餐该吃什么。</summary>
    Meal = 0,

    /// <summary>单天：一整天的早/午/晚（可选加餐）。</summary>
    Day = 1,

    /// <summary>单周：从目标日期起 7 天。</summary>
    Week = 2,
}

/// <summary>一次推荐请求。</summary>
public sealed record MealRecommendationRequest(
    int ChildId,
    MealRecommendationScope Scope,
    DateOnly TargetDate,
    MealType? MealType,
    int LookbackDays,
    bool IncludeAdHoc,
    string? ProviderId,
    string? Model,
    string? ExtraInstruction);

/// <summary>一条推荐出来的餐次。</summary>
public sealed record MealSuggestion(
    DateOnly Date,
    MealType MealType,
    TimeOnly? Time,
    string Menu,
    string? Foods,
    int? EstimatedCalories,
    string? Reason,
    IReadOnlyList<AllergyMatch> AllergenMatches,
    bool Blocked,
    string? BlockReason)
{
    public AllergySeverity? MaxSeverity => AllergyMatcher.MaxSeverity(AllergenMatches);

    public bool HasAllergen => AllergenMatches.Count > 0;

    /// <summary>转成可写入的 <see cref="MealPlan"/>。</summary>
    public MealPlan ToMealPlan() => new()
    {
        Date = Date,
        MealType = MealType,
        PlannedTime = Time,
        Menu = Menu,
        Foods = Foods,
        EstimatedCalories = EstimatedCalories,
        CreatedFromAiSuggestion = true,
    };
}

/// <summary>一次推荐的完整结果。</summary>
public sealed record MealRecommendationResult(
    bool Success,
    string? Error,
    string Summary,
    IReadOnlyList<MealSuggestion> Suggestions,
    IReadOnlyList<MealSuggestion> Blocked,
    string RawResponse,
    string ProviderName,
    string Model,
    string PromptPreview)
{
    public static MealRecommendationResult Fail(string error) =>
        new(false, error, string.Empty, [], [], string.Empty, string.Empty, string.Empty, string.Empty);

    public bool HasBlocked => Blocked.Count > 0;
}

public interface IMealRecommendationService
{
    Task<MealRecommendationResult> RecommendAsync(
        string userId,
        MealRecommendationRequest request,
        CancellationToken ct = default);

    /// <summary>做一次「预览」：只把要发给模型的上下文和提示词拼出来，不真的调用模型。</summary>
    Task<string> BuildPromptPreviewAsync(
        string userId,
        MealRecommendationRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// 饮食推荐。
///
/// 这里有一条不能省的设计约束：**模型说什么不算数，过敏原必须由本地再查一遍**。
/// 大模型对「不要出现牛奶」这类约束的遵守是概率性的，尤其是本地小模型；
/// 把「会不会给孩子过敏的东西」押在它的服从性上是不合适的。
/// 所以无论它返回什么，都会再用 <see cref="AllergyMatcher"/> 逐条复核，
/// 强过敏 / 过敏性休克命中的餐直接判为不可写入。
/// </summary>
public sealed class MealRecommendationService(
    IChildService childService,
    IGrowthService growthService,
    IMealService mealService,
    IAiSettingsStore settingsStore,
    IAiChatClient chatClient,
    ILogger<MealRecommendationService> logger) : IMealRecommendationService
{
    /// <summary>前置周期的硬上限。需求里写死 30 天，这里做成常量而不是配置，
    /// 免得有人在配置里写 3650 天，一次把几万条记录塞进提示词。</summary>
    public const int MaxLookbackDays = 30;

    public async Task<MealRecommendationResult> RecommendAsync(
        string userId,
        MealRecommendationRequest request,
        CancellationToken ct = default)
    {
        var settings = await settingsStore.GetEffectiveAsync(userId, ct);

        if (!TryResolveProvider(settings, request.ProviderId, out var provider, out var providerError))
        {
            return MealRecommendationResult.Fail(providerError);
        }

        var model = ResolveModel(provider, request.Model, settings);
        if (string.IsNullOrWhiteSpace(model))
        {
            return MealRecommendationResult.Fail("没有可用的模型名。请在「模型设置」里给这个端点加一个模型，或手工填一个。");
        }

        var context = await BuildContextAsync(userId, request, ct);
        if (context.Allergens.Count == 0)
        {
            // 不是硬错误，但必须提醒：没有过敏原数据时推荐的安全性无从谈起。
            logger.LogInformation("小孩 {ChildId} 还没有登记过敏原，推荐结果无法做过敏过滤。", request.ChildId);
        }

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(context, request);

        var options = new AiCallOptions(
            settings.Recommendation?.Temperature ?? AiCallOptions.Default.Temperature,
            settings.Recommendation?.MaxOutputTokens ?? AiCallOptions.Default.MaxOutputTokens,
            settings.Recommendation?.TimeoutSeconds ?? AiCallOptions.Default.TimeoutSeconds);

        var result = await chatClient.CompleteAsync(
            provider,
            model,
            [AiChatMessage.System(systemPrompt), AiChatMessage.User(userPrompt)],
            options,
            userId,
            ct);

        var promptPreview = BuildPreview(systemPrompt, userPrompt);

        if (!result.Success)
        {
            return new MealRecommendationResult(
                false, result.Error, string.Empty, [], [],
                result.RawResponse, provider.Name, model, promptPreview);
        }

        var parsed = ParseSuggestions(result.Content, request, out var parseError, out var summary);

        if (parseError is not null)
        {
            // finish_reason=length 说明不是「模型不会写 JSON」，而是「它没写完」——
            // 这两种情况的处理方式完全不同（调大额度 vs 换模型），所以要说清楚。
            var truncated = result.FinishReason == "length"
                ? $"模型这次是写到一半被截断的（达到最大输出 token {options.MaxOutputTokens}，finish_reason=length）：" +
                  "把「最大输出 token」调大（推理模型建议 8000 以上），或者把范围缩小（单周改单天）再试。"
                : string.Empty;

            var detail = string.IsNullOrEmpty(truncated)
                ? $"{parseError}（模型输出已保留在下方，可以换一个模型再试，或把 temperature 调低）"
                : $"{parseError}。{truncated}";

            return new MealRecommendationResult(
                false, detail, string.Empty, [], [], result.RawResponse, provider.Name, model, promptPreview);
        }

        // 本地复核：过敏原以数据库为准，不以模型的自我声明为准。
        var kept = new List<MealSuggestion>();
        var blocked = new List<MealSuggestion>();

        foreach (var suggestion in parsed)
        {
            var matches = AllergyMatcher.FindMatches(context.Allergens, suggestion.Menu, suggestion.Foods);
            var max = AllergyMatcher.MaxSeverity(matches);

            if (max is { } severity && severity >= AllergySeverity.Severe)
            {
                blocked.Add(suggestion with
                {
                    AllergenMatches = matches,
                    Blocked = true,
                    BlockReason = $"含{severity.ToChinese()}级过敏原，不能写入",
                });
                continue;
            }

            kept.Add(suggestion with { AllergenMatches = matches });
        }

        return new MealRecommendationResult(
            true, null, summary, kept, blocked, result.Content, provider.Name, model, promptPreview);
    }

    public async Task<string> BuildPromptPreviewAsync(
        string userId,
        MealRecommendationRequest request,
        CancellationToken ct = default)
    {
        var context = await BuildContextAsync(userId, request, ct);
        return BuildPreview(BuildSystemPrompt(), BuildUserPrompt(context, request));
    }

    private static bool TryResolveProvider(
        AiSettingsDocument settings,
        string? requestedId,
        out AiProviderConfig provider,
        out string error)
    {
        var enabled = settings.Providers.Where(p => p.Enabled).ToList();

        if (enabled.Count == 0)
        {
            provider = new AiProviderConfig();
            error = "还没有配置任何模型端点。请到「模型设置」里加一个 OpenAI 兼容的接口。";
            return false;
        }

        var id = string.IsNullOrWhiteSpace(requestedId) ? settings.DefaultProviderId : requestedId;
        var found = enabled.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? enabled[0];

        provider = found;
        error = string.Empty;
        return true;
    }

    private static string ResolveModel(AiProviderConfig provider, string? requested, AiSettingsDocument settings)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            return settings.DefaultModel.Trim();
        }

        return provider.Models.FirstOrDefault() ?? string.Empty;
    }

    private async Task<RecommendationContext> BuildContextAsync(
        string userId,
        MealRecommendationRequest request,
        CancellationToken ct)
    {
        var child = await childService.RequireAsync(request.ChildId, userId, ct);
        var allergens = await childService.ListAllergensAsync(request.ChildId, userId, ct);

        var lookback = Math.Clamp(
            request.LookbackDays <= 0 ? 7 : request.LookbackDays,
            1,
            MaxLookbackDays);

        var historyFrom = request.TargetDate.AddDays(-lookback);
        var historyTo = request.TargetDate.AddDays(-1);

        // 覆盖到推荐区间：单周要 7 天，单天 / 单餐 1 天。
        var coverDays = request.Scope == MealRecommendationScope.Week ? 7 : 1;
        var coverFrom = request.TargetDate;
        var coverTo = request.TargetDate.AddDays(coverDays - 1);

        var meals = historyFrom <= historyTo
            ? await mealService.GetRangeAsync(userId, request.ChildId, historyFrom, historyTo, ct)
            : new MealRangeSummary(historyTo, historyTo, []);

        var growth = historyFrom <= historyTo
            ? await growthService.ListAsync(userId, request.ChildId, historyFrom, historyTo, ct: ct)
            : [];

        var today = DateOnly.FromDateTime(DateTime.Today);

        return new RecommendationContext(
            child,
            allergens,
            meals,
            growth,
            lookback,
            coverFrom,
            coverTo,
            AgeCalculator.MonthsBetween(child.BirthDate, today),
            AgeCalculator.Describe(child.BirthDate, today));
    }

    private static string BuildSystemPrompt() =>
        """
        你是「KidPlan」的儿童饮食安排助手，服务对象是中国家庭里的一个小孩。

        你的任务是：根据孩子的年龄、过敏原、最近一段时间的饮食与身体情况，
        给出**可以直接执行**的饮食安排建议。

        硬性规则（违反任意一条，这次输出就是无效的）：
        1. 【最高优先级】绝对不能出现「禁用食材」里列出的任何食物，
           也包括它的制品、衍生品、酱料、汤底和调味料。
           例如禁用「牛奶」时，牛奶、酸奶、奶酪、芝士、黄油、奶油、奶粉、炼乳、奶昔都不允许出现。
        2. 只安排符合孩子月龄的食物。不给整粒坚果、果冻、大块肉、带刺鱼、整颗葡萄这类易噎食物；
           1 岁以内不加盐、不加糖、不加蜂蜜。
        3. 参考最近的身体记录：有发烧、腹泻、咳嗽、食欲差时，
           安排清淡、易消化、少油少盐的食物，并适当减少单餐分量。
        4. 参考最近吃过的菜，**不要连续重复**；如果某样菜最近吃得少，可以适当补上。
        5. 只输出 JSON，不要输出任何解释文字，不要用 ``` 代码块包裹，不要在 JSON 前后加字。
        """;

    private static string BuildUserPrompt(RecommendationContext context, MealRecommendationRequest request)
    {
        var builder = new StringBuilder();

        builder.AppendLine("## 孩子");
        builder.AppendLine($"- 小名：{context.Child.Alias}");
        builder.AppendLine($"- 年龄：{context.AgeText}（{context.AgeMonths} 个月）");
        builder.AppendLine($"- 性别：{context.Child.Gender.ToChinese()}");
        if (!string.IsNullOrWhiteSpace(context.Child.BloodType))
        {
            builder.AppendLine($"- 血型：{context.Child.BloodType}");
        }

        if (!string.IsNullOrWhiteSpace(context.Child.Notes))
        {
            builder.AppendLine($"- 需要特别注意：{context.Child.Notes}");
        }

        builder.AppendLine();
        builder.AppendLine("## 过敏原（等级从重到轻）");
        if (context.Allergens.Count == 0)
        {
            builder.AppendLine("- （这个孩子还没有登记任何过敏原；仍然要遵循上面的通用规则）");
        }
        else
        {
            foreach (var allergen in context.Allergens)
            {
                var kind = allergen.Kind == AllergenKind.Food ? "食物" : "非食物";
                var tag = allergen.Kind == AllergenKind.Food ? string.Empty : "（不参与菜单比对，仅供参考）";
                builder.AppendLine($"- {allergen.Name}｜{kind}｜{allergen.Severity.ToChinese()}{tag}");
            }
        }

        var hardBans = context.Allergens
            .Where(a => a.Kind == AllergenKind.Food && a.Severity >= AllergySeverity.Severe)
            .Select(a => a.Name)
            .ToList();

        var softBans = context.Allergens
            .Where(a => a.Kind == AllergenKind.Food
                && a.Severity is AllergySeverity.Moderate)
            .Select(a => a.Name)
            .ToList();

        var tolerant = context.Allergens
            .Where(a => a.Kind == AllergenKind.Food
                && a.Severity is AllergySeverity.Intolerance or AllergySeverity.Mild)
            .Select(a => a.Name)
            .ToList();

        builder.AppendLine();
        builder.AppendLine("## 禁用食材（规则 1，绝对不允许出现）");
        builder.AppendLine(hardBans.Count > 0
            ? "- " + string.Join("、", hardBans) + "（含其制品与衍生品）"
            : "- 无（但过敏原列表里的食物仍然要谨慎）");

        if (softBans.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 尽量回避");
            builder.AppendLine($"- {string.Join("、", softBans)}（如果确实要安排，必须在 reason 里写明原因）");
        }

        if (tolerant.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 可以少量尝试（出现过不适，但不是过敏）");
            builder.AppendLine($"- {string.Join("、", tolerant)}");
        }

        builder.AppendLine();
        builder.AppendLine($"## 最近 {context.LookbackDays} 天的饮食情况（{context.HistoryFrom:yyyy-MM-dd} ~ {context.HistoryTo:yyyy-MM-dd}）");

        var historyDays = context.History.Days.Where(d => !d.IsEmpty).ToList();
        if (historyDays.Count == 0)
        {
            builder.AppendLine("- 这段时间没有任何饮食记录。");
        }
        else
        {
            foreach (var day in historyDays)
            {
                builder.AppendLine($"- {day.Date:MM-dd}（{DayName(day.Date.DayOfWeek)}）");
                foreach (var item in day.Meals)
                {
                    var meal = item.Meal;
                    var parts = new List<string> { meal.MealType.ToChinese() };

                    if (meal.PlannedTime is { } time)
                    {
                        parts.Add(time.ToString("HH:mm"));
                    }

                    parts.Add($"「{meal.Menu}」");

                    if (!string.IsNullOrWhiteSpace(meal.Foods))
                    {
                        parts.Add($"食材：{meal.Foods}");
                    }

                    parts.Add(item.State switch
                    {
                        MealTimeState.Eaten => meal.Appetite == AppetiteLevel.Unknown
                            ? "已吃完"
                            : $"已吃完，食欲{meal.Appetite.ToChinese()}",
                        MealTimeState.Skipped => "没吃（跳过）",
                        MealTimeState.Unscheduled => "安排了点心/零食（未定时点）",
                        _ => "安排了但没确认",
                    });

                    if (!string.IsNullOrWhiteSpace(meal.FinishNote))
                    {
                        parts.Add($"情况：{meal.FinishNote}");
                    }

                    builder.AppendLine($"  - {string.Join('，', parts)}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine($"## 最近 {context.LookbackDays} 天的身体情况");
        if (context.Growth.Count == 0)
        {
            builder.AppendLine("- 这段时间没有任何身体记录。");
        }
        else
        {
            foreach (var record in context.Growth.OrderBy(r => r.RecordedAt))
            {
                var parts = new List<string>();
                if (record.TemperatureC is { } temperature)
                {
                    parts.Add($"体温 {temperature:0.#}℃");
                }

                if (record.Appetite != AppetiteLevel.Unknown)
                {
                    parts.Add($"食欲{record.Appetite.ToChinese()}");
                }

                if (record.Mood != MoodLevel.Unknown)
                {
                    parts.Add($"精神{record.Mood.ToChinese()}");
                }

                if (record.SleepHours is { } sleep)
                {
                    parts.Add($"睡眠 {sleep:0.#} 小时");
                }

                if (record.WeightKg is { } weight)
                {
                    parts.Add($"体重 {weight:0.##} kg");
                }

                if (!string.IsNullOrWhiteSpace(record.Symptoms))
                {
                    parts.Add($"症状：{record.Symptoms}");
                }

                if (!string.IsNullOrWhiteSpace(record.Notes))
                {
                    parts.Add($"备注：{record.Notes}");
                }

                if (parts.Count > 0)
                {
                    var date = DateOnly.FromDateTime(record.RecordedAt.DateTime);
                    builder.AppendLine($"- {date:MM-dd}：{string.Join('，', parts)}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 这次要推荐什么");
        builder.AppendLine(request.Scope switch
        {
            MealRecommendationScope.Meal =>
                $"只推荐 {context.CoverFrom:yyyy-MM-dd} 的**一餐**：" +
                $"{(request.MealType ?? MealType.Lunch).ToChinese()}。",
            MealRecommendationScope.Week =>
                $"推荐从 {context.CoverFrom:yyyy-MM-dd} 开始的**一周（7 天）**安排，" +
                "每天都要有早餐、午餐、晚餐。",
            _ =>
                $"推荐 {context.CoverFrom:yyyy-MM-dd} **一整天**的安排，" +
                "要有早餐、午餐、晚餐。",
        });

        if (request.IncludeAdHoc)
        {
            builder.AppendLine(
                "另外，每天可以安排 0~2 顿加餐：下午的用 \"Snack\"（点心），" +
                "上午或睡前的用 \"Treat\"（零食）。加餐请给时间，方便家里照着做；" +
                "如果确实不定时，可以省略 time 字段。");
        }
        else
        {
            builder.AppendLine("这次不需要安排点心 / 零食。");
        }

        if (!string.IsNullOrWhiteSpace(request.ExtraInstruction))
        {
            builder.AppendLine();
            builder.AppendLine("## 家长额外要求");
            builder.AppendLine(request.ExtraInstruction.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("## 输出格式（严格按这个 JSON 结构）");
        builder.AppendLine(
            """
            {
              "summary": "一句话说明这次安排的思路",
              "meals": [
                {
                  "date": "yyyy-MM-dd",
                  "mealType": "Breakfast | Lunch | Dinner | Snack | Treat",
                  "time": "HH:mm",
                  "menu": "这一餐吃什么，用 + 连接，例如「小米南瓜粥 + 水煮蛋黄 + 清炒西兰花」",
                  "foods": "食材，用、分隔",
                  "calories": 320,
                  "reason": "为什么这样安排，一两句话"
                }
              ]
            }
            """);

        return builder.ToString();
    }

    private static string BuildPreview(string systemPrompt, string userPrompt)
        => $"### 系统提示词\n{systemPrompt}\n\n### 用户提示词\n{userPrompt}";

    /// <summary>
    /// 解析模型输出。模型经常会加一堆废话或者 ```json 包裹，所以这里先做容错裁剪，
    /// 再反序列化；解析不出来时把原文返回给界面，而不是丢掉。
    /// </summary>
    internal static List<MealSuggestion> ParseSuggestions(
        string content,
        MealRecommendationRequest request,
        out string? error,
        out string summary)
    {
        error = null;
        summary = string.Empty;
        var results = new List<MealSuggestion>();

        var json = ExtractJsonObject(content);
        if (json is null)
        {
            error = "模型输出里找不到 JSON 对象";
            return results;
        }

        AiRecommendationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AiRecommendationPayload>(json, AiJson.Options);
        }
        catch (JsonException ex)
        {
            error = $"模型输出的 JSON 解析失败：{ex.Message}";
            return results;
        }

        if (payload is null)
        {
            error = "模型输出的 JSON 反序列化后为空";
            return results;
        }

        summary = payload.Summary?.Trim() ?? string.Empty;

        if (payload.Meals is null || payload.Meals.Count == 0)
        {
            error = "模型没有给出任何 meals";
            return results;
        }

        var seen = new HashSet<(DateOnly, MealType)>();

        foreach (var item in payload.Meals)
        {
            if (string.IsNullOrWhiteSpace(item.Menu))
            {
                continue;
            }

            var date = ParseDate(item.Date) ?? request.TargetDate;
            var mealType = ParseMealType(item.MealType) ?? request.MealType ?? MealType.Lunch;

            // 固定三餐必须有时间，否则落库会被 Validate 拦下来。
            // 模型经常忘记给加餐时间，这里补一个合理默认值而不是丢掉这条。
            var time = ParseTime(item.Time) ?? (mealType.IsFixedMeal() ? DefaultTime(mealType) : null);

            // 落库时的去重规则和这里保持一致，避免界面显示 7 条、实际只写进去 5 条。
            if (!seen.Add((date, mealType)))
            {
                continue;
            }

            results.Add(new MealSuggestion(
                date,
                mealType,
                time,
                item.Menu.Trim(),
                string.IsNullOrWhiteSpace(item.Foods) ? null : item.Foods.Trim(),
                item.Calories is > 0 and < 10000 ? item.Calories : null,
                string.IsNullOrWhiteSpace(item.Reason) ? null : item.Reason.Trim(),
                [],
                false,
                null));
        }

        if (results.Count == 0)
        {
            error = "模型给出的 meals 里没有一条可用的（菜单都是空的）";
        }

        return results;
    }

    /// <summary>从可能夹着废话的输出里抠出第一个完整的 JSON 对象。</summary>
    internal static string? ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.Trim();

        // 去掉 ```json ... ``` 包裹。
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                text = text[..lastFence];
            }

            text = text.Trim();
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        // 按括号配对找结尾，这样字符串里出现的 } 不会提前截断。
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value.Trim(), out var date) ? date : null;
    }

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParse(value.Trim(), out var time) ? time : null;
    }

    /// <summary>中英文餐次名都认，模型有时会写「早餐」。</summary>
    internal static MealType? ParseMealType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "breakfast" or "早餐" or "早饭" => MealType.Breakfast,
            "lunch" or "午餐" or "午饭" or "中餐" => MealType.Lunch,
            "dinner" or "晚餐" or "晚饭" => MealType.Dinner,
            "snack" or "点心" or "加餐" or "下午茶" => MealType.Snack,
            "treat" or "零食" or "小食" => MealType.Treat,
            _ => null,
        };
    }

    private static TimeOnly DefaultTime(MealType mealType) => mealType switch
    {
        MealType.Breakfast => new TimeOnly(7, 30),
        MealType.Lunch => new TimeOnly(12, 0),
        MealType.Dinner => new TimeOnly(18, 0),
        MealType.Snack => new TimeOnly(15, 30),
        _ => new TimeOnly(10, 0),
    };

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>提示词里用到的上下文快照。</summary>
    private sealed record RecommendationContext(
        Child Child,
        IReadOnlyList<ChildAllergen> Allergens,
        MealRangeSummary History,
        IReadOnlyList<GrowthRecord> Growth,
        int LookbackDays,
        DateOnly CoverFrom,
        DateOnly CoverTo,
        int AgeMonths,
        string AgeText)
    {
        public DateOnly HistoryFrom => CoverFrom.AddDays(-LookbackDays);

        public DateOnly HistoryTo => CoverFrom.AddDays(-1);
    }

    /// <summary>模型输出的 JSON 结构。</summary>
    private sealed class AiRecommendationPayload
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("meals")]
        public List<AiRecommendationMeal>? Meals { get; set; }
    }

    private sealed class AiRecommendationMeal
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("mealType")]
        public string? MealType { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("menu")]
        public string? Menu { get; set; }

        [JsonPropertyName("foods")]
        public string? Foods { get; set; }

        [JsonPropertyName("calories")]
        public int? Calories { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
