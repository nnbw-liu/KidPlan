using KidPlan.Web.Components;
using KidPlan.Web.Components.Account;
using KidPlan.Web.Data;
using KidPlan.Web.Services;
using KidPlan.Web.Services.Ai;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Blazor + UI
// ---------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.ShowTransitionDuration = 150;
    config.SnackbarConfiguration.HideTransitionDuration = 150;
});

// 用 TimeProvider 而不是直接 DateTime.Now，吃饭状态的判断才可测。
builder.Services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------
// 认证 / 授权
// ---------------------------------------------------------------------------
builder.Services.AddCascadingAuthenticationState();
// 显式注册授权：下面要用 [Authorize(Roles = "admin")] 这种基于角色的判断。
builder.Services.AddAuthorization();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// 部署在宝塔 Nginx 后面，容器内是 HTTP、对外是 HTTPS，
// 所以 Cookie 的安全策略跟随请求协议（依赖下面的 ForwardedHeaders）。
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "KidPlan.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // 家庭应用，登录状态保持久一点，省得天天输密码。
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/access-denied";
});

// ---------------------------------------------------------------------------
// 数据库（MySQL 8 / Pomelo）与账号体系
//
// 注册逻辑放在 KidPlanServiceCollectionExtensions 里，设计时工厂
// （DesignTimeDbContextFactory）会调用同一份代码，保证 dotnet ef 生成迁移时
// 看到的模型与应用运行时完全一致。
// ---------------------------------------------------------------------------
builder.Services.AddKidPlanDatabase(builder.Configuration);
builder.Services.AddKidPlanIdentity(builder.Configuration);

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------------------------------------------------------------------------
// 邮件（SMTP）
// 邮箱确认邮件与重置密码邮件都走这里。授权码不要写在 appsettings.json 里，
// 用 user-secrets（本机）或环境变量（服务器）。
// ---------------------------------------------------------------------------
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection(EmailSettings.SectionName));
builder.Services.AddScoped<IEmailSender<ApplicationUser>, SmtpEmailSender>();
builder.Services.AddScoped<AccountLinkBuilder>();

// ---------------------------------------------------------------------------
// 业务服务
// ---------------------------------------------------------------------------
builder.Services.Configure<MealScheduleOptions>(
    builder.Configuration.GetSection(MealScheduleOptions.SectionName));

builder.Services.AddSingleton<MealStatusCalculator>();
builder.Services.AddScoped<CurrentChildState>();
builder.Services.AddScoped<IChildService, ChildService>();
builder.Services.AddScoped<IGrowthService, GrowthService>();
builder.Services.AddScoped<IMealService, MealService>();
// 成长曲线的显示偏好（勾了哪些指标、分图还是叠加…）。
// Blazor Server 里 Scoped 的生命周期 = 一个浏览器标签页的连接，
// 正好就是「在这个页面来回切时记住我的选择」，刷新后回到默认值也无所谓，
// 所以不用落库，省掉一套用户偏好表的同步问题。
builder.Services.AddScoped<GrowthViewState>();

builder.Services.AddScoped<IChildOverviewService, ChildOverviewService>();

// ---------------------------------------------------------------------------
// AI（模型设置 + 饮食推荐 + 本地中继）
//
// 全局配置放在 config/ai.json，自己起 FileSystemWatcher 热加载。
// 刻意不用 AddJsonFile(reloadOnChange: true)：配置系统在文件变更回调里解析失败会直接抛异常，
// 那个异常跑在线程池线程上没人接得住——在配置文件里打错一个逗号就能把应用搞崩。
// ---------------------------------------------------------------------------
var aiConfigPath = builder.Configuration["Ai:ConfigFile"];
if (string.IsNullOrWhiteSpace(aiConfigPath))
{
    aiConfigPath = Path.Combine(builder.Environment.ContentRootPath, "config", "ai.json");
}

builder.Services.AddSingleton(new AiConfigFile(Path.GetFullPath(aiConfigPath)));
builder.Services.AddSingleton<IAiSettingsStore, AiSettingsStore>();

// 中继连接注册表必须跨请求共享，所以是单例。
builder.Services.AddSingleton<RelayConnectionRegistry>();
builder.Services.AddSingleton<IRelayConnectionRegistryAccessor, RelayConnectionRegistryAccessor>();
builder.Services.AddScoped<IRelayNodeService, RelayNodeService>();

