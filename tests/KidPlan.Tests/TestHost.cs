using KidPlan.Web.Data;
using KidPlan.Web.Models;
using KidPlan.Web.Services;
using KidPlan.Web.Services.Ai;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KidPlan.Tests;

/// <summary>
/// 固定“现在”的时间源。测试里所有依赖系统时间的逻辑都走它，
/// 这样饭点判断、年龄计算不会因为跑测试的时刻不同而时好时坏。
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    /// <summary>固定成东八区，避免测试结果受运行机器的时区影响。</summary>
    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("kidplan-test", TimeSpan.FromHours(8), "KidPlan 测试时区", "KidPlan 测试时区");
}

/// <summary>
/// 用 SQLite 内存库把整套服务真实装配起来（含 Identity 的 UserManager）。
///
/// 为什么不用 EF 的 InMemory 提供程序：这里要验证唯一索引、外键级联、
/// 枚举按字符串存储等真实数据库行为，SQLite 更接近生产环境；
/// 而且本机没有 MySQL，SQLite 是唯一能真正跑起来的关系库。
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly string _aiConfigDirectory;

    public TestHost(DateTimeOffset? now = null)
    {
        Now = now ?? new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8));

        // 必须保持连接打开，SQLite 的 :memory: 库在连接关闭时就没了。
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // AI 全局配置是「文件 + 热加载」的，所以测试也得给一个真的临时目录：
        // 否则要么写不动文件，要么污染开发机上的 config/ai.json。
        _aiConfigDirectory = Path.Combine(Path.GetTempPath(), "kidplan-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_aiConfigDirectory);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // 真实宿主由 Web 框架提供；测试宿主必须自己加上，
        // 否则 AddDefaultTokenProviders 注册的 DataProtectorTokenProvider 无法激活。
        services.AddDataProtection();

        // 与 Program.cs 一致：工厂 + 作用域内的 DbContext（Blazor Server 不能长期持有同一个上下文）。
        services.AddDbContextFactory<ApplicationDbContext>(
            options => options.UseSqlite(_connection),
            ServiceLifetime.Scoped);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
                // 与生产配置一致：EF Core 9 的存储层只支持到 Version2。
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.AddSingleton<TimeProvider>(new TestTimeProvider(Now));
        services.AddOptions();
        services.AddSingleton<MealStatusCalculator>();
        services.AddScoped<IChildService, ChildService>();
        services.AddScoped<IGrowthService, GrowthService>();
        services.AddScoped<IMealService, MealService>();
        services.AddScoped<IChildOverviewService, ChildOverviewService>();
        services.AddScoped<GrowthViewState>();

        // ---- AI 相关 ----
        services.AddSingleton(new AiConfigFile(Path.Combine(_aiConfigDirectory, "ai.json")));
        services.AddSingleton<IAiSettingsStore, AiSettingsStore>();
        services.AddSingleton<RelayConnectionRegistry>();
        services.AddSingleton<IRelayConnectionRegistryAccessor, RelayConnectionRegistryAccessor>();
        services.AddScoped<IRelayNodeService, RelayNodeService>();

        // 真的去调模型在单元测试里既慢又不确定，所以换成可编排的假客户端。
        services.AddSingleton<FakeAiChatClient>();
        services.AddSingleton<IAiChatClient>(sp => sp.GetRequiredService<FakeAiChatClient>());
        services.AddScoped<IMealRecommendationService, MealRecommendationService>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public DateTimeOffset Now { get; }

    public IServiceScope CreateScope() => _provider.CreateScope();

    /// <summary>假的模型客户端：想让它「说」什么就设什么。</summary>
    public FakeAiChatClient Ai => _provider.GetRequiredService<FakeAiChatClient>();

    /// <summary>真实的 AI 配置存储（指向一个临时目录里的 ai.json）。</summary>
    public IAiSettingsStore AiSettings => _provider.GetRequiredService<IAiSettingsStore>();

    /// <summary>建一个测试账号，返回它的 Id。</summary>
    public async Task<string> CreateUserAsync(string email, string password = "kidplan123")
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = email.Split('@')[0],
            CreatedAt = Now,
        };

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, "创建测试账号失败：" + string.Join("；", result.Errors.Select(e => e.Description)));

        return user.Id;
    }

    /// <summary>
    /// 给指定账号建一个小孩，返回小孩 Id。
    ///
    /// <paramref name="allergicFoods"/> 会被写成一条条**带等级**的过敏原记录
    /// （旧的自由文本字段已经不参与业务逻辑了）。
    /// 默认等级取「中等」：这个等级的行为是「必须勾选确认才能保存」，
    /// 既不会像「不耐受」那样悄悄放行，也不会像「强过敏」那样还要二次确认，
    /// 是大多数校验用例想要的中间档；需要验证别的档位时显式传入。
    /// </summary>
    public async Task<int> CreateChildAsync(
        string userId,
        string alias = "糖糖",
        string? allergicFoods = null,
        DateOnly? birthDate = null,
        AllergySeverity severity = AllergySeverity.Moderate)
    {
        using var scope = _provider.CreateScope();
        var children = scope.ServiceProvider.GetRequiredService<IChildService>();

        var child = await children.CreateAsync(userId, new Child
        {
            Alias = alias,
            BirthDate = birthDate ?? new DateOnly(2023, 6, 1),
            Gender = Gender.Girl,
        });

        if (!string.IsNullOrWhiteSpace(allergicFoods))
        {
            await children.AddAllergensFromTextAsync(userId, child.Id, allergicFoods, AllergenKind.Food, severity);
        }

        return child.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();

        try
        {
            Directory.Delete(_aiConfigDirectory, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不该让测试失败。
        }
    }
}

