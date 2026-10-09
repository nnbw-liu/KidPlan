using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

public interface IChildOverviewService
{
    /// <summary>组装首页仪表盘需要的一个小孩的概览数据。</summary>
    Task<ChildOverview?> BuildAsync(string userId, int childId, CancellationToken ct = default);

    /// <summary>组装首页要展示的所有小孩的概览数据。</summary>
    Task<IReadOnlyList<ChildOverview>> BuildAllAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// 把小孩资料、身体记录、饮食安排三块数据拼成首页需要的形态。
/// 单独放一个服务，避免 IChildService 和 IGrowthService 互相依赖形成循环。
/// </summary>
public sealed class ChildOverviewService(
    IChildService childService,
    IGrowthService growthService,
    IMealService mealService,
    TimeProvider timeProvider) : IChildOverviewService
{
    public async Task<ChildOverview?> BuildAsync(string userId, int childId, CancellationToken ct = default)
    {
        var child = await childService.FindAsync(childId, userId, ct);
        if (child is null)
        {
            return null;
        }

        return await BuildCoreAsync(userId, child, ct);
    }

    public async Task<IReadOnlyList<ChildOverview>> BuildAllAsync(string userId, CancellationToken ct = default)
    {
        var children = await childService.ListAsync(userId, includeArchived: false, ct: ct);
        var result = new List<ChildOverview>(children.Count);

        foreach (var child in children)
        {
            result.Add(await BuildCoreAsync(userId, child, ct));
        }

        return result;
    }

    private async Task<ChildOverview> BuildCoreAsync(string userId, Child child, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

        var growth = await growthService.BuildSummaryAsync(userId, child.Id, ct);
        var meals = await mealService.GetDayAsync(userId, child.Id, today, ct);

        return new ChildOverview(
            child,
            AgeCalculator.MonthsBetween(child.BirthDate, today),
            AgeCalculator.Describe(child.BirthDate, today),
            growth,
            meals);
    }
}
