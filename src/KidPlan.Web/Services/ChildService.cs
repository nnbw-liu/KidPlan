using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services;

public interface IChildService
{
    /// <summary>列出当前账号的小孩。</summary>
    Task<IReadOnlyList<Child>> ListAsync(string userId, bool includeArchived = false, CancellationToken ct = default);

    /// <summary>列出小孩并附带记录数量等统计信息（小孩管理页用）。</summary>
    Task<IReadOnlyList<ChildListItem>> ListWithStatsAsync(string userId, CancellationToken ct = default);

    /// <summary>按 Id 取小孩，并且必须属于该账号；不属于则返回 null。</summary>
    Task<Child?> FindAsync(int childId, string userId, CancellationToken ct = default);

    /// <summary>按 Id 取小孩，不属于该账号时抛 <see cref="ChildAccessDeniedException"/>。</summary>
    Task<Child> RequireAsync(int childId, string userId, CancellationToken ct = default);

    Task<int> CountAsync(string userId, CancellationToken ct = default);

    Task<Child> CreateAsync(string userId, Child child, CancellationToken ct = default);

    Task UpdateAsync(string userId, Child child, CancellationToken ct = default);

    Task SetArchivedAsync(string userId, int childId, bool archived, CancellationToken ct = default);

    Task DeleteAsync(string userId, int childId, CancellationToken ct = default);

    /// <summary>取某个小孩的过敏原列表（含等级）。会校验归属。</summary>
    Task<IReadOnlyList<ChildAllergen>> ListAllergensAsync(
        int childId,
        string userId,
        CancellationToken ct = default);

    Task<ChildAllergen> AddAllergenAsync(
        string userId,
        int childId,
        ChildAllergen allergen,
        CancellationToken ct = default);

    Task UpdateAllergenAsync(
        string userId,
        int allergenId,
        ChildAllergen allergen,
        CancellationToken ct = default);

    Task DeleteAllergenAsync(string userId, int allergenId, CancellationToken ct = default);

    /// <summary>
    /// 把一串自由文本（如「牛奶、鸡蛋 花生」）拆成多条过敏原，统一用给定等级和类别录入。
    /// 已经存在的同名条目会被跳过，不会重复添加。
    /// </summary>
    /// <returns>实际新增的条数。</returns>
    Task<int> AddAllergensFromTextAsync(
        string userId,
        int childId,
        string? text,
        AllergenKind kind,
        AllergySeverity severity,
        CancellationToken ct = default);
}

/// <summary>
/// 小孩的增删改查。所有方法都要求传入当前登录账号的 userId，
/// 并且每次查询都带 OwnerId 过滤，从数据访问层保证账号之间互相看不到对方的小孩。
/// </summary>
public sealed class ChildService(IDbContextFactory<ApplicationDbContext> dbFactory) : IChildService
{
    public async Task<IReadOnlyList<Child>> ListAsync(
        string userId,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Children.AsNoTracking().Where(c => c.OwnerId == userId);
        if (!includeArchived)
        {
            query = query.Where(c => !c.IsArchived);
        }

        return await query
            .OrderBy(c => c.BirthDate)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChildListItem>> ListWithStatsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);

        var rows = await db.Children
            .AsNoTracking()
            .Where(c => c.OwnerId == userId)
            .OrderBy(c => c.IsArchived)
            .ThenBy(c => c.BirthDate)
            .Select(c => new
            {
                Child = c,
                GrowthRecordCount = c.GrowthRecords.Count,
                MealPlanCount = c.MealPlans.Count,
                Latest = c.GrowthRecords
                    .Where(r => r.HeightCm != null || r.WeightKg != null)
                    .OrderByDescending(r => r.RecordedAt)
                    .Select(r => new { r.RecordedAt, r.HeightCm, r.WeightKg })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new ChildListItem(
                row.Child,
                AgeCalculator.MonthsBetween(row.Child.BirthDate, today),
                AgeCalculator.Describe(row.Child.BirthDate, today),
                row.GrowthRecordCount,
                row.MealPlanCount,
                row.Latest is null ? null : DateOnly.FromDateTime(row.Latest.RecordedAt.DateTime),
                row.Latest?.HeightCm,
                row.Latest?.WeightKg))
            .ToList();
    }

