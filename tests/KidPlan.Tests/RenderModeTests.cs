using System.Net;
using System.Reflection;
using KidPlan.Web.Components.Account.Pages;
using KidPlan.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace KidPlan.Tests;

/// <summary>
/// 渲染模式的真实 HTTP 回归测试（不需要数据库）。
///
/// 这是本项目最容易被无声改坏、而且一坏就致命的一处：
/// 账号页（注册 / 登录 / 改密码）必须走**静态 SSR**——它们要在请求-响应周期里
/// 由 SignInManager 读写认证 Cookie，而交互式渲染跑在 SignalR circuit 上、
/// 那里没有 HttpContext，写 Cookie 必然失败，用户就永远登录不上。
///
/// 光给账号页加 [ExcludeFromInteractiveRouting] 只够挡住交互式导航，
/// 初始请求还得靠 App.razor 里的 PageRenderMode 返回 null 才真正静态渲染。
/// 所以这里直接断言响应里有没有交互式渲染标记。
/// </summary>
public class RenderModeTests : IClassFixture<RenderModeTests.StaticSsrFactory>
{
    /// <summary>交互式服务端渲染会在 HTML 里留下这个标记；静态 SSR 不会。</summary>
    private const string InteractiveMarker = "<!--Blazor:{\"type\":\"server\"";

    private readonly StaticSsrFactory _factory;

    public RenderModeTests(StaticSsrFactory factory) => _factory = factory;

    /// <summary>
    /// 整个测试类共享一个宿主。刻意关掉自动迁移：这些用例不碰数据库，
    /// 本机没有 MySQL 也应该能跑。
    /// </summary>
    public sealed class StaticSsrFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // 用内存配置源覆盖 appsettings.json 里的 Database:AutoMigrate=true，
            // 否则启动时会真的去连 MySQL（本机通常没有）。
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:AutoMigrate"] = "false" }));
        }
    }

    [Theory]
    [InlineData("/account/login")]
    [InlineData("/account/register")]
    public async Task AccountPages_AreRenderedWithStaticSsr(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(InteractiveMarker, html);
        // 表单纯粹靠 HTTP POST 提交，所以必须真的有个指向自己的 form + 防伪令牌。
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task NotFoundPage_IsRenderedInteractively()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/no-such-page");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // 非账号页（含首页、小孩档案、成长、饮食）都走交互式渲染，否则按钮全是死的。
        Assert.Contains(InteractiveMarker, html);
    }

    [Fact]
    public async Task ProtectedPages_RedirectAnonymousVisitorsToLogin()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/children");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // TestServer 给的 Location 是绝对 URI，这里只看路径与查询串。
        var location = response.Headers.Location!;
        Assert.Equal("/account/login", location.AbsolutePath);
        Assert.Contains("ReturnUrl", location.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountPages_CarryTheExcludeAttribute_ThatDrivesTheDecision()
    {
        // App.razor 用 HttpContext.AcceptsInteractiveRouting() 判断，它读的就是这个特性。
        Type[] accountPages =
        [
            typeof(Login),
            typeof(Register),
            typeof(AccessDenied),
            typeof(Lockout),
            typeof(InvalidUser),
            typeof(KidPlan.Web.Components.Account.Pages.Manage.Index),
            typeof(KidPlan.Web.Components.Account.Pages.Manage.ChangePassword),
        ];

        foreach (var page in accountPages)
        {
            Assert.NotNull(page.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>());
        }
    }

    [Fact]
    public void BusinessPages_DoNotCarryTheExcludeAttribute()
    {
        Type[] businessPages =
        [
            typeof(Home),
            typeof(Children),
            typeof(ChildEdit),
            typeof(Body),
            typeof(Growth),
            typeof(Meals),
            typeof(MealHistory),
        ];

        foreach (var page in businessPages)
        {
            Assert.Null(page.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>());
        }
    }
}
