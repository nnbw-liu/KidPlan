namespace KidPlan.Web.Models;

/// <summary>性别。</summary>
public enum Gender
{
    Unspecified = 0,
    Boy = 1,
    Girl = 2,
}

/// <summary>
/// 一餐的类型。
///
/// 分成两类，这个区分贯穿「汇总统计」「漏记判定」「推荐」三处逻辑：
/// <list type="bullet">
///   <item><b>固定三餐</b>：<see cref="Breakfast"/> / <see cref="Lunch"/> / <see cref="Dinner"/>，
///         一天各一顿，必须有饭点，会参与「漏记」和「吃了 1/3 餐」这类进度统计；</item>
///   <item><b>不定时加餐</b>：<see cref="Snack"/>（点心）/ <see cref="Treat"/>（零食），
///         一天可以任意多顿，饭点可以留空（留空就不做饭点倒计时），也不会让某天被算成「漏记」。</item>
/// </list>
///
/// 数值刻意保持稳定：<c>Snack = 4</c> 在旧版本里叫「加餐」，现在叫「点心」，
/// 语义延续（都是正餐之外的补充），所以数据库里已有的行不需要改写。
/// </summary>
public enum MealType
{
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
    Snack = 4,
    Treat = 5,
}

/// <summary>人工确认的用餐结果（持久化到数据库）。</summary>
public enum MealStatus
{
    /// <summary>已安排，还没确认吃没吃完。</summary>
    Planned = 0,

    /// <summary>已确认吃完。</summary>
    Eaten = 1,

    /// <summary>已跳过（临时改变安排、孩子不在家等）。</summary>
    Skipped = 2,
}

/// <summary>
/// 根据系统时间实时推算出来的用餐进度（不落库，每次展示时计算）。
/// “是否已经吃完”由这个状态体现。
/// </summary>
public enum MealTimeState
{
    /// <summary>还没到饭点。</summary>
    Upcoming = 0,

    /// <summary>正在饭点窗口内，该吃了。</summary>
    Serving = 1,

    /// <summary>已过饭点仍未确认吃完，需要家长确认。</summary>
    Overdue = 2,

    /// <summary>已确认吃完。</summary>
    Eaten = 3,

    /// <summary>已跳过。</summary>
    Skipped = 4,

    /// <summary>
    /// 没有填饭点的加餐（点心 / 零食）。
    /// 这类餐次刻意不参与「到点没到点」的推算：既然本来就不定时，
    /// 就不该过一会儿就变成一条「已过饭点待确认」的红字，否则一天能刷出一屏噪音。
    /// </summary>
    Unscheduled = 5,
}

/// <summary>
/// 过敏严重程度。数值越大越危险，判断阈值时直接比大小。
///
/// 排序刻意不是「轻微 → 不耐受」，而是把不耐受放在最低档：
/// 不耐受（乳糖不耐受这类）机制上不是 IgE 介导的过敏，通常表现为消化道不适，
/// 和「吃一口就起疹子/喉头水肿」不是一回事，混在一起报警只会让人对警告麻木。
/// </summary>
public enum AllergySeverity
{
    /// <summary>不耐受：腹胀、腹泻这类，不是过敏反应。仅提示。</summary>
    Intolerance = 0,

    /// <summary>轻微：轻微皮疹、口周发红等。仅提示。</summary>
    Mild = 1,

    /// <summary>中等：需要回避，但误食通常可控。警告 + 需勾选确认。</summary>
    Moderate = 2,

    /// <summary>强过敏：明确的 IgE 过敏，误食反应明显。硬警告 + 双重确认。</summary>
    Severe = 3,

    /// <summary>过敏性休克：曾发生或高风险。硬警告 + 双重确认，且 AI 推荐一律回避。</summary>
    Anaphylaxis = 4,
}

/// <summary>过敏原是吃的还是别的（药物 / 环境等）。</summary>
public enum AllergenKind
{
    /// <summary>食物：会拿菜单和食材去比对。</summary>
    Food = 0,

    /// <summary>非食物（药物、花粉、尘螨……）：只做档案展示，不参与菜单比对。</summary>
    Other = 1,
}

/// <summary>食欲。</summary>
public enum AppetiteLevel
{
    Unknown = 0,
    Poor = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4,
}

/// <summary>精神状态 / 情绪。</summary>
public enum MoodLevel
{
    Unknown = 0,
    Low = 1,
    Normal = 2,
    Happy = 3,
    Excited = 4,
}

/// <summary>身体记录的类型：单次记录或某个时间段的记录。</summary>
public enum RecordKind
{
    /// <summary>单次记录，记录某个时间点的身体情况。</summary>
    Point = 0,

    /// <summary>时段记录，记录一段时间的身体情况（如一周的睡眠/食欲观察）。</summary>
    Period = 1,
}

/// <summary>
/// 过敏命中之后该用什么力度处理。由 <see cref="AllergySeverity"/> 推导，不要在别处另写一套阈值。
/// </summary>
public enum AllergyRiskAction
{
    /// <summary>只提示（不耐受 / 轻微）：不拦保存，也不进「需要你处理」的红色计数。</summary>
    Notice = 0,

    /// <summary>警告 + 需勾选「我知道风险」才能保存（中等）。</summary>
    Confirm = 1,

    /// <summary>硬警告 + 勾选之外还要再点一次确认（强过敏 / 过敏性休克）。</summary>
    DoubleConfirm = 2,
}

