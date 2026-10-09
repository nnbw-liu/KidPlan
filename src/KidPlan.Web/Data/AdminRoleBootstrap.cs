using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KidPlan.Web.Data;

/// <summary>
/// 角色名常量。目前只有一个角色：管理员。
/// </summary>
public static class KidPlanRoles
{
    /// <summary>管理员：能看到「模型设置」里的全局配置页签（会写服务器上的 config/ai.json）。</summary>
    public const string Admin = "admin";
}

/// <summary>启动时把管理员角色准备好，并打印一份「现在谁能改全局配置」的清单。</summary>
public sealed record AdminRoleBootstrapResult(
    IReadOnlyList<string> Admins,
    IReadOnlyList<string> Granted,
    IReadOnlyList<string> UnknownEmails);

/// <summary>
/// 管理员角色的落地方式。
///
/// 为什么需要这一步：项目里原本没有任何角色概念，「模型设置」的全局配置页签
/// 是所有登录账号都能看到、都能改的——而它改的是**服务器上的配置文件**，
/// 影响这个部署上的所有人。对家庭部署来说，家长账号之外的账号（比如给孩子开的）
/// 不该动它。
///
/// 角色是权威（授权判断只看 admin 角色），配置只是「怎么把角色发出去」的省事手段：
/// <c>Admin:Emails</c>（环境变量 <c>Admin__Emails</c>）里列出的邮箱，启动时会被
/// 加进 admin 角色。不想用配置也行，直接写库：
/// <code>
/// INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
///   VALUES (UUID(), 'admin', 'ADMIN', UUID());
/// INSERT INTO AspNetUserRoles (UserId, RoleId)
///   SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
///   WHERE u.Email = '你的邮箱' AND r.NormalizedName = 'ADMIN';
/// </code>
/// 注意：角色是写进登录 Cookie 的，改完要**重新登录**才生效。
/// </summary>
public static class AdminRoleBootstrap
{
    public static async Task<AdminRoleBootstrapResult> RunAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct = default)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        if (!await roleManager.RoleExistsAsync(KidPlanRoles.Admin))
        {
            var created = await roleManager.CreateAsync(new IdentityRole(KidPlanRoles.Admin));
            if (!created.Succeeded)
            {
                logger.LogError(
                    "创建 {Role} 角色失败：{Errors}。管理员相关的功能会不可用。",
                    KidPlanRoles.Admin,
                    string.Join("；", created.Errors.Select(e => e.Description)));
                return new AdminRoleBootstrapResult([], [], []);
            }

            logger.LogInformation("已创建 {Role} 角色。", KidPlanRoles.Admin);
        }

        // 配置里列的邮箱：有就加进角色，没有就记下来（多半是还没注册）。
        var configured = ParseEmails(configuration["Admin:Emails"]);
        var granted = new List<string>();
        var unknown = new List<string>();

        foreach (var email in configured)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                unknown.Add(email);
                continue;
            }

            if (!await userManager.IsInRoleAsync(user, KidPlanRoles.Admin))
            {
                var added = await userManager.AddToRoleAsync(user, KidPlanRoles.Admin);
                if (added.Succeeded)
                {
                    granted.Add(email);
                }
                else
                {
                    logger.LogError(
                        "把 {Email} 加进 {Role} 角色失败：{Errors}",
                        email,
                        KidPlanRoles.Admin,
                        string.Join("；", added.Errors.Select(e => e.Description)));
                }
            }
        }

        var admins = (await userManager.GetUsersInRoleAsync(KidPlanRoles.Admin))
            .Select(u => u.Email ?? u.UserName ?? u.Id)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (granted.Count > 0)
        {
            logger.LogInformation(
                "已把 {Count} 个账号设为管理员（Admin:Emails）：{Emails}。它们需要重新登录一次才会拿到新角色。",
                granted.Count,
                string.Join("、", granted));
        }

        if (unknown.Count > 0)
        {
            logger.LogWarning(
                "Admin:Emails 里这些邮箱在库里找不到对应账号，已跳过：{Emails}。" +
                "确认注册时用的就是这个邮箱，或者注册之后再重启一次应用。",
                string.Join("、", unknown));
        }

        if (admins.Count == 0)
        {
            logger.LogWarning(
                "当前没有任何管理员，所以「模型设置」里的全局配置页签谁都看不到（只能用管理员账号手工改 {Path}）。" +
                "要开放出来，设一个环境变量 Admin__Emails=你的登录邮箱 再重启；" +
                "或者按 docs/DEPLOY.md 里的 SQL 直接写 AspNetUserRoles 表。",
                "config/ai.json");
        }
        else
        {
            logger.LogInformation("当前管理员：{Admins}", string.Join("、", admins));
        }

        return new AdminRoleBootstrapResult(admins, granted, unknown);
    }

    /// <summary>
    /// 逗号 / 分号 / 空白分隔都认，大小写不敏感，去重。
    /// internal 是为了让单元测试直接盯着它：这段解析出错的表现是「明明配了管理员却没人能看到全局配置」，
    /// 而它本身跟数据库没关系，没必要为了可测就提升成 public API。
    /// </summary>
    internal static List<string> ParseEmails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
