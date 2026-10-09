using KidPlan.Web.Models;
using Microsoft.AspNetCore.Identity;

namespace KidPlan.Web.Data;

/// <summary>
/// 登录账号（家长）。一个账号下可以登记多个小孩，账号之间的数据互相不可见。
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>账号的显示名，注册时填写，没填就回退到邮箱前缀。</summary>
    [PersonalData]
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>该账号登记的所有小孩。</summary>
    public ICollection<Child> Children { get; set; } = new List<Child>();

    /// <summary>该账号的本地中继节点（自己电脑上的 KidPlan.Relay）。</summary>
    public ICollection<RelayNode> RelayNodes { get; set; } = new List<RelayNode>();
}
