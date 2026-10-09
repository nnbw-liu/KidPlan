using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

/// <summary>
/// 成长曲线的一个数据点。X 轴用日期标签，同时带上月龄供提示/表格显示。
/// 一条记录可能只填了其中几项，没填的保持 null，各条曲线各取所需。
/// </summary>
public sealed record GrowthPoint(
    DateOnly Date,
    int AgeMonths,
    string Label,
    RecordKind Kind,
    double? HeightCm,
    double? WeightKg,
    double? Bmi,
    double? TemperatureC,
    double? SleepHours,
    double? ExerciseMinutes,
    AppetiteLevel Appetite,
    MoodLevel Mood);

/// <summary>成长曲线整份数据（按时间升序）。</summary>
public sealed record GrowthChartData(
    DateOnly BirthDate,
    IReadOnlyList<GrowthPoint> Points)
{
    public bool IsEmpty => Points.Count == 0;

    /// <summary>这份数据里有没有某个指标的值。</summary>
    public bool Has(GrowthMetric metric) => Points.Any(p => GrowthMetrics.ValueOf(p, metric).HasValue);

    /// <summary>取出某个指标的 (标签, 值) 序列，只含有值的点。</summary>
    public (List<string> Labels, List<double> Values) Series(GrowthMetric metric)
    {
        var labels = new List<string>();
        var values = new List<double>();

        foreach (var point in Points)
        {
            if (GrowthMetrics.ValueOf(point, metric) is { } value)
            {
                labels.Add(point.Label);
                values.Add(value);
            }
        }

        return (labels, values);
    }
}

/// <summary>最新一次身体数据，以及与上一次的差值。</summary>
public sealed record GrowthSummary(
    DateOnly? LatestDate,
    decimal? HeightCm,
    decimal? WeightKg,
    decimal? TemperatureC,
    decimal? Bmi,
    decimal? HeightDelta,
    decimal? WeightDelta,
    int RecordCount,
    int AgeMonths);

/// <summary>一餐 + 实时推算出来的状态 + 过敏命中。</summary>
public sealed record MealWithState(
    MealPlan Meal,
    MealTimeState State,
    string StateText,
    /// <summary>
    /// 这一餐涉及的全部过敏原——**不管家长有没有确认过风险**。
    /// 这是客观事实，回顾类的界面需要它。
    /// </summary>
    IReadOnlyList<AllergyMatch> AllergenMatches)
{
    /// <summary>命中的最高等级；没有命中就是 null。</summary>
    public AllergySeverity? MaxSeverity =>
        AllergenMatches.Count == 0 ? null : AllergenMatches.Max(m => m.Severity);

    /// <summary>按最高等级决定这一餐该用什么力度处理。</summary>
    public AllergyRiskAction RiskAction => MaxSeverity?.ToRiskAction() ?? AllergyRiskAction.Notice;

    /// <summary>
    /// 还没确认过风险、且等级达到「中等」以上的命中——这些才是要弹红字让人处理的。
    /// 家长勾过「我知道风险」之后刻意清空，否则同一餐天天弹红字，警告就没人看了。
    /// </summary>
    public IReadOnlyList<AllergyMatch> PendingRiskMatches => Meal.AllergyWarningAcknowledged
        ? []
        : AllergenMatches.Where(m => m.Severity.ToRiskAction() != AllergyRiskAction.Notice).ToList();

    /// <summary>
    /// 仅提示级别的命中（不耐受 / 轻微）。不论确认与否都只做展示：
    /// 这类本来就常见，做成红色警告只会让人对真正的危险麻木。
    /// </summary>
    public IReadOnlyList<AllergyMatch> NoticeMatches => AllergenMatches
        .Where(m => m.Severity.ToRiskAction() == AllergyRiskAction.Notice)
        .ToList();

    public bool ContainsAllergen => AllergenMatches.Count > 0;

    /// <summary>需不需要家长现在去处理（逐步会算进红色计数）。</summary>
    public bool HasAllergyRisk => PendingRiskMatches.Count > 0;

    /// <summary>含强过敏 / 过敏性休克。这类永远值得显眼——哪怕已经确认过。</summary>
    public bool HasCriticalAllergen => MaxSeverity is { } s && s >= AllergySeverity.Severe;

    /// <summary>确认当时的最高等级，与现在的等级不一致时说明该复查了。</summary>
    public bool IsAllergenEscalated => Meal.AllergyWarningAcknowledged
        && Meal.AllergyAcknowledgedMaxSeverity is { } then
        && MaxSeverity is { } now
        && now > then;

    public bool IsEaten => State == MealTimeState.Eaten;

    public bool NeedsConfirmation => State == MealTimeState.Overdue;
}

