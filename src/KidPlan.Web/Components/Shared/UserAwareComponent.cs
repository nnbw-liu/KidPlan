using KidPlan.Web.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace KidPlan.Web.Components.Shared;

/// <summary>
/// 需要知道“当前登录账号是谁”的页面/组件的基类。
/// 所有业务查询都要带 userId 做数据隔离，集中在这里取一次，避免每个页面重复写。
/// </summary>
public abstract class UserAwareComponent : ComponentBase
{
    private string? _userId;

    [Inject]
    protected AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    protected UserManager<ApplicationUser> UserManager { get; set; } = default!;

    /// <summary>当前登录账号的 Id。</summary>
    protected async Task<string> GetUserIdAsync()
    {
        if (_userId is not null)
        {
            return _userId;
        }

        var state = await AuthStateProvider.GetAuthenticationStateAsync();
        _userId = UserManager.GetUserId(state.User)
            ?? throw new InvalidOperationException("当前请求没有登录账号。");

        return _userId;
    }

    /// <summary>当前登录账号的显示名，用于界面问候语。</summary>
    protected async Task<string> GetDisplayNameAsync()
    {
        var state = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = await UserManager.GetUserAsync(state.User);
        return user?.DisplayName
            ?? user?.UserName
            ?? state.User.Identity?.Name
            ?? "你";
    }
}
