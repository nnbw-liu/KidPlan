using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidPlan.Tests;

/// <summary>
/// 旧的自由文本过敏源 → 分级记录的一次性搬迁。
///
/// 这是本项目里唯一会**自动改动用户既有数据**的地方，所以三条性质都得钉住：
/// 幂等（跑多少次都只搬一次）、保守（等级猜高不猜低）、
/// 不覆盖（家长自己加过的条目不会被旧文本盖掉）。
/// </summary>
public class AllergenBackfillTests
{
    /// <summary>直接把旧列写进去——那两列已经不在任何界面上，只能绕过服务层造数据。</summary>
    private static async Task SeedLegacyTextAsync(
        TestHost host,
        int childId,
        string? allergicFoods,
        string? allergies)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var child = await db.Children.FirstAsync(c => c.Id == childId);
        child.AllergicFoods = allergicFoods;
        child.Allergies = allergies;

        await db.SaveChangesAsync();
    }

    private static async Task<int> RunAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await AllergenBackfill.RunAsync(db, NullLogger.Instance);
    }

    private static async Task<List<ChildAllergen>> AllergensAsync(TestHost host, int childId)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ChildAllergens
            .Where(a => a.ChildId == childId)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Run_SplitsLegacyTextIntoGradedRecords()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        await SeedLegacyTextAsync(host, childId, "牛奶、鸡蛋，花生", "花粉、青霉素");

        var added = await RunAsync(host);

        Assert.Equal(5, added);

        var allergens = await AllergensAsync(host, childId);

        Assert.Equal(3, allergens.Count(a => a.Kind == AllergenKind.Food));
        Assert.Equal(2, allergens.Count(a => a.Kind == AllergenKind.Other));
        Assert.Contains(allergens, a => a.Name == "牛奶" && a.Kind == AllergenKind.Food);
        Assert.Contains(allergens, a => a.Name == "花粉" && a.Kind == AllergenKind.Other);

        // 非食物过敏原不会被拿去比对菜单，分类必须正确，否则「花粉」会在「花卷」上天天误报。
        Assert.DoesNotContain(allergens, a => a.Kind == AllergenKind.Other && a.Name == "牛奶");
    }

    [Fact]
    public async Task Run_DefaultsToSevereAndFlagsForReview()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        await SeedLegacyTextAsync(host, childId, "牛奶", null);
        await RunAsync(host);

        var allergen = Assert.Single(await AllergensAsync(host, childId));

        // 等级是猜的，所以宁可猜高：猜低会漏掉真正的危险，猜高只是多一次确认。
        Assert.Equal(AllergySeverity.Severe, allergen.Severity);
        Assert.True(allergen.NeedsReview);
    }

    [Fact]
    public async Task Run_IsIdempotent()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        await SeedLegacyTextAsync(host, childId, "牛奶、鸡蛋", null);

        Assert.Equal(2, await RunAsync(host));
        Assert.Equal(0, await RunAsync(host));
        Assert.Equal(0, await RunAsync(host));

        Assert.Equal(2, (await AllergensAsync(host, childId)).Count);
    }

    [Fact]
    public async Task Run_DoesNotTouchChildrenThatAlreadyHaveGradedRecords()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖", allergicFoods: "牛奶");

        // 家长已经在界面上自己录过了，旧文本就是历史残留，绝不能盖回去。
        await SeedLegacyTextAsync(host, childId, "牛奶、鸡蛋、花生", null);

        Assert.Equal(0, await RunAsync(host));

        var allergens = await AllergensAsync(host, childId);
        Assert.Equal("牛奶", Assert.Single(allergens).Name);
    }

    [Fact]
    public async Task Run_KeepsTheLegacyColumnsAsAnArchive()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        await SeedLegacyTextAsync(host, childId, "牛奶、鸡蛋", "花粉");
        await RunAsync(host);

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var child = await db.Children.AsNoTracking().FirstAsync(c => c.Id == childId);

        // 迁移前的原文保留着：万一拆分规则有偏差，还能人工对照。
        Assert.Equal("牛奶、鸡蛋", child.AllergicFoods);
        Assert.Equal("花粉", child.Allergies);
    }

    [Fact]
    public async Task Run_HandlesEveryChildIndependently()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var first = await host.CreateChildAsync(userId, "糖糖");
        var second = await host.CreateChildAsync(userId, "念念");
        var plain = await host.CreateChildAsync(userId, "豆豆");

        await SeedLegacyTextAsync(host, first, "牛奶", null);

        // 念念已经录过了，豆豆则完全没有过敏记录——两种都不该被搬。
        await host.CreateScope().ServiceProvider.GetRequiredService<KidPlan.Web.Services.IChildService>()
            .AddAllergensFromTextAsync(userId, second, "芒果", AllergenKind.Food, AllergySeverity.Moderate);

        var added = await RunAsync(host);

        Assert.Equal(1, added);
        Assert.Equal("牛奶", Assert.Single(await AllergensAsync(host, first)).Name);
        Assert.Equal("芒果", Assert.Single(await AllergensAsync(host, second)).Name);
        Assert.Empty(await AllergensAsync(host, plain));
    }

    [Fact]
    public async Task Run_DoesNothingWhenThereIsNoLegacyText()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        await host.CreateChildAsync(userId, "糖糖");

        Assert.Equal(0, await RunAsync(host));
    }
}
