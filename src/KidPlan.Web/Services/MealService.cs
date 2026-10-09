using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services;

/// <summary>
/// 安排的内容命中了小孩的过敏原，且家长还没有明确确认时抛出。
/// 界面捕获它可以弹出“仍然要安排吗”的确认框。
/// </summary>
public sealed class AllergyConflictException(
    IReadOnlyList<AllergyMatch> matches,
    AllergySeverity maxSeverity,
    AllergyRiskAction action,
    bool requiresExplicitReconfirmation = false)
    : Exception($"这一餐命中了过敏原：{string.Join('、', matches.Select(m => m.Allergen))}")
{
    public IReadOnlyList<AllergyMatch> Matches { get; } = matches;

    /// <summary>命中的最高等级。</summary>
    public AllergySeverity MaxSeverity { get; } = maxSeverity;

    /// <summary>按等级该用多大的力度处理。</summary>
    public AllergyRiskAction Action { get; } = action;

    /// <summary>
    /// true 表示「已经勾了我知道风险，但等级是强过敏/休克，还需要再点一次确认」。
    /// 界面据此把提示从「请勾选」换成「请二次确认」。
    /// </summary>
    public bool RequiresExplicitReconfirmation { get; } = requiresExplicitReconfirmation;

    /// <summary>这个等级下必须勾选「我知道风险」才能保存。</summary>
    public bool RequiresAcknowledgement => Action != AllergyRiskAction.Notice;
}

/// <summary>批量写入（AI 推荐落地）的结果。</summary>
public sealed record MealImportResult(
    IReadOnlyList<MealPlan> Created,
    IReadOnlyList<MealImportRejection> Rejected);

/// <summary>被拒绝写入的一餐及原因。</summary>
public sealed record MealImportRejection(
    MealPlan Meal,
    string Reason,
    IReadOnlyList<AllergyMatch> Matches);

