using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>身体记录 + 成长曲线的组装逻辑。</summary>
public class GrowthServiceTests
{
    /// <summary>
    /// 用与 GrowthService 内部一致的偏移构造时间：
    /// 那里的日期区间比较用的是 DateTimeOffset.Now.Offset，这样边界才不会差一小时。
    /// </summary>
    private static DateTimeOffset At(int year, int month, int day, int hour = 9, int minute = 0)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), DateTimeOffset.Now.Offset);

    private static async Task<(TestHost Host, string UserId, int ChildId, IGrowthService Growth)> ArrangeAsync(
        string? allergicFoods = null)
    {
        var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods, new DateOnly(2023, 6, 1));

        var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();

        return (host, userId, childId, growth);
    }

    [Fact]
    public async Task BuildChart_OrdersByTime_AndSkipsRecordsWithoutMeasurements()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        // 故意乱序插入，验证输出一定按记录时间排序。
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90.5m, WeightKg = 13.2m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 1), HeightCm = 89.0m, WeightKg = 12.8m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 20), Symptoms = "轻微咳嗽" });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 7, 1), WeightKg = 12.4m });

        var chart = await growth.BuildChartAsync(userId, childId);

        Assert.Equal(new DateOnly(2023, 6, 1), chart.BirthDate);

        // 只有症状的那条不进曲线。
        Assert.Equal(3, chart.Points.Count);
        Assert.Equal(new[] { "2026-07-01", "2026-08-01", "2026-09-01" }, chart.Points.Select(p => p.Label));

        Assert.True(chart.Has(GrowthMetric.Height));
        Assert.True(chart.Has(GrowthMetric.Weight));
        Assert.True(chart.Has(GrowthMetric.Bmi));
        Assert.False(chart.IsEmpty);

        // 2023-06-01 → 2026-07-01 是 37 个月。
        Assert.Equal(37, chart.Points[0].AgeMonths);
        Assert.Equal(39, chart.Points[2].AgeMonths);
    }

    [Fact]
    public async Task BuildChart_KeepsPointWhenOnlySomeMetricsAreMissing()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90.5m });

        var chart = await growth.BuildChartAsync(userId, childId);

        var point = Assert.Single(chart.Points);
        Assert.Equal(90.5, point.HeightCm);
        Assert.Null(point.WeightKg);
        Assert.Null(point.Bmi);
        Assert.True(chart.Has(GrowthMetric.Height));
        Assert.False(chart.Has(GrowthMetric.Weight));
    }

    [Fact]
    public async Task BuildChart_ComputesBmiFromHeightAndWeight()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 9, 1),
            HeightCm = 100m,
            WeightKg = 16m,
        });

        var point = Assert.Single((await growth.BuildChartAsync(userId, childId)).Points);

        // 16 / (1.00 ^ 2) = 16
        Assert.Equal(16d, point.Bmi);
    }

    [Fact]
    public async Task BuildChart_FiltersByDateRange_Inclusively()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 7, 1), HeightCm = 87m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 1), HeightCm = 89m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 31, 23, 30), HeightCm = 89.5m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90.5m });

        var chart = await growth.BuildChartAsync(userId, childId, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null);

        Assert.Equal(2, chart.Points.Count);
        Assert.Equal(new[] { "2026-08-01", "2026-08-31" }, chart.Points.Select(p => p.Label));
    }

    [Fact]
    public async Task BuildChart_IsEmptyForRecordWithNoPlottableValue()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), Symptoms = "轻微咳嗽" });

        var chart = await growth.BuildChartAsync(userId, childId);

        Assert.Empty(chart.Points);
        Assert.True(chart.IsEmpty);
        Assert.False(chart.Has(GrowthMetric.Height));
    }

    [Fact]
    public async Task BuildChart_KeepsTemperatureOnlyRecord()
    {
        // 体温、睡眠、运动是新增的可选指标，所以「只有体温」的记录现在是要画出来的，
        // 这跟旧行为不同（旧版只认身高体重，体温被当成画不出来的记录丢掉）。
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), TemperatureC = 37.2m });

        var chart = await growth.BuildChartAsync(userId, childId);

        Assert.Single(chart.Points);
        Assert.True(chart.Has(GrowthMetric.Temperature));
        Assert.False(chart.Has(GrowthMetric.Height));
    }

    [Fact]
    public async Task BuildChart_FiltersByRecordKind()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 1), HeightCm = 89m });
        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Period,
            RecordedAt = At(2026, 9, 1),
            PeriodStart = new DateOnly(2026, 8, 25),
            PeriodEnd = new DateOnly(2026, 9, 1),
            HeightCm = 90m,
        });

        var all = await growth.BuildChartAsync(userId, childId, null, null, null);
        var points = await growth.BuildChartAsync(userId, childId, null, null, RecordKind.Point);
        var periods = await growth.BuildChartAsync(userId, childId, null, null, RecordKind.Period);

        Assert.Equal(2, all.Points.Count);
        Assert.Equal(RecordKind.Point, Assert.Single(points.Points).Kind);
        Assert.Equal(RecordKind.Period, Assert.Single(periods.Points).Kind);
    }

    [Fact]
    public async Task ChartSeries_ExposesOneLinePerMetric()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 8, 1),
            HeightCm = 89m,
            WeightKg = 12.8m,
            SleepHours = 11.5m,
        });

        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 9, 1),
            HeightCm = 90.5m,
            WeightKg = 13.2m,
            SleepHours = 10.5m,
        });

        var chart = await growth.BuildChartAsync(userId, childId);

        var (heightLabels, heightValues) = chart.Series(GrowthMetric.Height);
        Assert.Equal(new[] { "2026-08-01", "2026-09-01" }, heightLabels);
        Assert.Equal(new[] { 89d, 90.5d }, heightValues);

        // 睡眠只在两条记录里都有值，所以两条点都在。
        Assert.Equal(new[] { 11.5d, 10.5d }, chart.Series(GrowthMetric.SleepHours).Values);

        // 体温一条都没有，序列为空——界面据此显示「（无数据）」。
        Assert.Empty(chart.Series(GrowthMetric.Temperature).Labels);
    }

    [Fact]
    public async Task BuildSummary_ReturnsNullWithoutRecords()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        Assert.Null(await growth.BuildSummaryAsync(userId, childId));
    }

    [Fact]
    public async Task BuildSummary_ReturnsLatestValuesAndDeltas()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 7, 1), HeightCm = 87.0m, WeightKg = 12.1m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 1), HeightCm = 89.0m, WeightKg = 12.8m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90.5m, WeightKg = 13.2m, TemperatureC = 36.6m });

        var summary = await growth.BuildSummaryAsync(userId, childId);

        Assert.NotNull(summary);
        Assert.Equal(new DateOnly(2026, 9, 1), summary.LatestDate);
        Assert.Equal(90.5m, summary.HeightCm);
        Assert.Equal(13.2m, summary.WeightKg);
        Assert.Equal(36.6m, summary.TemperatureC);
        Assert.Equal(1.5m, summary.HeightDelta);
        Assert.Equal(0.4m, summary.WeightDelta);
        Assert.Equal(3, summary.RecordCount);
    }

    [Fact]
    public async Task BuildSummary_SkipsSymptomOnlyRecordsWhenLookingForPreviousMeasurement()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 7, 1), HeightCm = 87.0m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90.5m });
        // 最新一条只有体温：它是最新记录，但算增长量时要跳过它去找上一条有身高的。
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 2), TemperatureC = 38.1m });

        var summary = await growth.BuildSummaryAsync(userId, childId);

        Assert.Equal(new DateOnly(2026, 9, 2), summary!.LatestDate);
        Assert.Null(summary.HeightCm);
        Assert.Null(summary.HeightDelta);
        Assert.Equal(3, summary.RecordCount);
    }

    [Fact]
    public async Task PeriodRecord_SwapsReversedDates_AndMovesRecordedAtToPeriodEnd()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        var saved = await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Period,
            RecordedAt = At(2026, 9, 10),
            PeriodStart = new DateOnly(2026, 9, 8),
            PeriodEnd = new DateOnly(2026, 9, 5),   // 故意反着填
            WeightKg = 13.0m,
        });

        Assert.Equal(new DateOnly(2026, 9, 5), saved.PeriodStart);
        Assert.Equal(new DateOnly(2026, 9, 8), saved.PeriodEnd);
        Assert.Equal(new DateOnly(2026, 9, 8), DateOnly.FromDateTime(saved.RecordedAt.DateTime));
    }

    [Fact]
    public async Task PeriodRecord_WithOnlyOneDate_FillsTheOtherSide()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        var saved = await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Period,
            RecordedAt = At(2026, 9, 10),
            PeriodEnd = new DateOnly(2026, 9, 7),
            WeightKg = 13.0m,
        });

        Assert.Equal(new DateOnly(2026, 9, 7), saved.PeriodStart);
        Assert.Equal(new DateOnly(2026, 9, 7), saved.PeriodEnd);
    }

    [Fact]
    public async Task PeriodRecord_WithoutAnyDate_IsRejected()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await Assert.ThrowsAsync<ArgumentException>(() => growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Period,
            RecordedAt = At(2026, 9, 10),
            WeightKg = 13.0m,
        }));
    }

    [Fact]
    public async Task PointRecord_ClearsPeriodFields()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        var saved = await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 9, 1),
            HeightCm = 90m,
            PeriodStart = new DateOnly(2026, 8, 1),
            PeriodEnd = new DateOnly(2026, 8, 31),
        });

        Assert.Null(saved.PeriodStart);
        Assert.Null(saved.PeriodEnd);
    }

    [Fact]
    public async Task List_ReturnsNewestFirst()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 7, 1), HeightCm = 87m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 8, 1), HeightCm = 89m });
        await growth.AddAsync(userId, childId, new GrowthRecord { Kind = RecordKind.Point, RecordedAt = At(2026, 9, 1), HeightCm = 90m });

        var records = await growth.ListAsync(userId, childId);

        Assert.Equal(3, records.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), DateOnly.FromDateTime(records[0].RecordedAt.DateTime));
        Assert.Equal(new DateOnly(2026, 7, 1), DateOnly.FromDateTime(records[2].RecordedAt.DateTime));
    }

    [Fact]
    public async Task Update_ChangesEditableFields_AndDeleteRemovesTheRecord()
    {
        var (host, userId, childId, growth) = await ArrangeAsync();
        await using var _ = host;

        var saved = await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 9, 1),
            HeightCm = 90m,
            Notes = "社区医院量的",
        });

        saved.HeightCm = 91.5m;
        saved.Notes = "  换了一家医院  ";
        await growth.UpdateAsync(userId, saved);

        var reloaded = await growth.FindAsync(userId, saved.Id);
        Assert.Equal(91.5m, reloaded!.HeightCm);
        Assert.Equal("换了一家医院", reloaded.Notes);

        await growth.DeleteAsync(userId, saved.Id);
        Assert.Null(await growth.FindAsync(userId, saved.Id));
    }

    [Fact]
    public async Task ChildOverview_CombinesGrowthAndTodaysMeals()
    {
        // 首页概览用的是“真实的今天”，所以这里让固定时间也落在今天，
        // 只把“几点”钉死，避免测试结果随运行日期变化。
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 15)), TimeSpan.FromHours(8));

        await using var host = new TestHost(now);

        var userId = await host.CreateUserAsync("parent@example.com");
        var birthDate = new DateOnly(2023, 6, 1);
        var childId = await host.CreateChildAsync(userId, "糖糖", birthDate: birthDate);

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();
        var overview = scope.ServiceProvider.GetRequiredService<IChildOverviewService>();

        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = At(2026, 10, 1),
            HeightCm = 92m,
            WeightKg = 13.5m,
        });

        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = today,
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "番茄牛肉面",
            EstimatedCalories = 300,
        });

        var result = Assert.Single(await overview.BuildAllAsync(userId));

        Assert.Equal(childId, result.Child.Id);
        Assert.Equal(92m, result.Growth!.HeightCm);
        Assert.Equal(AgeCalculator.Describe(birthDate, today), result.AgeText);
        Assert.Equal(AgeCalculator.MonthsBetween(birthDate, today), result.AgeMonths);
        Assert.Equal(1, result.TodayMeals.TotalCount);
        Assert.Equal(1, result.TodayMeals.ServingCount);
        Assert.Equal(300, result.TodayMeals.PlannedCalories);
        Assert.Equal(0, result.TodayMeals.EatenCalories);
    }
}
