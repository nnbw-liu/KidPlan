using KidPlan.Web.Models;
using KidPlan.Web.Services;

namespace KidPlan.Tests;

/// <summary>
/// 可选择的曲线指标。
///
/// 这里的东西看起来都是「一张对照表」，但它同时被界面（勾选项）、
/// URL 参数（?metrics=height,weight）和曲线组装三处使用，
/// 一旦 Key 对不上就会出现「点了一个指标结果图是空的」这种很费时间的 bug。
/// </summary>
public class GrowthMetricsTests
{
    [Fact]
    public void EveryMetric_HasATitleAndARoundTrippableKey()
    {
        foreach (var metric in GrowthMetrics.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(GrowthMetrics.Title(metric)), $"{metric} 没有标题");
            Assert.False(string.IsNullOrWhiteSpace(GrowthMetrics.Key(metric)), $"{metric} 没有 Key");

            var key = GrowthMetrics.Key(metric);
            Assert.Equal(metric, GrowthMetrics.FromKey(key));
        }
    }

    [Fact]
    public void Keys_AreUniqueAndCaseInsensitive()
    {
        var keys = GrowthMetrics.All.Select(GrowthMetrics.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 收藏的链接大小写被改掉也要能打开。
        Assert.Equal(GrowthMetric.Height, GrowthMetrics.FromKey("HEIGHT"));
        Assert.Equal(GrowthMetric.Height, GrowthMetrics.FromKey(" Height "));
    }

    [Fact]
    public void FromKey_ReturnsNullForUnknownKey()
    {
        // 返回 null 而不是抛异常：URL 参数是用户能随手改的，
        // 改坏了应该退回到默认视图，而不是给一个错误页。
        Assert.Null(GrowthMetrics.FromKey("nonsense"));
        Assert.Null(GrowthMetrics.FromKey(null));
        Assert.Null(GrowthMetrics.FromKey(""));
    }

    [Fact]
    public void DefaultSelection_IsHeightWeightBmi()
    {
        Assert.Equal(
            new[] { GrowthMetric.Height, GrowthMetric.Weight, GrowthMetric.Bmi },
            GrowthMetrics.DefaultSelection);
    }

    [Fact]
    public void TemperatureSleepAndExercise_AreSelectableButOffByDefault()
    {
        // 需求里明确说体温「默认不勾」——多数日子没有体温数据，
        // 默认画出来只会多一张空图。
        foreach (var metric in new[] { GrowthMetric.Temperature, GrowthMetric.SleepHours, GrowthMetric.ExerciseMinutes })
        {
            Assert.Contains(metric, GrowthMetrics.All);
            Assert.DoesNotContain(metric, GrowthMetrics.DefaultSelection);
        }

        Assert.Equal("℃", GrowthMetrics.Unit(GrowthMetric.Temperature));
        Assert.Equal("小时", GrowthMetrics.Unit(GrowthMetric.SleepHours));
        Assert.Equal("分钟", GrowthMetrics.Unit(GrowthMetric.ExerciseMinutes));
    }

    [Fact]
    public void AppetiteAndMood_AreLadderMetricsWithTickLabels()
    {
        Assert.False(GrowthMetrics.IsNumeric(GrowthMetric.Appetite));
        Assert.False(GrowthMetrics.IsNumeric(GrowthMetric.Mood));
        Assert.True(GrowthMetrics.IsNumeric(GrowthMetric.Height));

        // 刻度表下标即数值：1 很差 / 2 一般 / 3 不错 / 4 很好。
        var appetite = GrowthMetrics.TickLabels(GrowthMetric.Appetite)!;
        Assert.Equal(5, appetite.Count);
        Assert.Equal("很差", appetite[1]);
        Assert.Equal("很好", appetite[4]);

        Assert.Null(GrowthMetrics.TickLabels(GrowthMetric.Height));
    }

    [Fact]
    public void ValueOf_TreatsUnknownEnumAsNoData()
    {
        var point = new GrowthPoint(
            DateOnly.FromDateTime(DateTime.Today),
            10,
            "2026-10-04",
            RecordKind.Point,
            HeightCm: 90,
            WeightKg: null,
            Bmi: null,
            TemperatureC: null,
            SleepHours: null,
            ExerciseMinutes: null,
            Appetite: AppetiteLevel.Unknown,
            Mood: MoodLevel.Happy);

        Assert.Equal(90d, GrowthMetrics.ValueOf(point, GrowthMetric.Height));

        // 「未记录」不是「很差」：画成 0 会把平均值和趋势一起带偏。
        Assert.Null(GrowthMetrics.ValueOf(point, GrowthMetric.Appetite));
        Assert.Equal((double)MoodLevel.Happy, GrowthMetrics.ValueOf(point, GrowthMetric.Mood));
    }

    [Fact]
    public void Format_RendersLadderValuesAsText()
    {
        Assert.Equal("不错", GrowthMetrics.Format(GrowthMetric.Appetite, 3));
        Assert.Equal("开心", GrowthMetrics.Format(GrowthMetric.Mood, 3));
        Assert.Equal("90.5", GrowthMetrics.Format(GrowthMetric.Height, 90.5));
    }
}

/// <summary>曲线的显示偏好（分图 / 叠加 / 归一化 / 记录方式 / 时间范围）。</summary>
public class GrowthViewStateTests
{
    [Fact]
    public void Metrics_DefaultsToTheDefaultSelection()
    {
        var state = new GrowthViewState();

        Assert.Equal(GrowthMetrics.DefaultSelection, state.EffectiveMetrics);
        Assert.False(state.Overlay);
        Assert.False(state.Normalize);
        Assert.Null(state.KindFilter);
        Assert.Equal(6, state.RangeMonths);
        Assert.False(state.UseCustomRange);
    }

    [Fact]
    public void EffectiveMetrics_NeverReturnsEmpty()
    {
        var state = new GrowthViewState { Metrics = [] };

        // 一个指标都不勾会让整页变成空白，比「没反应」更让人困惑。
        // 所以空集合退化回默认选择，而不是真的什么都不画。
        Assert.NotEmpty(state.EffectiveMetrics);
        Assert.Equal(GrowthMetrics.DefaultSelection, state.EffectiveMetrics);
    }

    [Fact]
    public void EffectiveMetrics_DropsUnknownEntriesAndKeepsOrder()
    {
        var state = new GrowthViewState
        {
            Metrics = [GrowthMetric.Temperature, GrowthMetric.Height],
        };

        // 顺序按 All 固定，这样图片形状在两次访问之间是稳定的。
        Assert.Equal([GrowthMetric.Height, GrowthMetric.Temperature], state.EffectiveMetrics);
    }
}
