using System.ComponentModel.DataAnnotations;
using KidPlan.Web.Data;

namespace KidPlan.Web.Models;

/// <summary>
/// 一个小孩。这是系统的核心个体：所有身体记录、饮食安排都挂在某个 Child 下面，
/// 并通过 <see cref="OwnerId"/> 归属到某个登录账号，实现账号之间的数据隔离。
/// </summary>
public class Child
{
    public int Id { get; set; }

    /// <summary>归属账号（AspNetUsers.Id）。</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerId { get; set; } = string.Empty;

    public ApplicationUser? Owner { get; set; }

    /// <summary>别名 / 小名，必填，列表和顶部切换器都用它显示。</summary>
    [Required(ErrorMessage = "请填写小孩的别名")]
    [MaxLength(50)]
    public string Alias { get; set; } = string.Empty;

    /// <summary>姓名（可选）。</summary>
    [MaxLength(50)]
    public string? FullName { get; set; }

    /// <summary>出生年月。用 DateOnly 存储为 MySQL 的 date。</summary>
    public DateOnly BirthDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public Gender Gender { get; set; } = Gender.Unspecified;

    /// <summary>爱好。</summary>
    [MaxLength(500)]
    public string? Hobbies { get; set; }

    /// <summary>
    /// 【旧字段，已不再读写】过敏的东西（非食物类）：花粉、尘螨、青霉素、动物毛发等。
    ///
    /// 分级改造后过敏源统一放在 <see cref="Allergens"/> 表里。这两列刻意保留：
    /// 迁移只做「搬过去」，不删原文，万一要回退版本或者搬错了还能捞回来。
    /// 界面上不再显示、也不参与任何匹配。确认新机制稳定后可以再出一个迁移删掉。
    /// </summary>
    [MaxLength(500)]
    public string? Allergies { get; set; }

    /// <summary>【旧字段，已不再读写】过敏的食物。见 <see cref="Allergies"/> 的说明。</summary>
    [MaxLength(500)]
    public string? AllergicFoods { get; set; }

    /// <summary>喜欢的运动。</summary>
    [MaxLength(500)]
    public string? FavoriteSports { get; set; }

    /// <summary>血型（可选）。</summary>
    [MaxLength(10)]
    public string? BloodType { get; set; }

    /// <summary>头像用一个 emoji 表示，避免上传文件带来的存储和部署复杂度。</summary>
    [MaxLength(8)]
    public string? AvatarEmoji { get; set; }

    /// <summary>其他需要记的备注（如既往病史、常服药物）。</summary>
    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>归档后不再出现在默认列表中，但数据保留。</summary>
    public bool IsArchived { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public ICollection<GrowthRecord> GrowthRecords { get; set; } = new List<GrowthRecord>();

    public ICollection<MealPlan> MealPlans { get; set; } = new List<MealPlan>();

    /// <summary>过敏原（含严重程度）。饮食校验和 AI 推荐都以它为准。</summary>
    public ICollection<ChildAllergen> Allergens { get; set; } = new List<ChildAllergen>();
}