/// <summary>某一天的饮食汇总。</summary>
public sealed record MealDaySummary(
    DateOnly Date,
    IReadOnlyList<MealWithState> Meals)
{
    /// <summary>固定三餐（早/午/晚）。</summary>
    public IReadOnlyList<MealWithState> FixedMeals =>
        Meals.Where(m => m.Meal.MealType.IsFixedMeal()).ToList();

    /// <summary>不定时加餐（点心 / 零食）。</summary>
    public IReadOnlyList<MealWithState> AdHocMeals =>
        Meals.Where(m => !m.Meal.MealType.IsFixedMeal()).ToList();

    public int TotalCount => Meals.Count;
    public int EatenCount => Meals.Count(m => m.State == MealTimeState.Eaten);
    public int OverdueCount => Meals.Count(m => m.State == MealTimeState.Overdue);
    public int UpcomingCount => Meals.Count(m => m.State == MealTimeState.Upcoming);
    public int ServingCount => Meals.Count(m => m.State == MealTimeState.Serving);
    public int SkippedCount => Meals.Count(m => m.State == MealTimeState.Skipped);
    public int UnscheduledCount => Meals.Count(m => m.State == MealTimeState.Unscheduled);

    public int FixedMealCount => Meals.Count(m => m.Meal.MealType.IsFixedMeal());
    public int FixedEatenCount => Meals.Count(m => m.Meal.MealType.IsFixedMeal() && m.State == MealTimeState.Eaten);
    public int AdHocMealCount => Meals.Count(m => !m.Meal.MealType.IsFixedMeal());
    public int AdHocEatenCount => Meals.Count(m => !m.Meal.MealType.IsFixedMeal() && m.State == MealTimeState.Eaten);

    /// <summary>有红字过敏命中、需要处理的餐数。</summary>
    public int AllergyRiskCount => Meals.Count(m => m.HasAllergyRisk);

    /// <summary>只有「仅提示」级别命中的餐数。</summary>
    public int AllergyNoticeCount => Meals.Count(m => !m.HasAllergyRisk && m.ContainsAllergen);

    public int PlannedCalories => Meals.Sum(m => m.Meal.EstimatedCalories ?? 0);
    public int EatenCalories => Meals.Where(m => m.IsEaten).Sum(m => m.Meal.EstimatedCalories ?? 0);

    /// <summary>这一天有没有任何安排。</summary>
    public bool IsEmpty => Meals.Count == 0;

    /// <summary>
    /// 这一天的**固定三餐**有没有安排。
    ///
    /// 漏记判定只看这个：只排了一顿点心不算「这天记过了」，
    /// 反过来只记了加餐也不会被判成漏记。
    /// </summary>
    public bool HasFixedMeals => FixedMealCount > 0;

    /// <summary>还有几餐既没确认吃完、也没标记跳过（不定时加餐不计入，它本来就不催）。</summary>
    public int UnconfirmedCount => Meals.Count(m => m.State is MealTimeState.Upcoming
        or MealTimeState.Serving
        or MealTimeState.Overdue);
}