/// <summary>
/// 可编排的假模型客户端。
///
/// 让 <c>MealRecommendationService</c> 的测试能直接喂一段「模型会返回的 JSON」，
/// 从而验证解析、时间补全、以及**本地过敏复核**这些真正容易出错的地方，
/// 而不必依赖某个真实模型今天心情好不好。
/// </summary>
public sealed class FakeAiChatClient : IAiChatClient
{
    /// <summary>下一次（或每一次）调用要返回的内容。</summary>
    public string Response { get; set; } = "{}";

    public bool Fail { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>假客户端返回的 finish_reason。要复现「被 max_tokens 截断」时设成 "length"。</summary>
    public string? FinishReason { get; set; } = "stop";

    /// <summary>最近一次收到的消息，用来断言提示词里到底带了什么。</summary>
    public IReadOnlyList<AiChatMessage> LastMessages { get; private set; } = [];

    public AiProviderConfig? LastProvider { get; private set; }

    public string? LastModel { get; private set; }

    public IReadOnlyList<string> Models { get; set; } = ["fake-model"];

    public int CallCount { get; private set; }

    public Task<AiChatResult> CompleteAsync(
        AiProviderConfig provider,
        string model,
        IReadOnlyList<AiChatMessage> messages,
        AiCallOptions options,
        string ownerId,
        CancellationToken ct = default)
    {
        CallCount++;
        LastProvider = provider;
        LastModel = model;
        LastMessages = messages;

        return Task.FromResult(Fail
            ? new AiChatResult(false, string.Empty, string.Empty, ErrorMessage ?? "假客户端被要求失败。")
            : new AiChatResult(true, Response, Response, null, 10, 20, FinishReason));
    }

    public Task<(bool Ok, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(
        AiProviderConfig provider,
        string ownerId,
        CancellationToken ct = default)
        => Task.FromResult(Fail
            ? (false, (IReadOnlyList<string>)[], ErrorMessage ?? "假客户端被要求失败。")
            : (true, Models, (string?)null));

    public Task<(bool Ok, string Message)> TestAsync(
        AiProviderConfig provider,
        string? model,
        string ownerId,
        CancellationToken ct = default)
        => Task.FromResult(Fail
            ? (false, ErrorMessage ?? "假客户端被要求失败。")
            : (true, "假客户端：连接正常"));
}
