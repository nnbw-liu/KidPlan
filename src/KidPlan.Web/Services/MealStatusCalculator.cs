using Microsoft.Extensions.Options;
using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

/// <summary>
/// 饭点判断的可配置窗口。
/// </summary>
public sealed class MealScheduleOptions
{
    public const string SectionName = "MealSchedule";

    /// <summary>饭点前多少分钟开始算“用餐中”。默认 30 分钟。</summary>
    public int ServingWindowBeforeMinutes { get; set; } = 30;

    /// <summary>饭点后多少分钟之内还算“用餐中”，超过就算“已过饭点·待确认”。默认 60 分钟。</summary>
    public int OverdueAfterMinutes { get; set; } = 60;
}

/// <summary>
/// 根据系统时间推算某一餐当前处于什么状态。
///
/// 这是“判断是否已经吃完”的核心逻辑：
/// - 家长已确认吃完 / 已跳过 → 直接返回确认结果；
/// - 没填饭点的加餐（点心 / 零食）→ <see cref="MealTimeState.Unscheduled"/>，不做时间推算；
/// - 未确认时，用系统当前时间和计划饭点比较，得出“未到饭点 / 用餐中 / 已过饭点·待确认”；
/// - 如果这一餐的日期在昨天或更早，无论时间都算“已过饭点·待确认”，
///   因为过去的日子不可能再补一句“还没吃”，必须让家长确认掉。
///
/// 依赖 <see cref="TimeProvider"/> 而不是直接读 DateTime.Now，这样单元测试可以注入固定的“现在”。
/// 时间比较统一使用 DateTimeOffset 的墙上时钟（<see cref="DateTimeOffset.DateTime"/>），
/// 保证记录、饭点、当前时间三者在同一个时区口径下比较，不受运行机器的时区设置影响。
/// </summary>
public sealed class MealStatusCalculator(IOptions<MealScheduleOptions> options, TimeProvider timeProvider)
{
    private readonly MealScheduleOptions _options = options.Value;

    /// <summary>用系统当前时间推算状态。</summary>
    public MealTimeState Resolve(MealPlan meal) => Resolve(meal, timeProvider.GetLocalNow());

    /// <summary>用指定的“当前时间”推算状态（测试用）。</summary>
    public MealTimeState Resolve(MealPlan meal, DateTimeOffset now)
    {
        // 家长已经确认过的，以确认为准。
        if (meal.Status == MealStatus.Eaten)
        {
            return MealTimeState.Eaten;
        }

        if (meal.Status == MealStatus.Skipped)
        {
            return MealTimeState.Skipped;
        }

        // 没填饭点的加餐不参与时间推算。
        //
        // 需求就是「不定时的多顿点心零食」：既然没定时间，就没有“到点了没吃”这回事。
        // 如果这里退化成「过去的日期一律已过饭点」，一天排三顿点心就会刷出三条红字待确认，
        // 而这些本来是可吃可不吃的——警告一旦变成噪音，真正的重要提醒也就没人看了。
        if (meal.PlannedTime is null)
        {
            return MealTimeState.Unscheduled;
        }

        var today = DateOnly.FromDateTime(now.DateTime);
        if (meal.Date > today)
        {
            return MealTimeState.Upcoming;
        }

        if (meal.Date < today)
        {
            return MealTimeState.Overdue;
        }

        var plannedAt = meal.Date.ToDateTime(meal.PlannedTime.Value);
        var nowWallClock = now.DateTime;

        if (nowWallClock < plannedAt.AddMinutes(-_options.ServingWindowBeforeMinutes))
        {
            return MealTimeState.Upcoming;
        }

        return nowWallClock <= plannedAt.AddMinutes(_options.OverdueAfterMinutes)
            ? MealTimeState.Serving
            : MealTimeState.Overdue;
    }

    /// <summary>是否已经可以认为“吃完了”。</summary>
    public bool IsEaten(MealPlan meal) => Resolve(meal) == MealTimeState.Eaten;

    /// <summary>
    /// 相对饭点的时间差：负数表示还差多久到饭点，正数表示已经过了多久。
    /// 没填饭点时没有意义，返回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    public TimeSpan GetOffsetFromPlannedTime(MealPlan meal) => GetOffsetFromPlannedTime(meal, timeProvider.GetLocalNow());

    /// <summary>相对饭点的时间差（测试用）。</summary>
    public TimeSpan GetOffsetFromPlannedTime(MealPlan meal, DateTimeOffset now)
        => meal.PlannedTime is { } planned
            ? now.DateTime - meal.Date.ToDateTime(planned)
            : TimeSpan.Zero;

    /// <summary>
    /// 给界面用的一句人话说明，例如“已过饭点 25 分钟，还没确认”。
    /// </summary>
    public string Describe(MealPlan meal) => Describe(meal, timeProvider.GetLocalNow());

    /// <summary>给界面用的一句人话说明（测试用）。</summary>
    public string Describe(MealPlan meal, DateTimeOffset now)
    {
        var state = Resolve(meal, now);
        var offset = GetOffsetFromPlannedTime(meal, now);
        var minutes = (int)Math.Round(Math.Abs(offset.TotalMinutes));

        return state switch
        {
            MealTimeState.Eaten => meal.EatenAt is { } eatenAt
                ? $"已于 {eatenAt.DateTime:HH:mm} 确认吃完"
                : "已确认吃完",
            MealTimeState.Skipped => "这一餐已跳过",
            MealTimeState.Unscheduled => $"没定时间，{meal.MealType.ToChinese()}随时都可以",
            MealTimeState.Upcoming => $"还有 {FormatDuration(minutes)} 到饭点",
            MealTimeState.Serving => offset < TimeSpan.Zero
                ? $"{FormatDuration(minutes)}后开饭"
                : $"已开饭 {FormatDuration(minutes)}",
            MealTimeState.Overdue when meal.Date < DateOnly.FromDateTime(now.DateTime)
                => "这是过去安排的餐，请确认是否吃过",
            MealTimeState.Overdue => $"已过饭点 {FormatDuration(minutes)}，还没确认",
            _ => string.Empty,
        };
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes} 分钟";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} 小时" : $"{hours} 小时 {rest} 分钟";
    }
}
