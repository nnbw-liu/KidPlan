using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

/// <summary>
/// 成长曲线页的显示偏好。
///
/// 注册成 Scoped（每个浏览器连接一份）：这样从曲线页跳到饮食页再跳回来，
/// 「我勾了哪几个指标、是分图还是叠加」还在，不用每次重新点一遍。
///
/// 之所以不写进 URL 查询串：改查询串会触发 Blazor 的导航，
/// 整页参数重设 → 重新查库 → 状态被重置，反而更麻烦。
/// 想要「分享给家里人看同一组指标」的话，URL 参数仍然可以读（见 Growth.razor）。
/// </summary>
public sealed class GrowthViewState
{
    /// <summary>勾选了哪些指标。null 表示还没设置过，用默认值。</summary>
    public HashSet<GrowthMetric>? Metrics { get; set; }

    /// <summary>true = 叠加到一张图；false = 每个指标各一张图。</summary>
    public bool Overlay { get; set; }

    /// <summary>叠加模式下是否归一化（只比走势，不比绝对值）。</summary>
    public bool Normalize { get; set; }

    /// <summary>只看单次记录 / 只看时段记录；null = 都看。</summary>
    public RecordKind? KindFilter { get; set; }

    /// <summary>回看多少个月；null = 全部。</summary>
    public int? RangeMonths { get; set; } = 6;

    public bool UseCustomRange { get; set; }

    public DateOnly? CustomFrom { get; set; }

    public DateOnly? CustomTo { get; set; }

    /// <summary>
    /// 实际要画的指标，**按固定顺序**返回，且保证非空。
    ///
    /// 两个刻意的约束：
    /// ① 顺序固定（按 <see cref="GrowthMetrics.All"/>）：否则每次勾选都换一次排列，
    ///    图的位置跳来跳去，找同一个指标得重新扫一遍屏幕。
    /// ② 空了就退回默认三件套：一个都不勾会让整页变成空白，
    ///    比「点了没反应」更难理解。
    /// </summary>
    public IReadOnlyList<GrowthMetric> EffectiveMetrics
    {
        get
        {
            var selected = Metrics is null or { Count: 0 }
                ? GrowthMetrics.DefaultSelection.ToHashSet()
                : Metrics;

            return GrowthMetrics.All.Where(selected.Contains).ToList();
        }
    }
}
