using KidPlan.Web.Services;

namespace KidPlan.Tests;

public class AgeCalculatorTests
{
    [Fact]
    public void MonthsBetween_CountsOnlyCompletedMonths()
    {
        var birth = new DateOnly(2023, 6, 15);

        Assert.Equal(0, AgeCalculator.MonthsBetween(birth, new DateOnly(2023, 7, 14)));
        Assert.Equal(1, AgeCalculator.MonthsBetween(birth, new DateOnly(2023, 7, 15)));
        Assert.Equal(12, AgeCalculator.MonthsBetween(birth, new DateOnly(2024, 6, 15)));
        Assert.Equal(27, AgeCalculator.MonthsBetween(birth, new DateOnly(2025, 9, 15)));
        Assert.Equal(26, AgeCalculator.MonthsBetween(birth, new DateOnly(2025, 9, 14)));
    }

    [Fact]
    public void MonthsBetween_NeverGoesNegative()
        => Assert.Equal(0, AgeCalculator.MonthsBetween(new DateOnly(2026, 1, 1), new DateOnly(2025, 1, 1)));

    [Fact]
    public void DaysBetween_CountsCalendarDays()
    {
        Assert.Equal(0, AgeCalculator.DaysBetween(new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 4)));
        Assert.Equal(31, AgeCalculator.DaysBetween(new DateOnly(2026, 9, 3), new DateOnly(2026, 10, 4)));
        Assert.Equal(0, AgeCalculator.DaysBetween(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 4)));
    }

    [Theory]
    [InlineData(2026, 10, 4, "0 天")]
    [InlineData(2026, 9, 24, "10 天")]
    [InlineData(2026, 9, 3, "1 个月")]      // 满 31 天，按整月显示
    [InlineData(2026, 5, 4, "5 个月")]
    [InlineData(2025, 10, 4, "1 岁")]
    [InlineData(2024, 7, 4, "2 岁 3 个月")]
    public void Describe_UsesChineseUnits(int year, int month, int day, string expected)
        => Assert.Equal(expected, AgeCalculator.Describe(new DateOnly(year, month, day), new DateOnly(2026, 10, 4)));

    [Fact]
    public void IsPlausibleBirthDate_RejectsFutureAndTooOld()
    {
        var today = new DateOnly(2026, 10, 4);

        Assert.True(AgeCalculator.IsPlausibleBirthDate(today, today));
        Assert.True(AgeCalculator.IsPlausibleBirthDate(new DateOnly(2020, 1, 1), today));
        Assert.True(AgeCalculator.IsPlausibleBirthDate(today.AddYears(-25), today));

        Assert.False(AgeCalculator.IsPlausibleBirthDate(today.AddDays(1), today));
        Assert.False(AgeCalculator.IsPlausibleBirthDate(today.AddYears(-25).AddDays(-1), today));
    }
}
