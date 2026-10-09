namespace KidPlan.Web.Services;

/// <summary>
/// 把多条量纲不同的曲线对齐到同一张图里。
///
/// 背景：身高 100 上下、体温 36 上下、运动 30 上下，直接叠在一起，
/// 体温那条会被压成一条水平线，什么也看不出来。所以叠加模式要做两件事：
/// 1. **按日期对齐**：各指标的测量日期并不重合（今天量了体温不一定量身高），
///    MudChart 又要求所有序列长度一致，所以先取日期并集，缺失处按相邻两点线性插值；
/// 2. **可选归一化**：把每条线按自己的最大最小值拉到 0~100，只比形状不比绝对值。
///
/// 数值插值写成纯函数，方便单测——图表出问题时最难查的就是这里。
/// </summary>
public static class ChartAlign
{
    /// <summary>一条待对齐的曲线。</summary>
    public sealed record Series(string Name, IReadOnlyList<string> Labels, IReadOnlyList<double> Values);

    /// <summary>
    /// 对齐后的结果。<see cref="Labels"/> 是所有序列日期标签的并集（升序）；
    /// <see cref="Values"/> 与 <see cref="Labels"/> 一一对应，长度相同。
    /// </summary>
    public sealed record AlignedChart(
        IReadOnlyList<string> Labels,
        IReadOnlyList<IReadOnlyList<double>> Values,
        IReadOnlyList<string> SeriesNames)
    {
        public bool IsEmpty => Labels.Count == 0;
    }

    /// <summary>
    /// 对齐多条曲线。
    /// </summary>
    /// <param name="series">各条曲线，标签必须是可排序的 <c>yyyy-MM-dd</c> 形式。</param>
    /// <param name="normalize">是否把每条线拉成 0~100 的相对走势。</param>
    public static AlignedChart Align(IReadOnlyList<Series> series, bool normalize = false)
    {
        var usable = series
            .Where(s => s.Labels.Count > 0 && s.Labels.Count == s.Values.Count)
            .ToList();

        if (usable.Count == 0)
        {
            return new AlignedChart([], [], []);
        }

        // 日期并集。标签是 yyyy-MM-dd，直接按字符串排序就等于按日期排序，
        // 还能避开「标签格式不一致时 DateTime.Parse 抛异常」这种坑。
        var labels = usable
            .SelectMany(s => s.Labels)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        var values = usable.Select(s => AlignOne(s, labels, normalize)).ToList();

        return new AlignedChart(labels, values, usable.Select(s => s.Name).ToList());
    }

    private static IReadOnlyList<double> AlignOne(Series series, IReadOnlyList<string> labels, bool normalize)
    {
        var points = new List<(DateOnly Date, double Value)>(series.Labels.Count);

        for (var i = 0; i < series.Labels.Count; i++)
        {
            if (DateOnly.TryParse(series.Labels[i], out var date))
            {
                points.Add((date, series.Values[i]));
            }
        }

        if (points.Count == 0)
        {
            return labels.Select(_ => 0d).ToList();
        }

        points.Sort((a, b) => a.Date.CompareTo(b.Date));

        var aligned = new List<double>(labels.Count);

        foreach (var label in labels)
        {
            if (!DateOnly.TryParse(label, out var date))
            {
                aligned.Add(points[0].Value);
                continue;
            }

            aligned.Add(Interpolate(points, date));
        }

        return normalize ? Normalize(aligned) : aligned;
    }

    /// <summary>按日期线性插值；区间外取最近端点的值（不外推，免得画出不存在的数据）。</summary>
    private static double Interpolate(List<(DateOnly Date, double Value)> points, DateOnly date)
    {
        if (date <= points[0].Date)
        {
            return points[0].Value;
        }

        var last = points[^1];
        if (date >= last.Date)
        {
            return last.Value;
        }

        for (var i = 1; i < points.Count; i++)
        {
            var (nextDate, nextValue) = points[i];
            if (nextDate < date)
            {
                continue;
            }

            var (prevDate, prevValue) = points[i - 1];

            if (nextDate == date)
            {
                return nextValue;
            }

            var span = nextDate.DayNumber - prevDate.DayNumber;
            if (span <= 0)
            {
                return nextValue;
            }

            var ratio = (date.DayNumber - prevDate.DayNumber) / (double)span;
            return prevValue + ((nextValue - prevValue) * ratio);
        }

        return last.Value;
    }

    /// <summary>把一条线按自己的最小/最大值拉到 0~100。全体相同时统一给 50，避免出现 0/0。</summary>
    internal static List<double> Normalize(List<double> values)
    {
        var min = values.Min();
        var max = values.Max();
        var span = max - min;

        return span <= double.Epsilon
            ? values.Select(_ => 50d).ToList()
            : values.Select(v => (v - min) / span * 100d).ToList();
    }
}
