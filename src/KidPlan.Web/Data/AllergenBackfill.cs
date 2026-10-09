using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Data;

/// <summary>
/// 把旧的自由文本过敏源搬迁到分级表里。
///
/// 为什么用 C# 而不是写进迁移脚本：MySQL 8 没有 split 函数，
/// 在 SQL 里按「,，、;；/\| 空格」切一串中文，写出来没人敢维护，
/// 也没法单测。放在这里跑，逻辑一目了然，还能用 SQLite 跑测试。
///
/// 判据是「这个小孩一条分级记录都没有」，所以：
/// - 幂等：跑多少次都只搬一次；
/// - 不会误伤：家长自己在界面上加的条目只要存在，就不会被旧文本盖掉。
///
/// 旧列（Allergies / AllergicFoods）刻意**不清空**，留作迁移前的存档。
/// </summary>
public static class AllergenBackfill
{
    /// <summary>
    /// 迁移默认等级。
    ///
    /// 取「强过敏」而不是「中等」是刻意的保守选择：旧机制下命中就要勾确认才能保存，
    /// 相当于原等级至少是「中等」；而这里给出的等级全是猜的，猜低了会漏掉真正的危险，
    /// 猜高了只是多一次确认。家长可以在界面上把等级调下去（调过就自动标记为已复核）。
    /// </summary>
    private const AllergySeverity DefaultSeverity = AllergySeverity.Severe;

    public static async Task<int> RunAsync(
        ApplicationDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        var children = await db.Children
            .Where(c => (c.AllergicFoods != null && c.AllergicFoods != string.Empty)
                || (c.Allergies != null && c.Allergies != string.Empty))
            .Select(c => new { c.Id, c.Alias, c.AllergicFoods, c.Allergies })
            .ToListAsync(ct);

        if (children.Count == 0)
        {
            return 0;
        }

        var alreadyMigrated = (await db.ChildAllergens
                .Select(a => a.ChildId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var added = 0;

        foreach (var child in children)
        {
            if (alreadyMigrated.Contains(child.Id))
            {
                continue;
            }

            var foods = Services.AllergyMatcher.SplitTokens(child.AllergicFoods);
            var others = Services.AllergyMatcher.SplitTokens(child.Allergies);

            foreach (var name in foods)
            {
                db.ChildAllergens.Add(New(child.Id, name, AllergenKind.Food));
                added++;
            }

            foreach (var name in others)
            {
                db.ChildAllergens.Add(New(child.Id, name, AllergenKind.Other));
                added++;
            }

            if (foods.Count + others.Count > 0)
            {
                logger.LogInformation(
                    "已把「{Alias}」的旧过敏源文本迁移成 {Count} 条分级记录（默认等级：{Severity}，待复核）。",
                    child.Alias,
                    foods.Count + others.Count,
                    DefaultSeverity.ToChinese());
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    private static ChildAllergen New(int childId, string name, AllergenKind kind) => new()
    {
        ChildId = childId,
        Name = name,
        Kind = kind,
        Severity = DefaultSeverity,
        // 等级是猜的，界面上会挂一个「待复核」的提示，家长改过一次就消失。
        NeedsReview = true,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };
}
