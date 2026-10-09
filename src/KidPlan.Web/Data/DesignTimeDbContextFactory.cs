using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KidPlan.Web.Data;

/// <summary>
/// 仅供 `dotnet ef` 命令行工具使用（迁移的生成 / 应用）。
///
/// 关键点：这里**不能**自己 new 一个 <see cref="ApplicationDbContext"/> 就完事。
/// 迁移快照必须和「应用真正运行时用到的模型」一致，否则每次启动 EF 都会报
/// pending model changes，而且工具生成的迁移与实际运行的模型会变成两份。
///
/// 于是这里走一遍和 Program.cs 完全相同的注册（<see cref="KidPlanServiceCollectionExtensions"/>），
/// 再从容器里取出上下文。这样不需要能连上的 MySQL，也能正常生成迁移脚本。
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKidPlanDatabase(configuration);
        services.AddKidPlanIdentity(configuration);

        return services.BuildServiceProvider().GetRequiredService<ApplicationDbContext>();
    }
}
