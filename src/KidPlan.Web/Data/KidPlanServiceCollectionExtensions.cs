using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Web.Data;

/// <summary>
/// 数据库与账号体系的服务注册。
///
/// 为什么要单独抽出来：<c>dotnet ef</c> 生成迁移时用的是
/// <see cref="DesignTimeDbContextFactory"/>，而应用运行时用的是 Program.cs。
/// 两边只要有一点点注册差异，EF 的模型就会不一致——表现是每次启动都打一行
/// <c>The model for context 'ApplicationDbContext' has pending changes</c>，
/// 更麻烦的是「用工具生成的迁移」和「真正跑的模型」变成两份。
///
/// 亲身踩过的坑：Identity 在注册进 DI 之后，会把 <c>IdentityOptions</c> 里的
/// 键长限制（默认 128）等套用到 EF 模型上。设计时工厂如果只 new 一个 DbContext、
/// 不注册 Identity，生成出来的 <c>AspNetUserLogins.LoginProvider</c> 等列就是
/// varchar(255)，而运行时模型要的是 varchar(128)，两边对不上。
///
/// 所以：注册逻辑只写一份，运行时和设计时都调这里。
/// </summary>
public static class KidPlanServiceCollectionExtensions
{
    /// <summary>注册 MySQL 上下文工厂，以及供 Identity 的 UserStore 使用的作用域内上下文。</summary>
    public static IServiceCollection AddKidPlanDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("未配置连接字符串 'DefaultConnection'。");

        // 固定 ServerVersion 而不是 AutoDetect：AutoDetect 会在配置阶段就建立一次物理连接，
        // 容器编排时数据库往往还没就绪，会直接让应用启动失败。
        var mySqlVersion = configuration["Database:MySqlVersion"] ?? "8.0.36";
        var serverVersion = new MySqlServerVersion(Version.Parse(mySqlVersion));

        services.AddDbContextFactory<ApplicationDbContext>(
            options => options.UseMySql(connectionString, serverVersion, mysql =>
            {
                if (configuration.GetValue("Database:RetryOnFailure", true))
                {
                    mysql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        null);
                }
            }),
            // 生命周期设为 Scoped：Identity 的 UserStore 需要一个作用域内的 DbContext；
            // 我们自己的服务则通过 IDbContextFactory 每次操作新建上下文，避免跟踪陈旧数据。
            ServiceLifetime.Scoped);

        services.AddScoped<ApplicationDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

        return services;
    }

    /// <summary>注册账号体系。这些选项同样会影响 EF 模型，所以设计时必须一起注册。</summary>
    public static IServiceCollection AddKidPlanIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            // 自建部署一般没有 SMTP。默认要求“确认邮箱后才能登录”会让注册完直接登不进去，
            // 所以默认关闭，需要的话用 Identity__RequireConfirmedAccount=true 打开。
            options.SignIn.RequireConfirmedAccount =
                configuration.GetValue("Identity:RequireConfirmedAccount", false);
            options.SignIn.RequireConfirmedEmail =
                configuration.GetValue("Identity:RequireConfirmedEmail", false);
            options.SignIn.RequireConfirmedPhoneNumber =
                configuration.GetValue("Identity:RequireConfirmedPhoneNumber", false);

            options.Password.RequiredLength = configuration.GetValue("Identity:PasswordMinLength", 6);
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;

            // 用邮箱当登录名，同一邮箱不允许重复注册。
            options.User.RequireUniqueEmail = true;

            // 明确使用 Version2：本项目基于 EF Core 9 的 Identity 存储，
            // 不包含 .NET 10 的 Passkey(v3) 表和接口，声明成 v3 会与实际的库表不一致。
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
        })
            // 启用角色：只有 admin 角色能看到「模型设置」里的全局配置页签。
            // AspNetRoles / AspNetUserRoles 两张表本来就在迁移里（Identity 自带），
            // 这里只是把 RoleManager 注册出来，不会改数据库结构。
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        return services;
    }
}
