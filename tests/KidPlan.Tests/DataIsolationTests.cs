using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>
/// 数据隔离：这是本项目最硬的约束——每个账号只能看到自己小孩的数据，
/// 所有业务查询都必须带 OwnerId 过滤。
/// </summary>
public class DataIsolationTests
{
    [Fact]
    public async Task ChildList_OnlyContainsOwnProfiles()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        await host.CreateChildAsync(alice, "糖糖");
        await host.CreateChildAsync(bob, "豆豆");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var aliceList = await children.ListAsync(alice);
        var bobList = await children.ListAsync(bob);

        Assert.Single(aliceList);
        Assert.Equal("糖糖", aliceList[0].Alias);
        Assert.Single(bobList);
        Assert.Equal("豆豆", bobList[0].Alias);
    }

    [Fact]
    public async Task Find_ReturnsNullForSomeoneElsesChild()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        Assert.NotNull(await children.FindAsync(childId, alice));
        Assert.Null(await children.FindAsync(childId, bob));
    }

    [Fact]
    public async Task Require_ThrowsForSomeoneElsesChild()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var ex = await Assert.ThrowsAsync<ChildAccessDeniedException>(() => children.RequireAsync(childId, bob));
        Assert.Equal(childId, ex.ChildId);
    }

    [Fact]
    public async Task CannotReadGrowthRecordsOfSomeoneElsesChild()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();

        var record = await growth.AddAsync(alice, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 90m,
        });

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.ListAsync(bob, childId));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.BuildChartAsync(bob, childId));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.BuildSummaryAsync(bob, childId));
        Assert.Null(await growth.FindAsync(bob, record.Id));
    }

    [Fact]
    public async Task CannotAddGrowthRecordToSomeoneElsesChild()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.AddAsync(bob, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 88m,
        }));
    }

    [Fact]
    public async Task CannotModifyOrDeleteSomeoneElsesGrowthRecord()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();

        var record = await growth.AddAsync(alice, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 90m,
        });

        record.HeightCm = 200m;

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.UpdateAsync(bob, record));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => growth.DeleteAsync(bob, record.Id));

        // 原始数据必须没被改掉。
        var reloaded = await growth.FindAsync(alice, record.Id);
        Assert.Equal(90m, reloaded!.HeightCm);
    }

    [Fact]
    public async Task CannotReadOrConfirmSomeoneElsesMeal()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var meal = await meals.AddAsync(alice, childId, new MealPlan
        {
            Date = new DateOnly(2026, 10, 4),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "番茄鸡蛋面",
        });

        await Assert.ThrowsAsync<ChildAccessDeniedException>(
            () => meals.GetDayAsync(bob, childId, new DateOnly(2026, 10, 4)));
        Assert.Null(await meals.FindAsync(bob, meal.Id));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => meals.MarkEatenAsync(bob, meal.Id));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => meals.MarkSkippedAsync(bob, meal.Id));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => meals.ReopenAsync(bob, meal.Id));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => meals.DeleteAsync(bob, meal.Id));

        // 确认状态没有被别人改掉。
        var stillPlanned = await meals.FindAsync(alice, meal.Id);
        Assert.Equal(MealStatus.Planned, stillPlanned!.Meal.Status);
    }

    [Fact]
    public async Task CannotAddMealToSomeoneElsesChild()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => meals.AddAsync(bob, childId, new MealPlan
        {
            Date = new DateOnly(2026, 10, 4),
            MealType = MealType.Dinner,
            PlannedTime = new TimeOnly(18, 0),
            Menu = "小米粥",
        }));
    }

    [Fact]
    public async Task DeletingSomeoneElsesChildHasNoEffect()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => children.DeleteAsync(bob, childId));

        var stillThere = await children.FindAsync(childId, alice);
        Assert.NotNull(stillThere);
    }

    [Fact]
    public async Task OverviewAndStats_OnlyCoverOwnChildren()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");

        var aliceChild = await host.CreateChildAsync(alice, "糖糖");
        await host.CreateChildAsync(bob, "豆豆");
        await host.CreateChildAsync(bob, "果果");

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();
        var overview = scope.ServiceProvider.GetRequiredService<IChildOverviewService>();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        await growth.AddAsync(alice, aliceChild, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 90m,
        });

        var aliceOverviews = await overview.BuildAllAsync(alice);
        var bobItems = await children.ListWithStatsAsync(bob);

        Assert.Single(aliceOverviews);
        Assert.Equal(1, aliceOverviews[0].Growth!.RecordCount);
        Assert.Equal(2, bobItems.Count);
        Assert.All(bobItems, item => Assert.Equal(0, item.GrowthRecordCount));
    }

    [Fact]
    public async Task DeletingAChildCascadesToItsRecordsAndMeals()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        await growth.AddAsync(alice, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 90m,
        });
        await meals.AddAsync(alice, childId, new MealPlan
        {
            Date = new DateOnly(2026, 10, 4),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "小米粥",
        });

        await children.DeleteAsync(alice, childId);

        Assert.Empty(await children.ListAsync(alice));

        using var checkScope = host.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<KidPlan.Web.Data.ApplicationDbContext>();
        Assert.Empty(db.GrowthRecords);
        Assert.Empty(db.MealPlans);
    }
}
