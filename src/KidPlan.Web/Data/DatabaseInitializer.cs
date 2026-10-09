using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Data;

/// <summary>
/// 启动时自动执行 EF 迁移。
/// 一次性部署脚本（docker compose up）里不再需要单独跑 dotnet ef database update，
/// 但需要容忍数据库刚启动还没就绪的情况，所以这里带指数退避重试。
/// </summary>
public static class DatabaseInitializer
{
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:AutoMigrate", true))
        {
            app.Logger.LogInformation("Database:AutoMigrate 为 false，跳过自动迁移。");
            return;
        }

        var maxAttempts = app.Configuration.GetValue("Database:MigrateMaxAttempts", 10);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
                if (pending.Count == 0)
                {
                    app.Logger.LogInformation("数据库结构已是最新，无需迁移。");
                    return;
                }

                app.Logger.LogInformation(
                    "准备应用 {Count} 个迁移：{Migrations}",
                    pending.Count,
                    string.Join(", ", pending));

                await db.Database.MigrateAsync();
                app.Logger.LogInformation("数据库迁移完成。");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 15));
                app.Logger.LogWarning(
                    ex,
                    "第 {Attempt}/{Max} 次连接数据库或执行迁移失败，{Delay} 秒后重试。",
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                // 重试次数用尽：这里直接抛出，让容器退出并由 restart 策略重启，
                // 比带着不可用的数据库继续跑要好排查。
                app.Logger.LogError(ex, "数据库迁移失败，已重试 {Max} 次，应用退出。", maxAttempts);
                throw;
            }
        }
    }
}
