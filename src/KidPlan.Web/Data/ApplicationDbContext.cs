using KidPlan.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KidPlan.Web.Data;

/// <summary>
/// 应用数据库上下文：Identity 的整套表 + 本项目的业务表。
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    /// <summary>
    /// MySQL 的 <c>datetime</c> 不带时区信息：Pomelo 写 <see cref="DateTimeOffset"/> 时只保留墙上时间，
    /// 偏移量在入库那一刻就丢了，读出来时按服务器本地时区重新推导。
    ///
    /// 这里把这个事实显式写出来，而不是依赖驱动隐式转换，原因有三：
    /// 1. 语义清楚——数据库里存的就是“当时的本地时间”，和界面、饭点判断的口径一致；
    /// 2. 列类型仍然是 <c>datetime(6)</c>，生成的迁移和 DDL 完全不变；
    /// 3. SQLite 不支持对 <c>DateTimeOffset</c> 做 ORDER BY，
    ///    显式转换后单元测试可以用 SQLite 跑真实模型（而不是另建一套测试专用模型）。
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, DateTime> WallClockConverter = new(
        value => value.DateTime,
        value => new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value)));

    public DbSet<Child> Children => Set<Child>();

    public DbSet<GrowthRecord> GrowthRecords => Set<GrowthRecord>();

    public DbSet<MealPlan> MealPlans => Set<MealPlan>();

    public DbSet<ChildAllergen> ChildAllergens => Set<ChildAllergen>();

    public DbSet<AiUserSettings> AiUserSettings => Set<AiUserSettings>();

    public DbSet<RelayNode> RelayNodes => Set<RelayNode>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Child>(entity =>
        {
            entity.HasIndex(e => new { e.OwnerId, e.IsArchived });

            entity.HasOne(e => e.Owner)
                .WithMany(u => u.Children)
                .HasForeignKey(e => e.OwnerId)
                // 账号被删除时，其小孩数据一并删除（DeletePersonalData 会用到）。
                .OnDelete(DeleteBehavior.Cascade);

            // 枚举一律以字符串入库（与下面几张表保持一致），方便直接读库排查。
            entity.Property(e => e.Gender).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<GrowthRecord>(entity =>
        {
            // 成长曲线的主查询模式：某个小孩 + 按时间排序。
            entity.HasIndex(e => new { e.ChildId, e.RecordedAt });

            entity.HasOne(e => e.Child)
                .WithMany(c => c.GrowthRecords)
                .HasForeignKey(e => e.ChildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.HeightCm).HasPrecision(5, 2);
            entity.Property(e => e.WeightKg).HasPrecision(5, 2);
            entity.Property(e => e.TemperatureC).HasPrecision(4, 1);
            entity.Property(e => e.SleepHours).HasPrecision(4, 1);

            // 枚举以字符串入库，方便直接在数据库里读和排查。
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Appetite).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Mood).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<MealPlan>(entity =>
        {
            // 饮食页的主查询模式：某个小孩 + 某一天。
            entity.HasIndex(e => new { e.ChildId, e.Date });

            entity.HasOne(e => e.Child)
                .WithMany(c => c.MealPlans)
                .HasForeignKey(e => e.ChildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.MealType).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Appetite).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.AllergyAcknowledgedMaxSeverity).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<ChildAllergen>(entity =>
        {
            // 主查询模式：某个小孩的全部过敏原。
            entity.HasIndex(e => new { e.ChildId, e.Kind });

            entity.HasOne(e => e.Child)
                .WithMany(c => c.Allergens)
                .HasForeignKey(e => e.ChildId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Severity).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<AiUserSettings>(entity =>
        {
            // OwnerId 直接当主键：一个账号一份覆盖，天然唯一。
            entity.HasKey(e => e.OwnerId);

            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RelayNode>(entity =>
        {
            entity.HasIndex(e => new { e.OwnerId, e.Enabled });

            // 令牌哈希必须唯一：同一个令牌不可能绑到两个节点上。
            entity.HasIndex(e => e.TokenHash).IsUnique();

            entity.HasOne(e => e.Owner)
                .WithMany(u => u.RelayNodes)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ApplyWallClockConversion(builder);
    }

    /// <summary>
    /// 本项目自己的实体（<c>KidPlan.Web.Models</c> 命名空间）里，
    /// 所有 <see cref="DateTimeOffset"/> 属性统一按“墙上时间”存取。
    ///
    /// 刻意不碰 Identity 自带的属性（例如 <c>AspNetUsers.LockoutEnd</c>），
    /// 那是框架自己的字段，改动它的存储语义风险更大。
    /// </summary>
    private static void ApplyWallClockConversion(ModelBuilder builder)
    {
        var businessNamespace = typeof(Child).Namespace;

        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(e => e.ClrType.Namespace == businessNamespace))
        {
            foreach (var property in entityType.GetProperties()
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(WallClockConverter);
            }
        }
    }
}