    public async Task<Child?> FindAsync(int childId, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Children
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == childId && c.OwnerId == userId, ct);
    }

    public async Task<Child> RequireAsync(int childId, string userId, CancellationToken ct = default)
        => await FindAsync(childId, userId, ct) ?? throw new ChildAccessDeniedException(childId);

    public async Task<int> CountAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Children.CountAsync(c => c.OwnerId == userId && !c.IsArchived, ct);
    }

    public async Task<Child> CreateAsync(string userId, Child child, CancellationToken ct = default)
    {
        Validate(child);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = new Child
        {
            OwnerId = userId,
            CreatedAt = DateTimeOffset.Now,
        };
        ApplyEditableFields(entity, child);

        db.Children.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task UpdateAsync(string userId, Child child, CancellationToken ct = default)
    {
        Validate(child);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Children
            .FirstOrDefaultAsync(c => c.Id == child.Id && c.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(child.Id);

        // 只覆盖允许编辑的字段，OwnerId / CreatedAt 不允许被表单改掉。
        ApplyEditableFields(entity, child);
        entity.UpdatedAt = DateTimeOffset.Now;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(string userId, int childId, bool archived, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Children
            .FirstOrDefaultAsync(c => c.Id == childId && c.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(childId);

        entity.IsArchived = archived;
        entity.UpdatedAt = DateTimeOffset.Now;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int childId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Children
            .FirstOrDefaultAsync(c => c.Id == childId && c.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(childId);

        // 身体记录和饮食安排通过级联删除一起清掉。
        db.Children.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    private static void ApplyEditableFields(Child target, Child source)
    {
        target.Alias = source.Alias.Trim();
        target.FullName = Clean(source.FullName);
        target.BirthDate = source.BirthDate;
        target.Gender = source.Gender;
        target.Hobbies = Clean(source.Hobbies);
        target.FavoriteSports = Clean(source.FavoriteSports);
        target.BloodType = Clean(source.BloodType);
        target.AvatarEmoji = Clean(source.AvatarEmoji);
        target.Notes = Clean(source.Notes);

        // 刻意不碰 Allergies / AllergicFoods：那是分级改造之前的两列自由文本，
        // 现在只作为「迁移前的原文」保留，界面既不显示也不写回。
        // 从这里覆盖它们会把迁移前的存档抹掉，不值得。
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<IReadOnlyList<ChildAllergen>> ListAllergensAsync(
        int childId,
        string userId,
        CancellationToken ct = default)
    {
        await RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.ChildAllergens
            .AsNoTracking()
            .Where(a => a.ChildId == childId)
            // 危险的排前面：档案页、饮食页都直接按这个顺序渲染。
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    public async Task<ChildAllergen> AddAllergenAsync(
        string userId,
        int childId,
        ChildAllergen allergen,
        CancellationToken ct = default)
    {
        await RequireAsync(childId, userId, ct);
        Normalize(allergen);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var exists = await db.ChildAllergens
            .AnyAsync(a => a.ChildId == childId
                && a.Kind == allergen.Kind
                && a.Name == allergen.Name, ct);

        if (exists)
        {
            throw new ArgumentException($"「{allergen.Name}」已经在过敏原列表里了。", nameof(allergen));
        }

        var entity = new ChildAllergen
        {
            ChildId = childId,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        ApplyEditableFields(entity, allergen);

        db.ChildAllergens.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task UpdateAllergenAsync(
        string userId,
        int allergenId,
        ChildAllergen allergen,
        CancellationToken ct = default)
    {
        Normalize(allergen);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.ChildAllergens
            .FirstOrDefaultAsync(a => a.Id == allergenId && a.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        var duplicate = await db.ChildAllergens
            .AnyAsync(a => a.Id != allergenId
                && a.ChildId == entity.ChildId
                && a.Kind == allergen.Kind
                && a.Name == allergen.Name, ct);

        if (duplicate)
        {
            throw new ArgumentException($"「{allergen.Name}」已经在过敏原列表里了。", nameof(allergen));
        }

        ApplyEditableFields(entity, allergen);

        // 家长亲手调过等级就算复核过了，界面上的「待复核」提示随之消失。
        entity.NeedsReview = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAllergenAsync(string userId, int allergenId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.ChildAllergens
            .FirstOrDefaultAsync(a => a.Id == allergenId && a.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        db.ChildAllergens.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> AddAllergensFromTextAsync(
        string userId,
        int childId,
        string? text,
        AllergenKind kind,
        AllergySeverity severity,
        CancellationToken ct = default)
    {
        var tokens = AllergyMatcher.SplitTokens(text);
        if (tokens.Count == 0)
        {
            return 0;
        }

        await RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.ChildAllergens
            .Where(a => a.ChildId == childId)
            .Select(a => new { a.Name, a.Kind })
            .ToListAsync(ct);

        var added = 0;
        foreach (var token in tokens)
        {
            if (existing.Any(e => e.Kind == kind
                    && string.Equals(e.Name, token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            db.ChildAllergens.Add(new ChildAllergen
            {
                ChildId = childId,
                Name = token,
                Kind = kind,
                Severity = severity,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    private static void ApplyEditableFields(ChildAllergen target, ChildAllergen source)
    {
        target.Name = source.Name.Trim();
        target.Kind = source.Kind;
        target.Severity = source.Severity;
        target.Notes = Clean(source.Notes);
        target.TestResult = Clean(source.TestResult);
        target.UpdatedAt = DateTimeOffset.Now;
    }

    private static void Normalize(ChildAllergen allergen)
    {
        allergen.Name = allergen.Name?.Trim() ?? string.Empty;

        if (allergen.Name.Length == 0)
        {
            throw new ArgumentException("请填写过敏原名称。", nameof(allergen));
        }

        if (allergen.Name.Length > 50)
        {
            throw new ArgumentException("过敏原名称最多 50 个字。", nameof(allergen));
        }
    }

    private static void Validate(Child child)
    {
        if (string.IsNullOrWhiteSpace(child.Alias))
        {
            throw new ArgumentException("请填写小孩的别名。", nameof(child));
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!AgeCalculator.IsPlausibleBirthDate(child.BirthDate, today))
        {
            throw new ArgumentException("出生年月不能是未来的日期，也不应该早于 25 年前。", nameof(child));
        }
    }
}