/// <summary>枚举的中文显示文案。</summary>
public static class EnumDisplay
{
    public static string ToChinese(this Gender value) => value switch
    {
        Gender.Boy => "男孩",
        Gender.Girl => "女孩",
        _ => "未填写",
    };

    public static string ToChinese(this MealType value) => value switch
    {
        MealType.Breakfast => "早餐",
        MealType.Lunch => "午餐",
        MealType.Dinner => "晚餐",
        MealType.Snack => "点心",
        MealType.Treat => "零食",
        _ => "用餐",
    };

    public static string ToChinese(this MealStatus value) => value switch
    {
        MealStatus.Eaten => "已吃完",
        MealStatus.Skipped => "已跳过",
        _ => "待确认",
    };

    public static string ToChinese(this MealTimeState value) => value switch
    {
        MealTimeState.Upcoming => "未到饭点",
        MealTimeState.Serving => "用餐中",
        MealTimeState.Overdue => "已过饭点·待确认",
        MealTimeState.Eaten => "已吃完",
        MealTimeState.Skipped => "已跳过",
        MealTimeState.Unscheduled => "加餐·未定时点",
        _ => "未知",
    };

    public static string ToChinese(this AppetiteLevel value) => value switch
    {
        AppetiteLevel.Poor => "很差",
        AppetiteLevel.Fair => "一般",
        AppetiteLevel.Good => "不错",
        AppetiteLevel.Excellent => "很好",
        _ => "未记录",
    };

    public static string ToChinese(this MoodLevel value) => value switch
    {
        MoodLevel.Low => "低落",
        MoodLevel.Normal => "平稳",
        MoodLevel.Happy => "开心",
        MoodLevel.Excited => "兴奋",
        _ => "未记录",
    };

    public static string ToChinese(this RecordKind value) => value switch
    {
        RecordKind.Period => "时段记录",
        _ => "单次记录",
    };

    public static string ToChinese(this AllergySeverity value) => value switch
    {
        AllergySeverity.Intolerance => "不耐受",
        AllergySeverity.Mild => "轻微",
        AllergySeverity.Moderate => "中等",
        AllergySeverity.Severe => "强过敏",
        AllergySeverity.Anaphylaxis => "过敏性休克",
        _ => "未分级",
    };

    /// <summary>给界面用的一句话解释，帮家长判断该选哪一级。</summary>
    public static string Describe(this AllergySeverity value) => value switch
    {
        AllergySeverity.Intolerance => "吃完胀气、拉肚子这类，不是过敏反应。只在安排时提示一下。",
        AllergySeverity.Mild => "轻微皮疹、口周发红，很快消退。只在安排时提示一下。",
        AllergySeverity.Moderate => "需要回避，误食一般可控。命中会警告，勾选「我知道风险」才能保存。",
        AllergySeverity.Severe => "明确过敏、误食反应明显。命中要勾选并再确认一次，AI 推荐会直接回避。",
        AllergySeverity.Anaphylaxis => "曾发生或高风险过敏性休克。命中要勾选并再确认一次，AI 推荐绝不出现该食材。",
        _ => string.Empty,
    };

    public static string ToChinese(this AllergenKind value) => value switch
    {
        AllergenKind.Other => "非食物",
        _ => "食物",
    };

    public static string ToChinese(this AllergyRiskAction value) => value switch
    {
        AllergyRiskAction.Confirm => "需确认",
        AllergyRiskAction.DoubleConfirm => "需二次确认",
        _ => "仅提示",
    };

    /// <summary>固定三餐，按一天中的顺序。</summary>
    public static readonly MealType[] FixedMealTypes =
    [
        MealType.Breakfast,
        MealType.Lunch,
        MealType.Dinner,
    ];

    /// <summary>不定时加餐（点心 / 零食），一天可以任意多顿。</summary>
    public static readonly MealType[] AdHocMealTypes =
    [
        MealType.Snack,
        MealType.Treat,
    ];

    /// <summary>所有餐次，按一天中的顺序排列。</summary>
    public static readonly MealType[] MealTypeOrder =
    [
        MealType.Breakfast,
        MealType.Lunch,
        MealType.Dinner,
        MealType.Snack,
        MealType.Treat,
    ];

    /// <summary>所有过敏等级，从轻到重。</summary>
    public static readonly AllergySeverity[] AllergySeverityOrder =
    [
        AllergySeverity.Intolerance,
        AllergySeverity.Mild,
        AllergySeverity.Moderate,
        AllergySeverity.Severe,
        AllergySeverity.Anaphylaxis,
    ];

    /// <summary>这一餐是不是「固定三餐」之一（决定它算不算漏记、要不要有饭点）。</summary>
    public static bool IsFixedMeal(this MealType value) => value switch
    {
        MealType.Breakfast or MealType.Lunch or MealType.Dinner => true,
        _ => false,
    };

    /// <summary>由过敏等级推导出处理力度。全项目只此一处阈值。</summary>
    public static AllergyRiskAction ToRiskAction(this AllergySeverity value) => value switch
    {
        AllergySeverity.Anaphylaxis or AllergySeverity.Severe => AllergyRiskAction.DoubleConfirm,
        AllergySeverity.Moderate => AllergyRiskAction.Confirm,
        _ => AllergyRiskAction.Notice,
    };
}
