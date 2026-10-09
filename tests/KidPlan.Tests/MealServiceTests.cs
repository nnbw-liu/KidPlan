using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// 饮食安排：过敏校验 + 根据系统时间推算“是否已经吃完”+ 确认动作。
/// </summary>
public class MealServiceTests
{
    /// <summary>固定的“现在”：2026-10-04 12:15（东八区）。</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 15, 0, TimeSpan.FromHours(8));

    private static readonly DateOnly Today = new(2026, 10, 4);

    private static MealPlan Lunch(int minute = 0, string menu = "番茄鸡蛋面", int hour = 12)
        => new()
        {
            Date = Today,
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(hour, minute),
            Menu = menu,
        };

    [Fact]
    public async Task Add_RejectsMenuThatHitsAnAllergy_WhenNotAcknowledged()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "牛奶、鸡蛋");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var ex = await Assert.ThrowsAsync<AllergyConflictException>(
            () => meals.AddAsync(userId, childId, Lunch(menu: "牛奶燕麦粥 + 蒸蛋")));

        Assert.Equal(2, ex.Matches.Count);
        Assert.Contains(ex.Matches, m => m.Allergen == "牛奶");
        Assert.Contains(ex.Matches, m => m.Allergen == "鸡蛋");

        // 被拒绝的安排不应该落库。
        Assert.Empty((await meals.GetDayAsync(userId, childId, Today)).Meals);
    }

    [Fact]
    public async Task Add_AcceptsMenuThatHitsAnAllergy_WhenAcknowledged()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "牛奶");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var meal = Lunch(menu: "牛奶燕麦粥");
        meal.AllergyWarningAcknowledged = true;

        var saved = await meals.AddAsync(userId, childId, meal);

        Assert.True(saved.Id > 0);
        // 家长确认过风险之后，列表里就不再重复报警。
        var listed = Assert.Single((await meals.GetDayAsync(userId, childId, Today)).Meals);
        Assert.False(listed.HasAllergyRisk);
    }

    [Fact]
    public async Task Add_AcceptsSafeMenu_WithNoAllergyRecorded()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: null);

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch(menu: "番茄牛肉面"));

        Assert.True(saved.Id > 0);
    }

    [Fact]
    public async Task Add_RequiresAMenu()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        await Assert.ThrowsAsync<ArgumentException>(() => meals.AddAsync(userId, childId, Lunch(menu: "   ")));
    }

    [Fact]
    public async Task Update_AlsoChecksAllergy()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "花生");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch(menu: "小米粥"));

        saved.Menu = "小米粥 + 花生米";
        await Assert.ThrowsAsync<AllergyConflictException>(() => meals.UpdateAsync(userId, saved));

        // 原值没有被改掉。
        var reloaded = await meals.FindAsync(userId, saved.Id);
        Assert.Equal("小米粥", reloaded!.Meal.Menu);

        saved.AllergyWarningAcknowledged = true;
        await meals.UpdateAsync(userId, saved);

        var updated = await meals.FindAsync(userId, saved.Id);
        Assert.Equal("小米粥 + 花生米", updated!.Meal.Menu);
    }

    [Fact]
    public async Task GetDay_SortsByPlannedTime_AndDerivesStateFromSystemClock()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 乱序插入：晚餐(18:00) / 午餐(12:00) / 早餐(07:30) / 加餐(15:30)
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Dinner, PlannedTime = new TimeOnly(18, 0), Menu = "米饭 + 清蒸鱼" });
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Lunch, PlannedTime = new TimeOnly(12, 0), Menu = "番茄鸡蛋面" });
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Breakfast, PlannedTime = new TimeOnly(7, 30), Menu = "小米粥" });
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Snack, PlannedTime = new TimeOnly(15, 30), Menu = "苹果" });

        var day = await meals.GetDayAsync(userId, childId, Today);

        Assert.Equal(4, day.TotalCount);
        Assert.Equal(
            new[] { MealType.Breakfast, MealType.Lunch, MealType.Snack, MealType.Dinner },
            day.Meals.Select(m => m.Meal.MealType));

        // 现在是 12:15：早餐早就过了饭点还没确认 → 待确认；午餐正在吃 → 用餐中；加餐和晚餐还没到。
        Assert.Equal(MealTimeState.Overdue, day.Meals[0].State);
        Assert.Equal(MealTimeState.Serving, day.Meals[1].State);
        Assert.Equal(MealTimeState.Upcoming, day.Meals[2].State);
        Assert.Equal(MealTimeState.Upcoming, day.Meals[3].State);

        Assert.Equal(1, day.OverdueCount);
        Assert.Equal(1, day.ServingCount);
        Assert.Equal(2, day.UpcomingCount);
        Assert.Equal(0, day.EatenCount);
    }

    [Fact]
    public async Task GetDay_MarksLeftoverMealsFromYesterdayAsOverdue()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var yesterday = Today.AddDays(-1);
        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = yesterday,
            MealType = MealType.Dinner,
            PlannedTime = new TimeOnly(18, 0),
            Menu = "米饭 + 西兰花",
        });

        var day = await meals.GetDayAsync(userId, childId, yesterday);
        Assert.Equal(MealTimeState.Overdue, Assert.Single(day.Meals).State);
        Assert.Equal("这是过去安排的餐，请确认是否吃过", day.Meals[0].StateText);
    }

    [Fact]
    public async Task MarkEaten_RecordsTimeNoteAndAppetite()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch());

        await meals.MarkEatenAsync(userId, saved.Id, "吃了一碗，蔬菜剩一半", AppetiteLevel.Good);

        var item = Assert.Single((await meals.GetDayAsync(userId, childId, Today)).Meals);
        Assert.Equal(MealTimeState.Eaten, item.State);
        Assert.True(item.IsEaten);
        Assert.Equal("吃了一碗，蔬菜剩一半", item.Meal.FinishNote);
        Assert.Equal(AppetiteLevel.Good, item.Meal.Appetite);
        Assert.NotNull(item.Meal.EatenAt);
        // 确认时间是「真实的当前时间」（DateTimeOffset.Now），不是测试里注入的固定时间，
        // 所以这里只校验文案格式。
        Assert.StartsWith("已于 ", item.StateText);
        Assert.EndsWith("确认吃完", item.StateText);
    }

    [Fact]
    public async Task Reopen_ClearsConfirmation()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch());
        await meals.MarkEatenAsync(userId, saved.Id, "吃完了", AppetiteLevel.Excellent);
        await meals.ReopenAsync(userId, saved.Id);

        var item = Assert.Single((await meals.GetDayAsync(userId, childId, Today)).Meals);
        Assert.Equal(MealStatus.Planned, item.Meal.Status);
        Assert.Null(item.Meal.EatenAt);
        Assert.Null(item.Meal.FinishNote);
        Assert.Equal(AppetiteLevel.Unknown, item.Meal.Appetite);
        // 回到待确认状态后，重新按时间推算（12:00 的饭点、现在 12:15 → 用餐中）。
        Assert.Equal(MealTimeState.Serving, item.State);
    }

    [Fact]
    public async Task MarkSkipped_OverridesTheTimeBasedState()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch());
        await meals.MarkSkippedAsync(userId, saved.Id);

        var item = Assert.Single((await meals.GetDayAsync(userId, childId, Today)).Meals);
        Assert.Equal(MealTimeState.Skipped, item.State);
        Assert.Equal("这一餐已跳过", item.StateText);
        Assert.Equal(1, (await meals.GetDayAsync(userId, childId, Today)).SkippedCount);
    }

    [Fact]
    public async Task Delete_RemovesTheMeal()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Lunch());
        await meals.DeleteAsync(userId, saved.Id);

        Assert.Empty((await meals.GetDayAsync(userId, childId, Today)).Meals);
    }

    [Fact]
    public async Task GetRange_ReturnsEveryDayInTheWindow_GroupedAndSorted()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 2026-10 那一周是从 09-28(周一) 到 10-04(周日)。10-04 是测试里的“今天”。
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Dinner, PlannedTime = new TimeOnly(18, 0), Menu = "米饭 + 清蒸鱼", EstimatedCalories = 400 });
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today, MealType = MealType.Breakfast, PlannedTime = new TimeOnly(7, 30), Menu = "小米粥", EstimatedCalories = 200 });
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today.AddDays(-2), MealType = MealType.Lunch, PlannedTime = new TimeOnly(12, 0), Menu = "番茄鸡蛋面", EstimatedCalories = 350 });

        var range = await meals.GetRangeAsync(userId, childId, Today.AddDays(-6), Today);

        Assert.Equal(Today.AddDays(-6), range.From);
        Assert.Equal(Today, range.To);

        // 一周 7 天都要出现，没有安排的日子也在（靠 IsEmpty 区分），这样界面才能列出漏记的天。
        Assert.Equal(7, range.Days.Count);

        var today = Assert.Single(range.Days, d => d.Date == Today);
        Assert.Equal(
            new[] { MealType.Breakfast, MealType.Dinner },
            today.Meals.Select(m => m.Meal.MealType));
        Assert.False(today.IsEmpty);

        // 10-02 有安排，10-01 和 10-03 没有。
        Assert.False(Assert.Single(range.Days, d => d.Date == Today.AddDays(-2)).IsEmpty);
        Assert.True(Assert.Single(range.Days, d => d.Date == Today.AddDays(-1)).IsEmpty);

        Assert.Equal(2, range.RecordedDayCount);
        Assert.Equal(5, range.MissingDayCount);
        Assert.Equal(3, range.TotalMeals);
        Assert.Equal(950, range.PlannedCalories);
    }

    [Fact]
    public async Task GetRange_TotalsOnlyCountThisWindow()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 窗口外的一餐（更早）不应该被算进来。
        await meals.AddAsync(userId, childId, new MealPlan { Date = Today.AddDays(-10), MealType = MealType.Lunch, PlannedTime = new TimeOnly(12, 0), Menu = "窗口外", EstimatedCalories = 999 });
        var inside = await meals.AddAsync(userId, childId, Lunch(menu: "窗口内", hour: 7));
        await meals.AddAsync(userId, childId, Lunch(menu: "待确认的午餐"));

        await meals.MarkEatenAsync(userId, inside.Id);

        var range = await meals.GetRangeAsync(userId, childId, Today, Today);

        var day = Assert.Single(range.Days);
        Assert.Equal(2, day.TotalCount);
        Assert.Equal(1, day.EatenCount);
        Assert.Equal(1, day.UnconfirmedCount);
        Assert.Equal(2, range.TotalMeals);
        Assert.Equal(1, range.EatenMeals);
        Assert.Equal(0, range.SkippedMeals);
    }

    [Fact]
    public async Task GetRange_CollectsAllergyHits_AndSwapsReversedBounds()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "花生、芒果");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var risky = Lunch(menu: "小米粥 + 花生米");
        risky.AllergyWarningAcknowledged = true;    // 家长确认过风险才存得下去
        await meals.AddAsync(userId, childId, risky);
        await meals.AddAsync(userId, childId, Lunch(hour: 7, menu: "小米粥"));

        // 区间故意传反，也应该被纠正回来。
        var range = await meals.GetRangeAsync(userId, childId, Today.AddDays(2), Today);

        Assert.Equal(Today, range.From);
        Assert.Equal(Today.AddDays(2), range.To);
        Assert.Equal(3, range.Days.Count);

        // 确认过风险的餐不再报警（和饮食页一个口径）……
        Assert.Empty(range.AllergyHits);

        // ……但“这一餐含过敏食物”是事实，回顾时必须看得到。
        var hit = Assert.Single(range.AllergenRiskHits);
        Assert.Equal(Today, hit.Date);
        Assert.Equal("花生", hit.Match.Allergen);
        Assert.Equal(MealType.Lunch, hit.Meal.Meal.MealType);
        Assert.True(hit.Meal.ContainsAllergen);
    }

    [Fact]
    public async Task GetRange_FlagsMealsThatOnlyBecameRiskyAfterTheAllergyWasRecorded()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: null);

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        // 先按“没有已知过敏”安排一餐，那时它是安全的。
        await meals.AddAsync(userId, childId, Lunch(menu: "花生酱吐司"));

        // 后来才发现过敏，把过敏食物补上。等级取「中等」——
        // 这一档是「拦截未确认」的档位，正好用来验证事后补录会不会被标出来。
        await children.AddAllergenAsync(userId, childId, new ChildAllergen
        {
            Name = "花生",
            Kind = AllergenKind.Food,
            Severity = AllergySeverity.Moderate,
        });

        var range = await meals.GetRangeAsync(userId, childId, Today, Today);

        // 这种“事后才变危险”的餐必须出现在未确认警告里。
        var hit = Assert.Single(range.AllergyHits);
        Assert.Equal("花生", hit.Match.Allergen);
        Assert.Equal(Today, hit.Date);

        // 它同时也算“涉及过敏食物”。
        Assert.Single(range.AllergenRiskHits);
        Assert.True(hit.Meal.HasAllergyRisk);
    }

    [Fact]
    public async Task GetRange_FlagsMealsWhoseSeverityWasRaisedAfterTheyWereAcknowledged()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "花生", severity: AllergySeverity.Moderate);

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        // 安排时按「中等」确认过风险。
        var meal = Lunch(menu: "花生酱吐司");
        meal.AllergyWarningAcknowledged = true;
        await meals.AddAsync(userId, childId, meal);

        // 之后复查把等级调成了「强过敏」。
        var allergen = Assert.Single(await children.ListAllergensAsync(childId, userId));
        await children.UpdateAllergenAsync(userId, allergen.Id, new ChildAllergen
        {
            Name = "花生",
            Kind = AllergenKind.Food,
            Severity = AllergySeverity.Severe,
        });

        var day = await meals.GetDayAsync(userId, childId, Today);
        var updated = Assert.Single(day.Meals);

        // 光记住「确认过一次」是发现不了这种情况的，所以这里必须能看出「等级后来被调高了」。
        Assert.True(updated.IsAllergenEscalated);
        Assert.Equal(AllergySeverity.Severe, updated.MaxSeverity);

        // 确认过风险的那一餐不算「未确认警告」，但确实涉及过敏食物。
        var range = await meals.GetRangeAsync(userId, childId, Today, Today);
        Assert.Empty(range.AllergyHits);
        Assert.Single(range.AllergenRiskHits);
    }

    [Fact]
    public async Task GetSuggestedTime_UsesDefault_ThenReusesTheLastUsedTime()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        Assert.Equal(new TimeOnly(7, 30), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Breakfast));
        Assert.Equal(new TimeOnly(12, 0), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Lunch));
        Assert.Equal(new TimeOnly(18, 0), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Dinner));

        // 加餐没有「建议饭点」：点心 / 零食本来就不定时，
        // 预填一个 15:30 会让人以为要按点喂，还会把「已过饭点」的推算引进来。
        Assert.Null(await meals.GetSuggestedTimeAsync(userId, childId, MealType.Snack));
        Assert.Null(await meals.GetSuggestedTimeAsync(userId, childId, MealType.Treat));

        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today,
            MealType = MealType.Breakfast,
            PlannedTime = new TimeOnly(8, 10),
            Menu = "小米粥",
        });

        Assert.Equal(new TimeOnly(8, 10), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Breakfast));
        // 其他餐次不受影响。
        Assert.Equal(new TimeOnly(12, 0), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Lunch));
    }

    [Fact]
    public async Task CheckAllergy_ReportsMatchesWithoutSaving()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "芒果");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var hits = await meals.CheckAllergyAsync(userId, childId, "芒果布丁", null);
        Assert.Single(hits);
        Assert.Equal("芒果", hits[0].Allergen);

        Assert.Empty(await meals.CheckAllergyAsync(userId, childId, "小米粥", "西兰花"));
    }

    [Fact]
    public async Task DaySummary_TotalsCaloriesOfEatenMealsOnly()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var breakfast = await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today,
            MealType = MealType.Breakfast,
            PlannedTime = new TimeOnly(7, 30),
            Menu = "小米粥",
            EstimatedCalories = 200,
        });

        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today,
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "番茄鸡蛋面",
            EstimatedCalories = 350,
        });

        await meals.MarkEatenAsync(userId, breakfast.Id);

        var day = await meals.GetDayAsync(userId, childId, Today);
        Assert.Equal(550, day.PlannedCalories);
        Assert.Equal(200, day.EatenCalories);
        Assert.Equal(1, day.EatenCount);
    }
}
