using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// 路由与导航的「有没有断链」回归。
///
/// 起因很实际：侧边栏的每一项都是手写的 URL 字符串，
/// 而页面上的 <c>@page</c> 是另一个地方手写的模板。两处改得不同步的话
/// 表现是「点进去 404」，而且在开发机上不一定马上发现。
/// 这里把两者对起来：菜单里的每个 URL 必须能匹配到一条真实存在的路由。
///
/// 顺带钉住几个新页面的路由——身体管理、成长曲线、过敏原管理、模型设置、本机中继
/// 都是这次新加的，路径很容易在后续重构里被顺手改掉。
/// </summary>
public class NavigationTests : IClassFixture<NavigationTests.NavigationFactory>
{
    private readonly NavigationFactory _factory;

    public NavigationTests(NavigationFactory factory) => _factory = factory;

    public sealed class NavigationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:AutoMigrate"] = "false" }));
    }

    /// <summary>所有已注册的路由模板（Blazor 的 @page 也会注册成端点）。</summary>
    private static List<string> Routes(WebApplicationFactory<Program> factory)
        => factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => "/" + r!.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>把 <c>/children/{ChildId:int}/allergens</c> 这样的模板变成可匹配具体 URL 的正则。</summary>
    private static Regex TemplateToRegex(string template)
    {
        // Regex.Escape 只会转义 '{'，不会转义 '}'，所以这里只认前一个反斜杠。
        var pattern = "^" + Regex.Replace(
            Regex.Escape(template),
            @"\\\{[^}]*\}",
            "[^/]+") + "/?$";

        return new Regex(pattern, RegexOptions.IgnoreCase);
    }

    private static bool Matches(string url, IEnumerable<string> templates)
    {
        var path = url.Split('?')[0];

        // 首页要精确匹配，否则 "/" 的正则会匹配一切。
        return templates.Any(t => TemplateToRegex(t).IsMatch(path));
    }

    /// <summary>读 NavMenu.razor 源文件，把 Href 抠出来。</summary>
    private static List<string> MenuLinks()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "src", "KidPlan.Web", "Components", "Layout", "NavMenu.razor");

        Assert.True(File.Exists(path), $"找不到 {path}");

        var text = File.ReadAllText(path);

        // Href="/children" 与 Href="@($"/children/{childId}/body")" 两种写法都要覆盖。
        var links = Regex.Matches(text, @"Href=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // 带插值的写法统一换成 id=1 的具体 URL 再来比对。
        return links
            .Select(l => l.Replace("@($\"", string.Empty).Replace("\")", string.Empty))
            .Select(l => l.Replace("{childId}", "1"))
            .Where(l => l.StartsWith('/'))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KidPlan.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void EveryMenuLink_PointsAtARealRoute()
    {
        var templates = Routes(_factory);
        var links = MenuLinks();

        Assert.NotEmpty(links);

        var broken = links.Where(link => !Matches(link, templates)).ToList();

        Assert.True(
            broken.Count == 0,
            "侧边栏里这些链接没有对应的页面（点进去会 404）：" + string.Join("、", broken));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/children")]
    [InlineData("/children/1/body")]
    [InlineData("/children/1/growth")]
    [InlineData("/children/1/meals")]
    [InlineData("/children/1/meal-history")]
    [InlineData("/children/1/allergens")]
    [InlineData("/children/1/edit")]
    [InlineData("/settings/ai")]
    [InlineData("/settings/relay")]
    [InlineData("/account/manage")]
    public void ExpectedRoutes_AreRegistered(string url)
    {
        Assert.True(Matches(url, Routes(_factory)), $"路由 {url} 没有注册。");
    }

    [Fact]
    public void BodyAndGrowthPages_KeepTheirSeparateJobs()
    {
        // 这两个页面以前挤在一起，拆分之后很容易又顺手把记录表单塞回曲线页。
        // 这里从源码层面钉住分工：身体管理负责「记 / 改 / 删 + 列表」，
        // 成长曲线只负责「画」，两边还要能互相跳。
        var body = PageSource("Body.razor");
        var growth = PageSource("Growth.razor");

        Assert.Contains("@page \"/children/{ChildId:int}/body\"", body);
        Assert.Contains("记录身体情况", body);
        Assert.Contains("全部记录", body);
        Assert.Contains("较上次", body);            // 最新数值摘要卡片在本页
        Assert.Contains("/children/{ChildId}/growth", body);

        Assert.Contains("@page \"/children/{ChildId:int}/growth\"", growth);
        Assert.Contains("选择要画的指标", growth);
        Assert.Contains("/children/{ChildId}/body", growth);

        Assert.DoesNotContain("新增一条记录", growth);
        Assert.DoesNotContain("保存记录", growth);
        Assert.DoesNotContain("_showForm", growth);
        Assert.DoesNotContain("<MudSimpleTable", growth);   // 记录表格整体搬走了
    }

    private static string PageSource(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "KidPlan.Web", "Components", "Pages", fileName);

        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public async Task NewSettingsPages_AreReachableByAuthenticatedRoute()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 未登录时应该是「重定向到登录页」而不是 500 —— 500 说明页面在预渲染阶段就抛了。
        foreach (var url in new[] { "/settings/ai", "/settings/relay", "/children/1/allergens" })
        {
            var response = await client.GetAsync(url);

            Assert.True(
                response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
                $"{url} 返回了 {(int)response.StatusCode}，说明页面在预渲染时出错了。");
        }
    }
}
