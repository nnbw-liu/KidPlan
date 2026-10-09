using KidPlan.Web.Services;

namespace KidPlan.Tests;

/// <summary>
/// 叠加曲线的横轴对齐。
///
/// 这是纯函数，但很容易写错：两个指标的测量日期几乎不会完全一样，
/// 对齐规则错了会画出「身高在涨、体重却在跌」这种莫名其妙的图。
/// 断言都用带精度的比较——插值天然带浮点误差，直接比集合迟早会假失败。
/// </summary>
public class ChartAlignTests
{
    private static ChartAlign.Series Series(string name, string[] labels, double[] values)
        => new(name, labels, values);

    private static void AssertClose(double[] expected, IReadOnlyList<double> actual, int precision = 6)
    {
        Assert.Equal(expected.Length, actual.Count);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], precision);
        }
    }

    /// <summary>
    /// 三条曲线刻意设计成：A 有三点、B 缺中间那个正好等分的点、C 只有一个点。
    /// 于是这一次断言同时覆盖了「并集」「插值」「端点外取端点」三件事。
    /// </summary>
    private static ChartAlign.Series[] ThreeSeries() =>
    [
        Series("A", ["2026-08-01", "2026-08-11", "2026-08-21"], [10, 20, 30]),
        Series("B", ["2026-08-01", "2026-08-21"], [100, 200]),
        Series("C", ["2026-08-05"], [7]),
    ];

    [Fact]
    public void Align_UsesUnionOfLabelsSortedByDate()
    {
        var aligned = ChartAlign.Align(ThreeSeries());

        Assert.Equal(["2026-08-01", "2026-08-05", "2026-08-11", "2026-08-21"], aligned.Labels);
        Assert.Equal(["A", "B", "C"], aligned.SeriesNames);
    }

    [Fact]
    public void Align_InterpolatesMissingMiddlePoints()
    {
        var aligned = ChartAlign.Align(ThreeSeries());

        AssertClose([10, 14, 20, 30], aligned.Values[0]);

        // B 只有首尾两点，08-11 正好在中间（20 天中的第 10 天）→ 150。
        AssertClose([100, 120, 150, 200], aligned.Values[1]);
    }

    [Fact]
    public void Align_ClampsOutsideTheKnownRangeInsteadOfExtrapolating()
    {
        var aligned = ChartAlign.Align(ThreeSeries());

        // C 只有一个点，其它日期一律取这个值。外推会画出「没测过的地方也在涨」的假趋势。
        AssertClose([7, 7, 7, 7], aligned.Values[2]);
    }

    [Fact]
    public void Align_NormalizeStretchesEachSeriesToZeroHundred()
    {
        var aligned = ChartAlign.Align(ThreeSeries(), normalize: true);

        // 每条线各自按自己的最小/最大值拉满：A 的 10→0、30→100；C 全程不变 → 50。
        AssertClose([0, 20, 50, 100], aligned.Values[0]);
        AssertClose([0, 20, 50, 100], aligned.Values[1]);
        AssertClose([50, 50, 50, 50], aligned.Values[2]);
    }

    [Fact]
    public void Align_NormalizeGivesFlatSeriesFifty()
    {
        // 全程没变化的指标归一化时分母为 0，会得到 NaN，画出来就是整条线消失。
        var flat = Series("体重", ["2026-08-01", "2026-09-01"], [13, 13]);

        var aligned = ChartAlign.Align([flat], normalize: true);

        AssertClose([50, 50], aligned.Values[0]);
    }

    [Fact]
    public void Align_DropsSeriesWithoutPoints_ButKeepsTheOthers()
    {
        var height = Series("身高", ["2026-08-01"], [89]);
        var empty = Series("体温", [], []);

        var aligned = ChartAlign.Align([empty, height]);

        Assert.Equal(["身高"], aligned.SeriesNames);
        Assert.Equal(["2026-08-01"], aligned.Labels);
    }

    [Fact]
    public void Align_DropsSeriesWhoseLabelAndValueCountsDiffer()
    {
        // 长度不一致是上游组装的 bug；这里宁可丢掉一条线，也不能让 MudChart 拿到错位的数据。
        var broken = Series("坏的", ["2026-08-01", "2026-08-02"], [1]);
        var good = Series("身高", ["2026-08-01"], [89]);

        var aligned = ChartAlign.Align([broken, good]);

        Assert.Equal(["身高"], aligned.SeriesNames);
    }

    [Fact]
    public void Align_ReturnsEmptyWhenNothingHasData()
    {
        var aligned = ChartAlign.Align([Series("身高", [], [])]);

        Assert.Empty(aligned.Labels);
        Assert.Empty(aligned.Values);
        Assert.Empty(aligned.SeriesNames);
        Assert.True(aligned.IsEmpty);
    }

    [Fact]
    public void Align_SortsLabelsOrdinalSoIsoDatesComeOutChronological()
    {
        var series = Series("身高", ["2026-10-01", "2026-02-01", "2026-09-30"], [1, 2, 3]);

        var aligned = ChartAlign.Align([series]);

        Assert.Equal(["2026-02-01", "2026-09-30", "2026-10-01"], aligned.Labels);
        AssertClose([2, 3, 1], aligned.Values[0]);
    }
}
