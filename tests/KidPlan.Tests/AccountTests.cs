using KidPlan.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// 账号体系（注册 / 登录凭据 / 改密码）依赖的 Identity 配置。
/// 真正的注册页面是静态 SSR 的 Razor 组件，这里验证它背后的 Identity 行为，
/// 保证「注册时只用邮箱+密码，不要求邮箱确认」这条线是可用的。
/// </summary>
public class AccountTests
{
    [Fact]
    public async Task Registration_StoresDisplayNameAndUsesEmailAsUserName()
    {
        await using var host = new TestHost();

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = "mama@example.com",
            Email = "mama@example.com",
            DisplayName = "妈妈",
            CreatedAt = host.Now,
        };

        var result = await users.CreateAsync(user, "kidplan123");

        Assert.True(result.Succeeded);
        Assert.Equal("妈妈", user.DisplayName);

        var found = await users.FindByEmailAsync("mama@example.com");
        Assert.NotNull(found);
        Assert.Equal("妈妈", found.DisplayName);
        Assert.Equal(host.Now, found.CreatedAt);
    }

    [Fact]
    public async Task Registration_RejectsDuplicateEmail()
    {
        await using var host = new TestHost();
        await host.CreateUserAsync("mama@example.com");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var duplicate = new ApplicationUser
        {
            UserName = "mama@example.com",
            Email = "mama@example.com",
        };

        var result = await users.CreateAsync(duplicate, "kidplan123");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));
    }

    [Fact]
    public async Task Registration_RequiresAtLeastSixCharacterPassword()
    {
        await using var host = new TestHost();

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = "mama@example.com", Email = "mama@example.com" };

        var result = await users.CreateAsync(user, "12345");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordTooShort));
    }

    [Fact]
    public async Task Registration_AcceptsSimplePassword_WithNoComplexityRules()
    {
        await using var host = new TestHost();

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // 六个纯数字：自建部署给长辈用，不该被复杂度规则挡住。
        var user = new ApplicationUser { UserName = "yeye@example.com", Email = "yeye@example.com" };

        Assert.True((await users.CreateAsync(user, "123456")).Succeeded);
    }

    [Fact]
    public async Task PasswordCheck_MatchesTheStoredHash()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("mama@example.com", "kidplan123");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);

        Assert.True(await users.CheckPasswordAsync(user!, "kidplan123"));
        Assert.False(await users.CheckPasswordAsync(user!, "wrong-password"));
    }

    [Fact]
    public async Task ChangePassword_ReplacesTheStoredCredential()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("mama@example.com", "kidplan123");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);

        var result = await users.ChangePasswordAsync(user!, "kidplan123", "newpassword");
        Assert.True(result.Succeeded);

        Assert.False(await users.CheckPasswordAsync(user!, "kidplan123"));
        Assert.True(await users.CheckPasswordAsync(user!, "newpassword"));
    }

    [Fact]
    public async Task ChangePassword_FailsWithWrongCurrentPassword()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("mama@example.com", "kidplan123");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        var result = await users.ChangePasswordAsync(user!, "not-the-password", "newpassword");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
    }

    [Fact]
    public async Task UpdateDisplayName_DoesNotTouchCredentials()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("mama@example.com", "kidplan123");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        user!.DisplayName = "外婆";

        Assert.True((await users.UpdateAsync(user)).Succeeded);

        var reloaded = await users.FindByIdAsync(userId);
        Assert.Equal("外婆", reloaded!.DisplayName);
        Assert.True(await users.CheckPasswordAsync(reloaded, "kidplan123"));
    }

    [Fact]
    public async Task TwoAccounts_AreNeverMixedUpByEmail()
    {
        await using var host = new TestHost();
        var first = await host.CreateUserAsync("mama@example.com");
        var second = await host.CreateUserAsync("baba@example.com");

        using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.NotEqual(first, second);
        Assert.Equal(first, (await users.FindByEmailAsync("mama@example.com"))!.Id);
        Assert.Equal(second, (await users.FindByEmailAsync("baba@example.com"))!.Id);
        Assert.Null(await users.FindByEmailAsync("nobody@example.com"));
    }
}
