using KidPlan.Web.Models;
using KidPlan.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Tests;

/// <summary>小孩档案的增删改查、校验与归档。</summary>
public class ChildServiceTests
{
    [Fact]
    public async Task Create_FillsOwnerAndKeepsAllRegistrationFields()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var created = await children.CreateAsync(userId, new Child
        {
            Alias = "糖糖",
            FullName = "王糖糖",
            BirthDate = new DateOnly(2023, 6, 1),
            Gender = Gender.Girl,
            Hobbies = "拼积木、画画",
            Allergies = "花粉、青霉素",
            AllergicFoods = "牛奶、鸡蛋",
            FavoriteSports = "骑车、游泳",
            BloodType = "O",
            AvatarEmoji = "🐰",
            Notes = "对尘螨也比较敏感",
        });

        Assert.True(created.Id > 0);
        Assert.Equal(userId, created.OwnerId);

        var reloaded = await children.FindAsync(created.Id, userId);
        Assert.NotNull(reloaded);
        Assert.Equal("糖糖", reloaded.Alias);
        Assert.Equal("王糖糖", reloaded.FullName);
        Assert.Equal(new DateOnly(2023, 6, 1), reloaded.BirthDate);
        Assert.Equal(Gender.Girl, reloaded.Gender);
        Assert.Equal("拼积木、画画", reloaded.Hobbies);
        Assert.Equal("骑车、游泳", reloaded.FavoriteSports);
        Assert.Equal("O", reloaded.BloodType);
        Assert.Equal("🐰", reloaded.AvatarEmoji);
        Assert.Equal("对尘螨也比较敏感", reloaded.Notes);
        Assert.False(reloaded.IsArchived);

        // 过敏原已经拆成一条条带等级的记录，旧的自由文本字段不再读写。
        // 一次性把两类过敏原都灌进去：这正好覆盖「旧文本迁移」走的那条路径。
        await children.AddAllergensFromTextAsync(userId, created.Id, "牛奶、鸡蛋", AllergenKind.Food, AllergySeverity.Severe);
        await children.AddAllergensFromTextAsync(userId, created.Id, "花粉、青霉素", AllergenKind.Other, AllergySeverity.Moderate);

        var allergens = await children.ListAllergensAsync(created.Id, userId);

        Assert.Equal(4, allergens.Count);
        Assert.Contains(allergens, a => a.Name == "牛奶" && a.Kind == AllergenKind.Food && a.Severity == AllergySeverity.Severe);
        Assert.Contains(allergens, a => a.Name == "花粉" && a.Kind == AllergenKind.Other && a.Severity == AllergySeverity.Moderate);

        // 重的排前面，这样界面不用再排一次。
        Assert.Equal(AllergySeverity.Severe, allergens[0].Severity);
    }

    [Fact]
    public async Task Create_RejectsMissingAlias()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        await Assert.ThrowsAsync<ArgumentException>(() => children.CreateAsync(userId, new Child
        {
            Alias = "   ",
            BirthDate = new DateOnly(2023, 6, 1),
        }));
    }

    [Fact]
    public async Task Create_RejectsImplausibleBirthDate()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var future = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        await Assert.ThrowsAsync<ArgumentException>(() => children.CreateAsync(userId, new Child
        {
            Alias = "未来的小孩",
            BirthDate = future,
        }));

        var tooOld = DateOnly.FromDateTime(DateTime.Today).AddYears(-26);
        await Assert.ThrowsAsync<ArgumentException>(() => children.CreateAsync(userId, new Child
        {
            Alias = "太老的小孩",
            BirthDate = tooOld,
        }));
    }

    [Fact]
    public async Task Update_CannotStealOwnershipOrChangeCreationTime()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");
        var childId = await host.CreateChildAsync(alice, "糖糖");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var original = await children.FindAsync(childId, alice);

        // 模拟被构造过的表单：试图把 OwnerId 改成别人、并伪造创建时间。
        var tampered = new Child
        {
            Id = childId,
            Alias = "糖糖",
            BirthDate = original!.BirthDate,
            OwnerId = bob,
            CreatedAt = DateTimeOffset.MinValue,
        };

        await children.UpdateAsync(alice, tampered);

        var reloaded = await children.FindAsync(childId, alice);
        Assert.NotNull(reloaded);
        Assert.Equal(alice, reloaded.OwnerId);
        Assert.Equal(original.CreatedAt, reloaded.CreatedAt);
    }

    [Fact]
    public async Task Update_TrimsTextAndConvertsBlankToNull()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var childId = await host.CreateChildAsync(userId, "糖糖");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var child = await children.FindAsync(childId, userId);
        child!.Alias = "  糖糖  ";
        child.Hobbies = "   ";
        child.Notes = "  换了一家医院  ";

        await children.UpdateAsync(userId, child);

        var reloaded = await children.FindAsync(childId, userId);
        Assert.Equal("糖糖", reloaded!.Alias);
        Assert.Null(reloaded.Hobbies);
        Assert.Equal("换了一家医院", reloaded.Notes);
    }

    [Fact]
    public async Task List_HidesArchivedByDefault_AndCanIncludeThem()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");
        var first = await host.CreateChildAsync(userId, "糖糖");
        await host.CreateChildAsync(userId, "豆豆");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        await children.SetArchivedAsync(userId, first, true);

        var visible = await children.ListAsync(userId);
        var all = await children.ListAsync(userId, includeArchived: true);

        Assert.Single(visible);
        Assert.Equal("豆豆", visible[0].Alias);
        Assert.Equal(2, all.Count);
        Assert.Equal(1, await children.CountAsync(userId));

        await children.SetArchivedAsync(userId, first, false);
        Assert.Equal(2, (await children.ListAsync(userId)).Count);
        Assert.Equal(2, await children.CountAsync(userId));
    }

    [Fact]
    public async Task ListWithStats_CountsRecordsAndMeals()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();
        var growth = scope.ServiceProvider.GetRequiredService<IGrowthService>();
        var meals = scope.ServiceProvider.GetRequiredService<IMealService>();

        var childId = await host.CreateChildAsync(userId, "糖糖", birthDate: new DateOnly(2023, 6, 1));

        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now,
            HeightCm = 90.5m,
            WeightKg = 13.2m,
        });
        await growth.AddAsync(userId, childId, new GrowthRecord
        {
            Kind = RecordKind.Point,
            RecordedAt = host.Now.AddDays(-10),
            HeightCm = 89m,
            WeightKg = 12.8m,
        });
        await meals.AddAsync(userId, childId, new MealPlan
        {
            Date = new DateOnly(2026, 10, 4),
            MealType = MealType.Lunch,
            PlannedTime = new TimeOnly(12, 0),
            Menu = "番茄鸡蛋面",
        });

        var item = Assert.Single(await children.ListWithStatsAsync(userId));

        Assert.Equal(2, item.GrowthRecordCount);
        Assert.Equal(1, item.MealPlanCount);
        Assert.Equal(90.5m, item.LatestHeightCm);
        Assert.Equal(13.2m, item.LatestWeightKg);
        Assert.Equal(DateOnly.FromDateTime(host.Now.DateTime), item.LatestRecordDate);
    }

    [Fact]
    public async Task Count_IsZeroForBrandNewAccount()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        using var scope = host.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        Assert.Equal(0, await children.CountAsync(userId));
        Assert.Empty(await children.ListAsync(userId));
    }
}
