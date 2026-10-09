using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services;

public interface IGrowthService
{
    Task<IReadOnlyList<GrowthRecord>> ListAsync(
        string userId,
        int childId,
        DateOnly? from = null,
        DateOnly? to = null,
        RecordKind? kind = null,
        CancellationToken ct = default);

    Task<GrowthRecord?> FindAsync(string userId, int recordId, CancellationToken ct = default);

    Task<GrowthRecord> AddAsync(string userId, int childId, GrowthRecord record, CancellationToken ct = default);

    Task UpdateAsync(string userId, GrowthRecord record, CancellationToken ct = default);

    Task DeleteAsync(string userId, int recordId, CancellationToken ct = default);

    /// <summary>
    /// 构造成长曲线数据（按时间排序）。
    ///
    /// 注意这里**不再**只挑有身高体重的记录：曲线指标是可选的，
    /// 「只记了体温」「只记了睡眠」的记录同样要能画出来。
    /// 一条记录只要有任何一项可画的值就保留，各条曲线各取所需。
    /// </summary>
    Task<GrowthChartData> BuildChartAsync(
        string userId,
        int childId,
        DateOnly? from = null,
        DateOnly? to = null,
        RecordKind? kind = null,
        CancellationToken ct = default);

    /// <summary>最新一次身体数据及与上次的差值。</summary>
    Task<GrowthSummary?> BuildSummaryAsync(string userId, int childId, CancellationToken ct = default);
}

