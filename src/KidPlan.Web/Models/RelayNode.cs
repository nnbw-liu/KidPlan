using System.ComponentModel.DataAnnotations;
using KidPlan.Web.Data;

namespace KidPlan.Web.Models;

/// <summary>
/// 一个「本地中继」节点：在你自己的电脑上跑着的 <c>KidPlan.Relay</c> 小程序。
///
/// 它主动连到云端服务器，所以服务器不需要能访问你家网络——
/// 这也是手机在外面用 4G 也能调用家里模型的原因：手机只跟云端说话。
/// </summary>
public class RelayNode
{
    public int Id { get; set; }

    /// <summary>归属账号。中继是「某个人的电脑」，不跨账号共享。</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerId { get; set; } = string.Empty;

    public ApplicationUser? Owner { get; set; }

    /// <summary>给自己认的名字，例如「书房的台式机」。</summary>
    [Required(ErrorMessage = "请给这个中继起个名字")]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 令牌的 SHA-256（Base64）。**明文只在创建时显示一次**，
    /// 库被拖走也不能直接拿去连服务器。
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>令牌前 8 位，纯粹为了在界面上区分「是哪一把」。</summary>
    [MaxLength(16)]
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>中继自己上报的机器名，方便对上是哪台机器。</summary>
    [MaxLength(100)]
    public string? MachineName { get; set; }

    /// <summary>中继上报的版本号。</summary>
    [MaxLength(30)]
    public string? AgentVersion { get; set; }

    /// <summary>最近一次心跳。太久没心跳就认为掉线了。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>禁用后令牌立即失效，长连接会被踢掉。</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
