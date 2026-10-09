using System.ComponentModel.DataAnnotations;

namespace KidPlan.Web.Models;

/// <summary>
/// 一条过敏原记录。
///
/// 之前过敏源是小孩档案里的两个自由文本字段（<see cref="Child.AllergicFoods"/> /
/// <see cref="Child.Allergies"/>）。问题有两个：
/// 1. 没法表达严重程度——「乳糖不耐受」和「花生过敏性休克」被同等对待，
///    结果就是警告天天弹，真正危险的那次反而不显眼；
/// 2. 没法结构化地被别处消费——AI 推荐要按等级决定「能不能出现这个食材」，
///    从一段文字里猜等级是不可靠的。
///
/// 所以拆成一张表，一条一行。
/// </summary>
public class ChildAllergen
{
    public int Id { get; set; }

    public int ChildId { get; set; }

    public Child? Child { get; set; }

    /// <summary>过敏原名字，例如「牛奶」「鸡蛋」「青霉素」「花粉」。</summary>
    [Required(ErrorMessage = "请填写过敏原")]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>食物还是非食物。只有食物会拿菜单和食材去比对。</summary>
    public AllergenKind Kind { get; set; } = AllergenKind.Food;

    /// <summary>严重程度。命中之后怎么处理完全由它决定。</summary>
    public AllergySeverity Severity { get; set; } = AllergySeverity.Moderate;

    /// <summary>备注：上次是什么反应、多久出现、医生怎么说、有没有带肾上腺素笔。</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>做过过敏原检测的话，把结果记在这里（如「IgE 3 级」「点刺 ++」）。</summary>
    [MaxLength(200)]
    public string? TestResult { get; set; }

    /// <summary>
    /// 旧版自由文本迁移过来的条目。
    /// 界面上会提示家长复核等级——迁移只能保证「不丢」，等级是猜的。
    /// </summary>
    public bool NeedsReview { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
