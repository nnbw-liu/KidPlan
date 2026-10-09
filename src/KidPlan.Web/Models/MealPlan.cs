using System.ComponentModel.DataAnnotations;

namespace KidPlan.Web.Models;

/// <summary>
/// 一条饮食安排。既是“计划”，也是“是否吃完”的记录载体。
/// 是否吃完由两部分决定：
/// 1. <see cref="Status"/> —— 家长手工确认的结果（已吃完 / 已跳过）；
/// 2. 系统当前时间与 <see cref="PlannedTime"/> 的关系 —— 未确认时实时推算的状态。
///
/// <see cref="PlannedTime"/> 可以为空：点心 / 零食本来就是不定时的，
/// 留空表示「今天有这顿，但不掐时间」，这类餐次不做饭点倒计时，也不会变成「已过饭点待确认」。
/// </summary>
public class MealPlan
{
    public int Id { get; set; }

    public int ChildId { get; set; }

    public Child? Child { get; set; }

    /// <summary>归属日期（本地日期）。</summary>
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public MealType MealType { get; set; } = MealType.Breakfast;

    /// <summary>
    /// 计划吃饭时间（本地时间）；为空表示「不定时」。
    /// 固定三餐请务必填上，加餐可以留空。
    /// </summary>
    public TimeOnly? PlannedTime { get; set; } = new(8, 0);

    /// <summary>菜单 / 菜名，例如“小米粥 + 蒸蛋 + 西兰花”。</summary>
    [Required(ErrorMessage = "请填写这一餐吃什么")]
    [MaxLength(500)]
    public string Menu { get; set; } = string.Empty;

    /// <summary>涉及的食材，用逗号分隔。过敏校验主要看这个字段和 Menu。</summary>
    [MaxLength(500)]
    public string? Foods { get; set; }

    /// <summary>预计热量（千卡），可选。</summary>
    [Range(0, 10000, ErrorMessage = "热量请填 0~10000 千卡")]
    public int? EstimatedCalories { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>家长确认的状态。</summary>
    public MealStatus Status { get; set; } = MealStatus.Planned;

    /// <summary>确认吃完的实际时间。</summary>
    public DateTimeOffset? EatenAt { get; set; }

    /// <summary>吃完情况备注，例如“吃了大半碗，蔬菜没吃完”。</summary>
    [MaxLength(500)]
    public string? FinishNote { get; set; }

    /// <summary>这一餐的实际食欲。</summary>
    public AppetiteLevel Appetite { get; set; } = AppetiteLevel.Unknown;

    /// <summary>
    /// 安排的内容命中小孩过敏食物时，需要家长明确勾选确认才能保存，
    /// 避免误排造成风险。
    /// </summary>
    public bool AllergyWarningAcknowledged { get; set; }

    /// <summary>
    /// 确认风险当时，命中的最高过敏等级。
    ///
    /// 存在的意义是给回顾页用：安排时只命中了「中等」，后来家长把某个食材升级成
    /// 「过敏性休克」，那这顿旧安排就该被重新审视。只存一个 bool 是看不出这个差别的。
    /// </summary>
    public AllergySeverity? AllergyAcknowledgedMaxSeverity { get; set; }

    /// <summary>什么时候勾的「我知道风险」。</summary>
    public DateTimeOffset? AllergyAcknowledgedAt { get; set; }

    /// <summary>这一餐是 AI 推荐生成后写入的（纯记录用途，方便回头评价推荐质量）。</summary>
    public bool CreatedFromAiSuggestion { get; set; }

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
