using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using KidPlan.Web.Data;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// 账号相关 Razor 组件（Components/Account/Pages）需要的最小端点集合。
///
/// 说明：本项目移除了模板自带的 Passkey 与外部登录端点——MySQL 生态当前无法支持
/// .NET 10 的 Passkey 存储，而且这是家庭自用系统，不需要第三方登录。
/// 邮箱确认、忘记密码、重置密码都走 Components/Account/Pages 下的静态 SSR 页面
/// （它们用 EditForm + [SupplyParameterFromForm]，不需要额外端点）。
/// 这里保留的只有退出登录。
/// </summary>
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        // 退出登录必须是 POST + 防伪令牌，避免 <img src="/Account/Logout"> 这种 CSRF 强制登出。
        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string returnUrl) =>
        {
            await signInManager.SignOutAsync();

            // 只允许跳到本站相对路径，防止被构造成开放重定向。
            if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                || returnUrl.StartsWith("//", StringComparison.Ordinal))
            {
                returnUrl = "/";
            }

            return TypedResults.LocalRedirect($"~/{returnUrl.TrimStart('/')}");
        });

        return accountGroup;
    }
}
