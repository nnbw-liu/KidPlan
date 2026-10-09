using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// 饮食安排从「三餐」改成「固定三餐 + 不定时多顿点心零食」之后的行为。
///
/// 这块改动最容易被忽略的一点是：加餐没有饭点，
/// 而「有没有过饭点」是靠饭点算出来的——所以无饭点的加餐必须**永远不会**变成
/// 「已过饭点待确认」，否则家长会被一堆莫名其妙的红字淹没。
/// </summary>
public class MealTypeSplitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 15, 0, TimeSpan.FromHours(8));
    private static readonly DateOnly Today = new(2026, 10, 4);

    private static MealPlan Meal(MealType type, TimeOnly? time, string menu)
        => new() { Date = Today, MealType = type, PlannedTime = time, Menu = menu };

    [Fact]
    public void FixedMealTypes_AreExactlyTheThreeMainMeals()
    {
        Assert.Equal(
            [MealType.Breakfast, MealType.Lunch, MealType.Dinner],
            EnumDisplay.FixedMealTypes);

        Assert.Equal(
            [MealType.Snack, MealType.Treat],
            EnumDisplay.AdHocMealTypes);

        Assert.True(MealType.Breakfast.IsFixedMeal());
        Assert.True(MealType.Dinner.IsFixedMeal());
        Assert.False(MealType.Snack.IsFixedMeal());
        Assert.False(MealType.Treat.IsFixedMeal());
    }

    [Fact]
    public async Task Add_RejectsFixedMealWithoutTime()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 三餐没有饭点就没法算状态，属于填错了，必须明确拒绝。
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => meals.AddAsync(userId, childId, Meal(MealType.Lunch, null, "番茄面")));

        Assert.Contains("饭点", ex.Message);
    }

    [Theory]
    [InlineData(MealType.Snack)]
    [InlineData(MealType.Treat)]
    public async Task Add_AcceptsAdHocMealWithoutTime(MealType type)
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var saved = await meals.AddAsync(userId, childId, Meal(type, null, "一小把葡萄干"));

        Assert.Null(saved.PlannedTime);

        var day = await meals.GetDayAsync(userId, childId, Today);
        var item = Assert.Single(day.Meals);

        // 「没定时间，随时都可以」——而不是「已过饭点」。
        Assert.Equal(MealTimeState.Unscheduled, item.State);
        Assert.False(item.NeedsConfirmation);
        Assert.Equal(0, day.OverdueCount);
        Assert.Equal(1, day.UnscheduledCount);
    }

    [Fact]
    public async Task GetDay_SeparatesFixedMealsFromAdHocMeals()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        await meals.AddAsync(userId, childId, Meal(MealType.Breakfast, new TimeOnly(7, 30), "小米粥"));
        await meals.AddAsync(userId, childId, Meal(MealType.Lunch, new TimeOnly(12, 0), "番茄面"));
        await meals.AddAsync(userId, childId, Meal(MealType.Snack, new TimeOnly(15, 30), "酸奶"));
        await meals.AddAsync(userId, childId, Meal(MealType.Snack, null, "香蕉"));
        await meals.AddAsync(userId, childId, Meal(MealType.Treat, null, "两块饼干"));

        var day = await meals.GetDayAsync(userId, childId, Today);

        Assert.Equal(5, day.TotalCount);
        Assert.Equal(2, day.FixedMealCount);
        Assert.Equal(3, day.AdHocMealCount);

        // 现在 12:15：早餐 7:30 早已过点 → 「待确认」；午餐 12:00 正在饭点上 → 「用餐中」。
        Assert.Equal(1, day.OverdueCount);
        Assert.Equal(1, day.ServingCount);

        // 「还没确认」= 定了饭点但既没吃完也没跳过的那几顿：
        // 早餐（过点）+ 午餐（用餐中）+ 定了 15:30 的点心，一共三顿。
        // 两顿没定时间的加餐不在里面——它们本来就不催。
        Assert.Equal(3, day.UnconfirmedCount);

        Assert.Equal(0, day.FixedEatenCount);
        Assert.Equal(0, day.AdHocEatenCount);
    }

    [Fact]
    public async Task Summing_UntimedMealsNeverCountsAsMissingOrOverdue()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 昨天只记了一顿点心：这不算「这天记过了」，但也不该算成「过饭点没确认」。
        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today.AddDays(-1),
            MealType = MealType.Treat,
            PlannedTime = null,
            Menu = "一根奶酪棒",
        });

        var range = await meals.GetRangeAsync(userId, childId, Today.AddDays(-1), Today);
        var yesterday = range.Days.Single(d => d.Date == Today.AddDays(-1));

        Assert.False(yesterday.HasFixedMeals);
        Assert.Equal(1, yesterday.AdHocMealCount);
        Assert.Equal(0, yesterday.OverdueCount);
        Assert.Equal(1, yesterday.UnscheduledCount);

        // 两天都没有固定三餐，所以两天都算「漏记」——只记了加餐不算这天记过了。
        Assert.Equal(2, range.MissingDayCount);
        Assert.Equal(0, range.FixedMeals);
        Assert.Equal(1, range.AdHocMeals);
    }

    [Fact]
    public async Task GetRange_ClampsHugeRanges()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 回归测试：以前「全部」模式会传 1900-01-01 进来，
        // 于是这里会materialize 四万多天的对象，接着界面为每个空白天渲染一个组件，
        // 浏览器标签页直接被撑爆（那次的「闪退」就是这么来的）。
        var range = await meals.GetRangeAsync(userId, childId, new DateOnly(1900, 1, 1), Today);

        Assert.InRange(range.Days.Count, 1, MealService.MaxRangeDays);
        Assert.Equal(Today, range.To);

        // 上界往前推 MaxRangeDays-1 天，两端都含。
        Assert.Equal(Today.AddDays(-(MealService.MaxRangeDays - 1)), range.From);
    }

    [Fact]
    public async Task GetEarliestMealDate_ReturnsNullWhenNothingPlanned()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        Assert.Null(await meals.GetEarliestMealDateAsync(userId, childId));

        await meals.AddAsync(userId, childId, Meal(MealType.Snack, null, "苹果"));
        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = Today.AddDays(-30),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "面条",
        });

        Assert.Equal(Today.AddDays(-30), await meals.GetEarliestMealDateAsync(userId, childId));
    }

    [Fact]
    public async Task GetSuggestedTime_ReturnsNullForAdHocMeals()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        // 加餐默认不定时：给个「默认 15:30」反而会让人以为要按点喂。
        Assert.Null(await meals.GetSuggestedTimeAsync(userId, childId, MealType.Snack));
        Assert.Null(await meals.GetSuggestedTimeAsync(userId, childId, MealType.Treat));

        // 三餐仍有默认值。
        Assert.Equal(new TimeOnly(7, 30), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Breakfast));
        Assert.Equal(new TimeOnly(12, 0), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Lunch));
        Assert.Equal(new TimeOnly(18, 0), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Dinner));
    }

    [Fact]
    public async Task GetSuggestedTime_ReusesTheLastTimeUsedForThatMealType()
    {
        await using var host = new TestHost(Now);
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        await meals.AddAsync(userId, childId, Meal(MealType.Snack, new TimeOnly(16, 5), "酸奶"));

        // 家长之前把点心定在 16:05，之后就沿用它，而不是回到默认值。
        Assert.Equal(new TimeOnly(16, 5), await meals.GetSuggestedTimeAsync(userId, childId, MealType.Snack));
    }
}