/// <summary>
/// 身体记录（成长数据）服务。所有方法先校验小孩归属，再操作数据。
/// </summary>
public sealed class GrowthService(IDbContextFactory<ApplicationDbContext> dbFactory, IChildService childService)
    : IGrowthService
{
    public async Task<IReadOnlyList<GrowthRecord>> ListAsync(
        string userId,
        int childId,
        DateOnly? from = null,
        DateOnly? to = null,
        RecordKind? kind = null,
        CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.GrowthRecords.AsNoTracking().Where(r => r.ChildId == childId);

        if (from is { } fromDate)
        {
            var fromTime = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset);
            query = query.Where(r => r.RecordedAt >= fromTime);
        }

        if (to is { } toDate)
        {
            // 结束日期按当天 23:59:59 处理，避免“选到今天却查不到今天的记录”。
            var toTime = new DateTimeOffset(toDate.ToDateTime(new TimeOnly(23, 59, 59)), DateTimeOffset.Now.Offset);
            query = query.Where(r => r.RecordedAt <= toTime);
        }

        if (kind is { } recordKind)
        {
            query = query.Where(r => r.Kind == recordKind);
        }

        return await query
            .OrderByDescending(r => r.RecordedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);
    }

    public async Task<GrowthRecord?> FindAsync(string userId, int recordId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.GrowthRecords
            .AsNoTracking()
            .Where(r => r.Id == recordId)
            // 通过 Child.OwnerId 做归属校验，防止猜到别人的记录 Id。
            .FirstOrDefaultAsync(r => r.Child!.OwnerId == userId, ct);
    }

    public async Task<GrowthRecord> AddAsync(
        string userId,
        int childId,
        GrowthRecord record,
        CancellationToken ct = default)
    {
        await childService.RequireAsync(childId, userId, ct);
        Normalize(record);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = new GrowthRecord
        {
            ChildId = childId,
            RecordedByUserId = userId,
            CreatedAt = DateTimeOffset.Now,
        };
        ApplyEditableFields(entity, record);

        db.GrowthRecords.Add(entity);
        await db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task UpdateAsync(string userId, GrowthRecord record, CancellationToken ct = default)
    {
        Normalize(record);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.GrowthRecords
            .FirstOrDefaultAsync(r => r.Id == record.Id && r.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(record.ChildId);

        ApplyEditableFields(entity, record);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int recordId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = await db.GrowthRecords
            .FirstOrDefaultAsync(r => r.Id == recordId && r.Child!.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        db.GrowthRecords.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<GrowthChartData> BuildChartAsync(
        string userId,
        int childId,
        DateOnly? from = null,
        DateOnly? to = null,
        RecordKind? kind = null,
        CancellationToken ct = default)
    {
        var child = await childService.RequireAsync(childId, userId, ct);
        var records = await ListAsync(userId, childId, from, to, kind, ct);

        var points = records
            // 曲线至少要有一个能画的值；纯症状/备注记录不进曲线。
            .Where(HasAnyPlottableValue)
            .OrderBy(r => r.RecordedAt)
            .ThenBy(r => r.Id)
            .Select(r =>
            {
                var date = DateOnly.FromDateTime(r.RecordedAt.DateTime);
                return new GrowthPoint(
                    date,
                    AgeCalculator.MonthsBetween(child.BirthDate, date),
                    date.ToString("yyyy-MM-dd"),
                    r.Kind,
                    (double?)r.HeightCm,
                    (double?)r.WeightKg,
                    (double?)r.Bmi,
                    (double?)r.TemperatureC,
                    (double?)r.SleepHours,
                    r.ExerciseMinutes is { } minutes ? minutes : null,
                    r.Appetite,
                    r.Mood);
            })
            .ToList();

        return new GrowthChartData(child.BirthDate, points);
    }

    /// <summary>这条记录有没有任何一个可以画进曲线的数值。</summary>
    private static bool HasAnyPlottableValue(GrowthRecord record)
        => record.HeightCm.HasValue
            || record.WeightKg.HasValue
            || record.TemperatureC.HasValue
            || record.SleepHours.HasValue
            || record.ExerciseMinutes.HasValue
            || record.Appetite != AppetiteLevel.Unknown
            || record.Mood != MoodLevel.Unknown;

    public async Task<GrowthSummary?> BuildSummaryAsync(string userId, int childId, CancellationToken ct = default)
    {
        var child = await childService.RequireAsync(childId, userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var records = await db.GrowthRecords
            .AsNoTracking()
            .Where(r => r.ChildId == childId)
            .OrderByDescending(r => r.RecordedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        if (records.Count == 0)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var latest = records[0];

        // 上一次“有身高或体重”的记录，用来算增长量。
        var previous = records
            .Skip(1)
            .FirstOrDefault(r => r.HeightCm.HasValue || r.WeightKg.HasValue);

        return new GrowthSummary(
            DateOnly.FromDateTime(latest.RecordedAt.DateTime),
            latest.HeightCm,
            latest.WeightKg,
            latest.TemperatureC,
            latest.Bmi,
            Diff(latest.HeightCm, previous?.HeightCm),
            Diff(latest.WeightKg, previous?.WeightKg),
            records.Count,
            AgeCalculator.MonthsBetween(child.BirthDate, today));
    }

    private static decimal? Diff(decimal? current, decimal? previous)
        => current.HasValue && previous.HasValue ? Math.Round(current.Value - previous.Value, 2) : null;

    private static void ApplyEditableFields(GrowthRecord target, GrowthRecord source)
    {
        target.Kind = source.Kind;
        target.RecordedAt = source.RecordedAt;
        target.PeriodStart = source.PeriodStart;
        target.PeriodEnd = source.PeriodEnd;
        target.HeightCm = source.HeightCm;
        target.WeightKg = source.WeightKg;
        target.TemperatureC = source.TemperatureC;
        target.SleepHours = source.SleepHours;
        target.ExerciseMinutes = source.ExerciseMinutes;
        target.Appetite = source.Appetite;
        target.Mood = source.Mood;
        target.Symptoms = Clean(source.Symptoms);
        target.Notes = Clean(source.Notes);
    }

    /// <summary>把时段记录的起止日期补全并校验。</summary>
    private static void Normalize(GrowthRecord record)
    {
        if (record.Kind == RecordKind.Point)
        {
            record.PeriodStart = null;
            record.PeriodEnd = null;
            return;
        }

        if (record.PeriodStart is null && record.PeriodEnd is null)
        {
            throw new ArgumentException("时段记录需要填写开始日期或结束日期。", nameof(record));
        }

        // 只填了一头就自动补成同一天，避免出现悬空的时段。
        record.PeriodStart ??= record.PeriodEnd;
        record.PeriodEnd ??= record.PeriodStart;

        if (record.PeriodStart > record.PeriodEnd)
        {
            (record.PeriodStart, record.PeriodEnd) = (record.PeriodEnd, record.PeriodStart);
        }

        // 让这条记录的时间落在时段结束那天，曲线排序才符合直觉。
        if (DateOnly.FromDateTime(record.RecordedAt.DateTime) != record.PeriodEnd)
        {
            var time = TimeOnly.FromDateTime(record.RecordedAt.DateTime);
            record.RecordedAt = new DateTimeOffset(
                record.PeriodEnd!.Value.ToDateTime(time),
                record.RecordedAt.Offset);
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