AiHttpClient.Configure(builder.Services);
builder.Services.AddSingleton<DirectHttpTransport>();
builder.Services.AddSingleton<LocalRelayTransport>();
builder.Services.AddSingleton<IAiTransportRouter, AiTransportRouter>();
builder.Services.AddSingleton<IAiChatClient, OpenAiChatClient>();
builder.Services.AddScoped<IMealRecommendationService, MealRecommendationService>();

var app = builder.Build();

// 要求邮箱确认、却没有可用的 SMTP，等于新用户永远注册不进来。
// 这种情况必须在启动日志里直接说清楚，否则表现出来的只是“注册完登不进去”。
var identityOptions = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
var emailSettings = app.Services.GetRequiredService<IOptions<EmailSettings>>().Value;
if (identityOptions.SignIn.RequireConfirmedAccount && !emailSettings.IsUsable)
{
    app.Logger.LogError(
        "已开启邮箱确认（Identity:RequireConfirmedAccount=true），但 SMTP 配置不完整，" +
        "新注册的账号无法完成邮箱确认、也就无法登录。" +
        "请检查 Smtp:Host、Smtp:FromAddress（或 Smtp:UserName）、Smtp:Password 三项。");
}

// ---------------------------------------------------------------------------
// HTTP 管道
// ---------------------------------------------------------------------------
// 宝塔 Nginx 反代过来的 X-Forwarded-Proto / X-Forwarded-For 必须被采纳，
// 否则应用会以为自己跑在 HTTP 上，导致重定向地址和 Cookie 的 Secure 判断出错。
// 这里清空已知网络限制：容器网络里的对端 IP 不固定，且容器端口只绑定在 127.0.0.1 上。
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// 容器内跑的是 HTTP，TLS 由宝塔 Nginx 终结，默认不做 HTTPS 跳转。
if (app.Configuration.GetValue("HttpsRedirection:Enabled", false))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

// 中继长连接走 WebSocket。Blazor 的 SignalR 有自己的握手通道，不依赖这一句，
// 但我们自己映射的 /api/relay/connect 必须有它，否则 AcceptWebSocketAsync 会直接抛。
app.UseWebSockets(new WebSocketOptions
{
    // 默认 2 分钟太宽松：家里路由器 / 运营商 NAT 常见 60~120 秒就掐空闲连接，
    // 30 秒一次 ping 能稳稳保住这条长连接。
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

// 本地中继（客户端电脑上的 KidPlan.Relay）连进来的端点。
app.MapRelayEndpoint();

// Docker HEALTHCHECK 用的健康检查：数据库连得上、并且没有未应用的迁移才算健康。
app.MapGet("/healthz", async (ApplicationDbContext db, CancellationToken ct) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(ct))
        {
            return Results.Json(new { status = "unhealthy", reason = "database unreachable" }, statusCode: 503);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            return Results.Json(
                new { status = "unhealthy", reason = "pending migrations", pending },
                statusCode: 503);
        }

        return Results.Json(new { status = "healthy" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", reason = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();

// 启动时自动执行迁移，并等待数据库就绪（容器首次启动时 MySQL 往往还在初始化）。
await app.ApplyMigrationsAsync();

// 迁移之后把旧的自由文本过敏源搬进分级表（幂等，只搬一次）。
try
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var migrated = await AllergenBackfill.RunAsync(db, app.Logger);
    if (migrated > 0)
    {
        app.Logger.LogInformation(
            "过敏源分级迁移完成，共新增 {Count} 条记录。它们默认是「强过敏」且标记为待复核，" +
            "请到小孩资料页逐个确认等级。",
            migrated);
    }
}
catch (Exception ex)
{
    // 迁移失败不该拦住启动：业务照常可用，只是过敏源还是旧文本形态。
    app.Logger.LogError(ex, "过敏源分级迁移失败，已跳过。旧文本字段仍然保留，不会丢数据。");
}

// 把 admin 角色准备好（「模型设置 → 全局配置」只有管理员看得到）。
// 失败也不拦启动：最坏情况是没人能改全局配置，但改文件这条路一直都在。
try
{
    await using var scope = app.Services.CreateAsyncScope();
    await AdminRoleBootstrap.RunAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogError(
        ex,
        "管理员角色初始化失败。模型设置里的「全局配置」页签可能没人能看到，" +
        "这种情况直接改 config/ai.json 也能生效。");
}

app.Run();

/// <summary>
/// 顶层语句生成的 Program 类默认是 internal，集成测试（WebApplicationFactory&lt;Program&gt;）需要它可见。
/// </summary>
public partial class Program;
