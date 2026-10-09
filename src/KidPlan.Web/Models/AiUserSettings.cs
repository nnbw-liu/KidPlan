using System.ComponentModel.DataAnnotations;
using KidPlan.Web.Data;

namespace KidPlan.Web.Models;

/// <summary>
/// 某个账号对全局 AI 配置的覆盖，按账号一行，内容是一段 JSON
/// （<see cref="AiSettingsOverride"/> 的序列化结果）。
///
/// 为什么整份塞 JSON 而不是拆成 AiEndpoints / AiModels 两张表：
/// 这是「配置」不是「业务数据」，没有按字段查询的需求，
/// 拆表只会让「合并全局 + 账号覆盖」这件事变得很难读，而 JSON 一眼就能看懂。
/// </summary>
public class AiUserSettings
{
    /// <summary>归属账号（AspNetUsers.Id），同时也是主键：一个账号只有一份覆盖。</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerId { get; set; } = string.Empty;

    public ApplicationUser? Owner { get; set; }

    /// <summary>覆盖内容（JSON）。空字符串表示「没有任何覆盖，完全跟随全局」。</summary>
    public string OverrideJson { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