/// <summary>
/// 一段日期区间（一周 / 一个月 / 全部）的餐饮记录。
/// “记录”是回顾用的：每天的明细 + 整段区间的汇总。
/// </summary>
public sealed class MealRangeSummary
{
    public MealRangeSummary(DateOnly from, DateOnly to, IReadOnlyList<MealDaySummary> days)
    {
        From = from;
        To = to;
        Days = days;

        // 这两个清单以前写成每次访问都重算的计算属性，模板里又反复读 Count，
        // 于是同一份数据在一次渲染里被扫描十几遍。区间一大就很痛，所以在这里算一次。
        AllergyHits = days
            .SelectMany(d => d.Meals
                .SelectMany(m => m.PendingRiskMatches.Select(match => (d.Date, m, match))))
            .ToList();

        AllergenRiskHits = days
            .SelectMany(d => d.Meals
                .SelectMany(m => m.AllergenMatches.Select(match => (d.Date, m, match))))
            .ToList();

        NoticeHits = days
            .SelectMany(d => d.Meals
                .SelectMany(m => m.NoticeMatches.Select(match => (d.Date, m, match))))
            .ToList();
    }

    public DateOnly From { get; }

    public DateOnly To { get; }

    public IReadOnlyList<MealDaySummary> Days { get; }

    /// <summary>区间内已经安排过固定三餐的天数。</summary>
    public int RecordedDayCount => Days.Count(d => d.HasFixedMeals);

    /// <summary>区间内固定三餐一天都没安排的天数。</summary>
    public int MissingDayCount => Days.Count(d => !d.HasFixedMeals);

    public int TotalMeals => Days.Sum(d => d.TotalCount);
    public int EatenMeals => Days.Sum(d => d.EatenCount);
    public int SkippedMeals => Days.Sum(d => d.SkippedCount);
    public int OverdueMeals => Days.Sum(d => d.OverdueCount);
    public int UnconfirmedMeals => Days.Sum(d => d.UnconfirmedCount);
    public int FixedMeals => Days.Sum(d => d.FixedMealCount);
    public int AdHocMeals => Days.Sum(d => d.AdHocMealCount);
    public int PlannedCalories => Days.Sum(d => d.PlannedCalories);
    public int EatenCalories => Days.Sum(d => d.EatenCalories);

    /// <summary>
    /// 区间内**还没被确认过风险**、且等级在「中等」以上的过敏命中——
    /// 也就是“需要你去看一眼”的那些。
    /// </summary>
    public IReadOnlyList<(DateOnly Date, MealWithState Meal, AllergyMatch Match)> AllergyHits { get; }

    /// <summary>
    /// 区间内**所有**涉及过敏食物的餐次，包含家长安排时已经勾选“我知道风险”的那部分。
    /// 这些不会在饮食页重复报警（避免狼来了），但回顾时仍然应该看得到吃过什么。
    /// </summary>
    public IReadOnlyList<(DateOnly Date, MealWithState Meal, AllergyMatch Match)> AllergenRiskHits { get; }

    /// <summary>只有「仅提示」级别命中的那些（不耐受 / 轻微）。</summary>
    public IReadOnlyList<(DateOnly Date, MealWithState Meal, AllergyMatch Match)> NoticeHits { get; }

    public bool IsEmpty => TotalMeals == 0;
}

/// <summary>首页仪表盘用的一个小孩的概览。</summary>
public sealed record ChildOverview(
    Child Child,
    int AgeMonths,
    string AgeText,
    GrowthSummary? Growth,
    MealDaySummary TodayMeals);

/// <summary>小孩列表页的一行。</summary>
public sealed record ChildListItem(
    Child Child,
    int AgeMonths,
    string AgeText,
    int GrowthRecordCount,
    int MealPlanCount,
    DateOnly? LatestRecordDate,
    decimal? LatestHeightCm,
    decimal? LatestWeightKg);