public interface IMealService
{
    /// <summary>取某一天的饮食安排，并附带按系统时间推算的状态和过敏命中。</summary>
    Task<MealDaySummary> GetDayAsync(string userId, int childId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// 取一段日期区间（含首尾）的餐饮记录，按天拆好，用于「周 / 月 / 全部」回顾。
    /// 区间内没有安排的日子也会出现在结果里（<see cref="MealDaySummary.IsEmpty"/> 为 true），
    /// 这样界面才能指出哪些天漏记了。
    ///
    /// 区间天数有上限（见 <see cref="MealService.MaxRangeDays"/>）：
    /// 「全部」模式绝不能真的从 1900 年一天天铺到今天，那是四万多天。
    /// </summary>
    Task<MealRangeSummary> GetRangeAsync(
        string userId,
        int childId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default);

    /// <summary>这个小孩最早一条饮食安排的日期；一条都没有时返回 null。</summary>
    Task<DateOnly?> GetEarliestMealDateAsync(string userId, int childId, CancellationToken ct = default);

    Task<MealWithState?> FindAsync(string userId, int mealId, CancellationToken ct = default);

    Task<MealPlan> AddAsync(string userId, int childId, MealPlan meal, CancellationToken ct = default);

    /// <summary>
    /// 批量写入（AI 推荐落地用）。
    /// 强过敏 / 过敏性休克命中的餐**一律拒绝**，不会替家长「代勾」风险确认——
    /// 模型生成的东西没人逐条看过，自动确认等于把最危险的那一类交给概率。
    /// </summary>
    Task<MealImportResult> ImportAsync(
        string userId,
        int childId,
        IEnumerable<MealPlan> meals,
        CancellationToken ct = default);

    Task UpdateAsync(string userId, MealPlan meal, CancellationToken ct = default);

    Task DeleteAsync(string userId, int mealId, CancellationToken ct = default);

    /// <summary>确认吃完。</summary>
    Task MarkEatenAsync(
        string userId,
        int mealId,
        string? finishNote = null,
        AppetiteLevel appetite = AppetiteLevel.Unknown,
        CancellationToken ct = default);

    /// <summary>标记跳过（临时不吃了）。</summary>
    Task MarkSkippedAsync(string userId, int mealId, CancellationToken ct = default);

    /// <summary>撤销确认，回到“待确认”。</summary>
    Task ReopenAsync(string userId, int mealId, CancellationToken ct = default);

    /// <summary>单独检查一份菜单是否命中小孩的过敏原（编辑时实时提示用）。</summary>
    Task<IReadOnlyList<AllergyMatch>> CheckAllergyAsync(
        string userId,
        int childId,
        string? menu,
        string? foods,
        CancellationToken ct = default);

    /// <summary>按历史习惯给一份默认饭点；不定时加餐返回 null。</summary>
    Task<TimeOnly?> GetSuggestedTimeAsync(string userId, int childId, MealType mealType, CancellationToken ct = default);
}

/// <summary>
/// 饮食安排服务。“是否吃完”由 <see cref="MealStatusCalculator"/> 依据系统时间实时推算，
/// 这里负责把数据库里的安排和推算结果组合起来，并处理确认动作与过敏校验。
/// </summary>
public sealed class MealService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IChildService childService,
    MealStatusCalculator statusCalculator) : IMealService
{
    /// <summary>
    /// 「回顾」区间一次最多铺多少天。
    ///
    /// 这个上限不是洁癖：区间是按天展开成 <see cref="MealDaySummary"/> 的，
    /// 而「漏记提醒」又给每个空白天渲染一个 chip。之前「全部」模式固定从 1900-01-01 起算，
    /// 等于一次渲染四万多个组件，浏览器标签页会直接被撑爆。
    /// 400 天 ≈ 一年多的回顾，对家用场景足够，也留足了安全余量。
    /// </summary>
    public const int MaxRangeDays = 400;

    /// <summary>各餐次的默认饭点，用于新增安排时预填。</summary>
    private static readonly Dictionary<MealType, TimeOnly> DefaultTimes = new()
    {
        [MealType.Breakfast] = new TimeOnly(7, 30),
        [MealType.Lunch] = new TimeOnly(12, 0),
        [MealType.Dinner] = new TimeOnly(18, 0),
        [MealType.Snack] = new TimeOnly(15, 30),
        [MealType.Treat] = new TimeOnly(10, 0),
    };

    public async Task<MealDaySummary> GetDayAsync(
        string userId,
        int childId,
        DateOnly date,
        CancellationToken ct = default)
    {
        var child = await childService.RequireAsync(childId, userId, ct);
        var allergens = await childService.ListAllergensAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var meals = await db.MealPlans
            .AsNoTracking()
            .Where(m => m.ChildId == childId && m.Date == date)
            .OrderBy(m => m.PlannedTime == null)
            .ThenBy(m => m.PlannedTime)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var items = meals.Select(m => ToState(m, allergens)).ToList();

        return new MealDaySummary(date, items);
    }

    public async Task<MealRangeSummary> GetRangeAsync(
        string userId,
        int childId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var child = await childService.RequireAsync(childId, userId, ct);
        var allergens = await childService.ListAllergensAsync(childId, userId, ct);

        // 顺手兼容传反的区间，免得界面算错时整段查不到数据。
        if (from > to)
        {
            (from, to) = (to, from);
        }

        // 硬上限。就算调用方传了 1900-01-01，这里也只铺最近的 MaxRangeDays 天。
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            from = to.AddDays(-(MaxRangeDays - 1));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var meals = await db.MealPlans
            .AsNoTracking()
            .Where(m => m.ChildId == childId && m.Date >= from && m.Date <= to)
            .OrderBy(m => m.Date)
            .ThenBy(m => m.PlannedTime == null)
            .ThenBy(m => m.PlannedTime)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var byDate = meals
            .GroupBy(m => m.Date)
            .ToDictionary(g => g.Key, g => g.Select(m => ToState(m, allergens)).ToList());

        var days = new List<MealDaySummary>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            days.Add(new MealDaySummary(
                date,
                byDate.TryGetValue(date, out var items) ? items : []));
        }

        return new MealRangeSummary(from, to, days);
    }

    public async Task<DateOnly?> GetEarliestMealDateAsync(
        string userId,
        int childId,
        CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dates = await db.MealPlans
            .AsNoTracking()
            .Where(m => m.ChildId == childId)
            .Select(m => (DateOnly?)m.Date)
            .OrderBy(d => d)
            .FirstOrDefaultAsync(ct);

        return dates;
    }

    public async Task<MealWithState?> FindAsync(string userId, int mealId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.MealPlans
            .AsNoTracking()
            .Include(m => m.Child)
            .FirstOrDefaultAsync(m => m.Id == mealId && m.Child!.OwnerId == userId, ct);

        if (entity?.Child is null)
        {
            return null;
        }

        var allergens = await db.ChildAllergens
            .AsNoTracking()
            .Where(a => a.ChildId == entity.ChildId)
            .ToListAsync(ct);

        return ToState(entity, allergens);
    }

    public async Task<MealPlan> AddAsync(string userId, int childId, MealPlan meal, CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);
        var allergens = await childService.ListAllergensAsync(childId, userId, ct);
        Validate(meal);

        var matches = AllergyMatcher.FindMatches(allergens, meal.Menu, meal.Foods);
        GuardAllergy(matches, meal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = new MealPlan
        {
            ChildId = childId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.Now,
        };
        ApplyEditableFields(entity, meal);

        db.MealPlans.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task<MealImportResult> ImportAsync(
        string userId,
        int childId,
        IEnumerable<MealPlan> meals,
        CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);
        var allergens = await childService.ListAllergensAsync(childId, userId, ct);

        var created = new List<MealPlan>();
        var rejected = new List<MealImportRejection>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 同一天同餐次只留一条：模型偶尔会把同一顿写两遍。
        var seen = new HashSet<(DateOnly Date, MealType Type)>();

        foreach (var meal in meals)
        {
            if (string.IsNullOrWhiteSpace(meal.Menu))
            {
                rejected.Add(new MealImportRejection(meal, "没有菜单内容", []));
                continue;
            }

            if (!seen.Add((meal.Date, meal.MealType)))
            {
                rejected.Add(new MealImportRejection(meal, "同一天同一餐次重复了", []));
                continue;
            }

            var matches = AllergyMatcher.FindMatches(allergens, meal.Menu, meal.Foods);
            var max = AllergyMatcher.MaxSeverity(matches);

            if (max is { } severity && severity >= AllergySeverity.Severe)
            {
                // 把命中的具体过敏原写进理由：「含强过敏级过敏原」这种话
                // 家长看完还是不知道该改哪儿，点名「含花生、牛奶」才可以直接照着改。
                var names = string.Join('、', matches
                    .Where(m => m.Severity >= AllergySeverity.Severe)
                    .Select(m => m.Allergen)
                    .Distinct());

                rejected.Add(new MealImportRejection(
                    meal,
                    $"含{severity.ToChinese()}级过敏原（{names}），AI 推荐不会替你确认风险，已跳过",
                    matches));
                continue;
            }

            var entity = new MealPlan
            {
                ChildId = childId,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.Now,
                CreatedFromAiSuggestion = true,
            };

            ApplyEditableFields(entity, meal);

            // 中等及以下：自动记录「这一餐涉及过敏原」，但不当作已经确认风险。
            // 也就是说它仍然会在饮食页以红字出现，等家长自己去看、自己勾。
            entity.AllergyWarningAcknowledged = false;
            entity.AllergyAcknowledgedMaxSeverity = null;
            entity.AllergyAcknowledgedAt = null;

            db.MealPlans.Add(entity);
            created.Add(entity);
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new MealImportResult(created, rejected);
    }

    public async Task UpdateAsync(string userId, MealPlan meal, CancellationToken ct = default)
    {
        Validate(meal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.MealPlans
            .Include(m => m.Child)
            .FirstOrDefaultAsync(m => m.Id == meal.Id && m.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(meal.ChildId);

        var allergens = await db.ChildAllergens
            .AsNoTracking()
            .Where(a => a.ChildId == entity.ChildId)
            .ToListAsync(ct);

        var matches = AllergyMatcher.FindMatches(allergens, meal.Menu, meal.Foods);
        GuardAllergy(matches, meal);

        ApplyEditableFields(entity, meal);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int mealId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.MealPlans
            .FirstOrDefaultAsync(m => m.Id == mealId && m.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        db.MealPlans.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkEatenAsync(
        string userId,
        int mealId,
        string? finishNote = null,
        AppetiteLevel appetite = AppetiteLevel.Unknown,
        CancellationToken ct = default)
        => await SetStatusAsync(userId, mealId, MealStatus.Eaten, finishNote, appetite, ct);

    public async Task MarkSkippedAsync(string userId, int mealId, CancellationToken ct = default)
        => await SetStatusAsync(userId, mealId, MealStatus.Skipped, null, AppetiteLevel.Unknown, ct);

    public async Task ReopenAsync(string userId, int mealId, CancellationToken ct = default)
        => await SetStatusAsync(userId, mealId, MealStatus.Planned, null, AppetiteLevel.Unknown, ct);

    public async Task<IReadOnlyList<AllergyMatch>> CheckAllergyAsync(
        string userId,
        int childId,
        string? menu,
        string? foods,
        CancellationToken ct = default)
    {
        // RequireAsync 同时完成了归属校验。
        await childService.RequireAsync(childId, userId, ct);
        var allergens = await childService.ListAllergensAsync(childId, userId, ct);
        return AllergyMatcher.FindMatches(allergens, menu, foods);
    }

    public async Task<TimeOnly?> GetSuggestedTimeAsync(
        string userId,
        int childId,
        MealType mealType,
        CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 优先沿用这个小孩以前用过的饭点，让安排保持一致。
        var previous = await db.MealPlans
            .AsNoTracking()
            .Where(m => m.ChildId == childId && m.MealType == mealType && m.PlannedTime != null)
            .OrderByDescending(m => m.Date)
            .Select(m => m.PlannedTime)
            .FirstOrDefaultAsync(ct);

        if (previous is not null)
        {
            return previous;
        }

        // 加餐没有「建议饭点」这回事：点心 / 零食本来就不定时，
        // 预填一个 15:30 反而会让人以为要按点喂，还会把「已过饭点」的推算引进来。
        // 三餐仍然给默认值，减少手工输入。
        return mealType.IsFixedMeal()
            ? DefaultTimes.GetValueOrDefault(mealType, new TimeOnly(8, 0))
            : null;
    }

    /// <summary>
    /// 过敏拦截。等级决定力度：
    /// 不耐受/轻微 → 放行（界面自己用小字提示）；
    /// 中等 → 必须勾选「我知道风险」；
    /// 强过敏/休克 → 勾选之外，客户端还要把看到的等级回填回来（等于「再点一次确认」）。
    /// </summary>
    private static void GuardAllergy(IReadOnlyList<AllergyMatch> matches, MealPlan meal)
    {
        if (AllergyMatcher.MaxSeverity(matches) is not { } severity)
        {
            // 没有命中就把确认痕迹清掉，避免旧数据在菜单改了以后还挂着「已确认风险」。
            meal.AllergyWarningAcknowledged = false;
            meal.AllergyAcknowledgedMaxSeverity = null;
            meal.AllergyAcknowledgedAt = null;
            return;
        }

        var action = severity.ToRiskAction();
        if (action == AllergyRiskAction.Notice)
        {
            // 只提示，不拦。但也别记成「已确认风险」，否则升级等级后会被当成确认过。
            meal.AllergyWarningAcknowledged = false;
            meal.AllergyAcknowledgedMaxSeverity = null;
            meal.AllergyAcknowledgedAt = null;
            return;
        }

        if (!meal.AllergyWarningAcknowledged)
        {
            throw new AllergyConflictException(matches, severity, action);
        }

        if (action == AllergyRiskAction.DoubleConfirm && meal.AllergyAcknowledgedMaxSeverity != severity)
        {
            throw new AllergyConflictException(matches, severity, action, requiresExplicitReconfirmation: true);
        }

        // 二次确认这一关过掉之后，把「确认时看到的是哪一级」统一钳到服务端算出来的等级。
        //
        // 两个作用：
        // ① 客户端偷懒没回传等级时，事后仍然能看出等级被调高过（否则永远是 null，查不出来）；
        // ② 客户端就算回传一个更高的等级，也不能让这餐以后免于「等级被调高」的提示。
        meal.AllergyAcknowledgedMaxSeverity = severity;
        meal.AllergyAcknowledgedAt ??= DateTimeOffset.Now;
    }

    private async Task SetStatusAsync(
        string userId,
        int mealId,
        MealStatus status,
        string? finishNote,
        AppetiteLevel appetite,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.MealPlans
            .FirstOrDefaultAsync(m => m.Id == mealId && m.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        entity.Status = status;
        entity.EatenAt = status == MealStatus.Eaten ? DateTimeOffset.Now : null;
        entity.UpdatedAt = DateTimeOffset.Now;

        if (status == MealStatus.Eaten)
        {
            entity.FinishNote = string.IsNullOrWhiteSpace(finishNote) ? null : finishNote.Trim();
            entity.Appetite = appetite;
        }
        else if (status == MealStatus.Planned)
        {
            entity.FinishNote = null;
            entity.Appetite = AppetiteLevel.Unknown;
        }

        await db.SaveChangesAsync(ct);
    }

    private MealWithState ToState(MealPlan meal, IReadOnlyList<ChildAllergen> allergens)
    {
        var state = statusCalculator.Resolve(meal);

        // 无条件算一遍命中项：“这一餐涉不涉及过敏原”是客观事实，
        // 回顾用的界面（餐饮记录）需要它，哪怕家长安排时已经确认过风险。
        var matches = AllergyMatcher.FindMatches(allergens, meal.Menu, meal.Foods);

        return new MealWithState(meal, state, statusCalculator.Describe(meal), matches);
    }

    private static void ApplyEditableFields(MealPlan target, MealPlan source)
    {
        target.Date = source.Date;
        target.MealType = source.MealType;
        target.PlannedTime = source.PlannedTime;
        target.Menu = source.Menu.Trim();
        target.Foods = Clean(source.Foods);
        target.EstimatedCalories = source.EstimatedCalories;
        target.Notes = Clean(source.Notes);
        target.AllergyWarningAcknowledged = source.AllergyWarningAcknowledged;
        target.AllergyAcknowledgedMaxSeverity = source.AllergyWarningAcknowledged
            ? source.AllergyAcknowledgedMaxSeverity
            : null;
        target.AllergyAcknowledgedAt = source.AllergyWarningAcknowledged
            ? source.AllergyAcknowledgedAt ?? DateTimeOffset.Now
            : null;
        target.CreatedFromAiSuggestion = source.CreatedFromAiSuggestion;
        target.UpdatedAt = DateTimeOffset.Now;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(MealPlan meal)
    {
        if (string.IsNullOrWhiteSpace(meal.Menu))
        {
            throw new ArgumentException("请填写这一餐吃什么。", nameof(meal));
        }

        if (meal.MealType.IsFixedMeal() && meal.PlannedTime is null)
        {
            throw new ArgumentException("早餐 / 午餐 / 晚餐需要有饭点；点心、零食可以不定时。", nameof(meal));
        }
    }
}
