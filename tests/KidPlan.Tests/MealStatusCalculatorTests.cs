using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.Options;

namespace KidPlan.Tests;

/// <summary>
/// 「根据当前系统时间判断这一餐是否已经吃完」的核心逻辑。
/// 默认窗口：饭点前 30 分钟算用餐中，饭点后 60 分钟之内还算用餐中，超过就是「已过饭点·待确认」。
/// </summary>
public class MealStatusCalculatorTests
{
    private static readonly DateTimeOffset Noon = At(2026, 10, 4, 12, 0);

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static MealStatusCalculator Create(DateTimeOffset? now = null, MealScheduleOptions? options = null)
        => new(Options.Create(options ?? new MealScheduleOptions()),
               new TestTimeProvider(now ?? Noon));

    private static MealPlan Meal(
        int year, int month, int day, int hour, int minute,
        MealStatus status = MealStatus.Planned)
        => new()
        {
            Id = 1,
            ChildId = 1,
            Date = new DateOnly(year, month, day),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(hour, minute),
            Menu = "番茄鸡蛋面",
            Status = status,
        };

    [Fact]
    public void BeforeServingWindow_IsUpcoming()
        => Assert.Equal(MealTimeState.Upcoming, Create().Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 11, 29)));

    [Fact]
    public void AtServingWindowStart_IsAlreadyServing()
        => Assert.Equal(MealTimeState.Serving, Create().Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 11, 30)));

    [Fact]
    public void AtPlannedTime_IsServing()
        => Assert.Equal(MealTimeState.Serving, Create().Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 12, 0)));

    [Fact]
    public void AtOverdueBoundary_IsStillServing()
        => Assert.Equal(MealTimeState.Serving, Create().Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 13, 0)));

    [Fact]
    public void OneMinutePastBoundary_IsOverdue()
        => Assert.Equal(MealTimeState.Overdue, Create().Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 13, 1)));

    [Fact]
    public void Yesterday_IsAlwaysOverdue()
    {
        // 过去的日子不可能再补一句「还没吃」，必须让家长确认掉。
        Assert.Equal(MealTimeState.Overdue, Create().Resolve(Meal(2026, 10, 3, 12, 0), At(2026, 10, 4, 8, 0)));
        Assert.Equal(MealTimeState.Overdue, Create().Resolve(Meal(2026, 10, 3, 23, 30), At(2026, 10, 4, 23, 59)));
    }

    [Fact]
    public void Tomorrow_IsUpcoming()
        => Assert.Equal(MealTimeState.Upcoming, Create().Resolve(Meal(2026, 10, 5, 7, 30), At(2026, 10, 4, 23, 0)));

    [Fact]
    public void ConfirmedEaten_WinsOverTime()
    {
        var eaten = Meal(2026, 10, 3, 12, 0, MealStatus.Eaten);
        Assert.Equal(MealTimeState.Eaten, Create().Resolve(eaten, At(2026, 10, 4, 12, 0)));
    }

    [Fact]
    public void ConfirmedSkipped_WinsOverTime()
    {
        var skipped = Meal(2026, 10, 5, 12, 0, MealStatus.Skipped);
        Assert.Equal(MealTimeState.Skipped, Create().Resolve(skipped, At(2026, 10, 4, 12, 0)));
    }

    [Fact]
    public void ResolveWithoutExplicitNow_UsesInjectedTimeProvider()
    {
        // 注入的「现在」是 12:15，饭点 12:00 → 用餐中
        var calculator = Create(At(2026, 10, 4, 12, 15));

        Assert.Equal(MealTimeState.Serving, calculator.Resolve(Meal(2026, 10, 4, 12, 0)));
    }

    [Fact]
    public void ResultDependsOnWallClockOnly_NotOnOffset()
    {
        // 生产的「现在」来自 TimeProvider.GetLocalNow()，比较的是墙上时钟。
        // 同一组墙上时间、不同偏移，判断结果一致。
        var calculator = Create();

        Assert.Equal(MealTimeState.Serving,
            calculator.Resolve(Meal(2026, 10, 4, 12, 0), new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.FromHours(8))));
        Assert.Equal(MealTimeState.Serving,
            calculator.Resolve(Meal(2026, 10, 4, 12, 0), new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void RespectsConfiguredWindows()
    {
        var options = new MealScheduleOptions { ServingWindowBeforeMinutes = 0, OverdueAfterMinutes = 0 };
        var calculator = Create(options: options);

        Assert.Equal(MealTimeState.Upcoming, calculator.Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 11, 59)));
        Assert.Equal(MealTimeState.Serving, calculator.Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 12, 0)));
        Assert.Equal(MealTimeState.Overdue, calculator.Resolve(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 12, 1)));
    }

    [Fact]
    public void GetOffsetFromPlannedTime_IsSigned()
    {
        var calculator = Create();

        Assert.Equal(TimeSpan.FromMinutes(25),
            calculator.GetOffsetFromPlannedTime(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 12, 25)));
        Assert.Equal(TimeSpan.FromMinutes(-40),
            calculator.GetOffsetFromPlannedTime(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 11, 20)));
    }

    [Fact]
    public void Describe_TellsHowLateAnUnconfirmedMealIs()
        => Assert.Equal("已过饭点 1 小时 25 分钟，还没确认",
            Create().Describe(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 13, 25)));

    [Fact]
    public void Describe_MarksLeftoverMealsFromPastDays()
        => Assert.Equal("这是过去安排的餐，请确认是否吃过",
            Create().Describe(Meal(2026, 10, 3, 12, 0), At(2026, 10, 4, 12, 0)));

    [Fact]
    public void Describe_CountsDownToUpcomingMeal()
        => Assert.Equal("还有 1 小时 到饭点",
            Create().Describe(Meal(2026, 10, 4, 12, 0), At(2026, 10, 4, 11, 0)));

    [Fact]
    public void Describe_ReportsWhenTheMealWasConfirmed()
    {
        var eaten = Meal(2026, 10, 4, 12, 0, MealStatus.Eaten);
        eaten.EatenAt = At(2026, 10, 4, 12, 40);

        Assert.Equal("已于 12:40 确认吃完", Create().Describe(eaten, At(2026, 10, 4, 13, 0)));
    }

    [Fact]
    public void Describe_MarksSkippedMeal()
    {
        var skipped = Meal(2026, 10, 4, 12, 0, MealStatus.Skipped);

        Assert.Equal("这一餐已跳过", Create().Describe(skipped, At(2026, 10, 4, 12, 30)));
    }

    [Fact]
    public void IsEaten_IsTrueOnlyAfterConfirmation()
    {
        var calculator = Create(At(2026, 10, 4, 13, 30));

        // 过了饭点也只是「待确认」，不等于已经吃完。
        Assert.False(calculator.IsEaten(Meal(2026, 10, 4, 12, 0)));
        Assert.True(calculator.IsEaten(Meal(2026, 10, 4, 12, 0, MealStatus.Eaten)));
    }
}
