using KidPlan.Web.Models;
using KidPlan.Web.Services.Ai;

namespace KidPlan.Tests;

/// <summary>
/// AI 配置的读取、合并与热加载。
///
/// 这里最要紧的一条是「配置文件写坏不能把应用带走」：
/// 早先的做法是 <c>AddJsonFile(reloadOnChange: true)</c>，而配置系统在文件变更回调里
/// 解析失败会直接抛异常，那个异常跑在线程池线程上没人接得住——
/// 也就是说在配置里打错一个逗号就能把整个站点搞崩。
/// 所以这个文件里有一半用例在验证「坏输入不会让配置丢失」。
/// </summary>
public class AiSettingsStoreTests
{
    private static AiProviderConfig Provider(
        string id,
        string name = "端点",
        string baseUrl = "http://127.0.0.1:1234/v1",
        params string[] models)
        => new()
        {
            Id = id,
            Name = name,
            BaseUrl = baseUrl,
            Enabled = true,
            Models = [.. models],
        };

    private static async Task WaitForFileWatcherAsync(IAiSettingsStore store, Func<bool> condition)
    {
        // 文件监听有 400ms 去抖，所以这里给足时间轮询，而不是死等一个固定时长。
        var deadline = DateTimeOffset.Now.AddSeconds(5);

        while (DateTimeOffset.Now < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("等了 5 秒，配置热加载没有生效。");
    }

    // ------------------------------------------------------------------
    // 掩码
    // ------------------------------------------------------------------

    [Fact]
    public void Mask_KeepsOnlyTheLastFourCharacters()
    {
        var masked = IAiSettingsStore.Mask("sk-1234567890abcd");

        Assert.EndsWith("abcd", masked);
        Assert.DoesNotContain("1234567890", masked);
        Assert.True(IAiSettingsStore.IsMasked(masked));
    }

    [Fact]
    public void Mask_HandlesShortEmptyAndNullKeys()
    {
        Assert.Equal(string.Empty, IAiSettingsStore.Mask(null));
        Assert.Equal(string.Empty, IAiSettingsStore.Mask(string.Empty));

        // 太短的钥匙掩码之后就等于没掩，索性全遮掉。
        Assert.Equal("••••", IAiSettingsStore.Mask("abc"));
        Assert.False(IAiSettingsStore.IsMasked("sk-real-key"));
    }

    // ------------------------------------------------------------------
    // 全局配置
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetGlobal_StartsWithAnEmptyButUsableDocument()
    {
        await using var host = new TestHost();

        var global = host.AiSettings.GetGlobal();

        Assert.Empty(global.Providers);
        Assert.NotNull(global.Recommendation);
        Assert.Equal(30, global.Recommendation.MaxLookbackDays);
        Assert.Null(global.DefaultProviderId);
    }

    [Fact]
    public async Task SaveGlobal_RoundTripsThroughTheFile()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "local",
            DefaultModel = "qwen2.5-7b-instruct",
            Recommendation = new AiRecommendationOptions { DefaultLookbackDays = 14, Temperature = 0.2 },
            Providers =
            [
                new AiProviderConfig
                {
                    Id = "local",
                    Name = "家里的 LM Studio",
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Transport = AiTransport.LocalRelay,
                    RelayNodeId = 7,
                    Models = ["qwen2.5-7b-instruct"],
                    ExtraHeaders = { ["HTTP-Referer"] = "https://example.com" },
                },
            ],
        });

        // 全新的 store 实例 = 真的从磁盘读一遍，而不是拿内存里的副本。
        var reloaded = host.AiSettings.ReloadGlobal();
        Assert.Null(reloaded.LastError);
        Assert.True(reloaded.FileExists);

        var global = host.AiSettings.GetGlobal();
        var provider = Assert.Single(global.Providers);

        Assert.Equal("local", global.DefaultProviderId);
        Assert.Equal("qwen2.5-7b-instruct", global.DefaultModel);
        Assert.Equal(14, global.Recommendation.DefaultLookbackDays);
        Assert.Equal(0.2, global.Recommendation.Temperature);
        Assert.Equal(AiTransport.LocalRelay, provider.Transport);
        Assert.Equal(7, provider.RelayNodeId);
        Assert.Equal("https://example.com", provider.ExtraHeaders["HTTP-Referer"]);
    }

    [Fact]
    public async Task SaveGlobal_RestoresRealKeysWhenGivenMaskedOnes()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers = [new AiProviderConfig { Id = "a", Name = "A", ApiKey = "sk-real-secret-1234" }],
        });

        // 界面拿到的永远是掩码。家长只是改个名字再保存，不能把钥匙变成「••••••••1234」。
        var fromUi = host.AiSettings.GetGlobal();
        fromUi.Providers[0].ApiKey = IAiSettingsStore.Mask(fromUi.Providers[0].ApiKey);
        fromUi.Providers[0].Name = "改了个名字";

        await host.AiSettings.SaveGlobalAsync(fromUi);

        var saved = host.AiSettings.GetGlobal().Providers[0];
        Assert.Equal("sk-real-secret-1234", saved.ApiKey);
        Assert.Equal("改了个名字", saved.Name);
    }

    [Fact]
    public async Task SaveGlobal_KeepsANewKeyWhenItIsNotMasked()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers = [new AiProviderConfig { Id = "a", Name = "A", ApiKey = "sk-old" }],
        });

        var document = host.AiSettings.GetGlobal();
        document.Providers[0].ApiKey = "sk-brand-new";

        await host.AiSettings.SaveGlobalAsync(document);

        Assert.Equal("sk-brand-new", host.AiSettings.GetGlobal().Providers[0].ApiKey);
    }

    [Fact]
    public async Task SaveGlobal_IsPickedUpWithoutRestart()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "local",
            Providers = [Provider("local", "家里的", models: "m1")],
        });

        // GetGlobal 每次都会去检查文件有没有变（改文件不必重启服务就能生效）。
        var global = host.AiSettings.GetGlobal();

        Assert.Equal("local", global.DefaultProviderId);
        Assert.Single(global.Providers);
    }

    // ------------------------------------------------------------------
    // 账号覆盖
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetEffective_ReturnsGlobalWhenThereIsNoOverride()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "global-one",
            DefaultModel = "g-model",
            Providers = [Provider("global-one", "全局端点", models: "g-model")],
        });

        var effective = await host.AiSettings.GetEffectiveAsync(userId);

        Assert.Equal("global-one", effective.DefaultProviderId);
        Assert.Equal("g-model", effective.DefaultModel);
        Assert.Equal("全局端点", Assert.Single(effective.Providers).Name);
    }

    [Fact]
    public async Task GetEffective_ReplacesProviderWholesale()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers = [Provider("shared", "全局名字", "https://api.example.com/v1", "g-model")],
        });

        // 只改地址，但整条替换：覆盖页面上看到什么就是什么，
        // 不会出现「我改了 BaseUrl 怎么还是全局那个」这种查半天的问题。
        await host.AiSettings.SaveOverrideAsync(userId, new AiSettingsOverride
        {
            Providers =
            [
                new AiProviderConfig
                {
                    Id = "shared",
                    Name = "我的名字",
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Enabled = true,
                    Models = ["my-model"],
                },
            ],
        });

        var effective = await host.AiSettings.GetEffectiveAsync(userId);
        var provider = Assert.Single(effective.Providers);

        Assert.Equal("我的名字", provider.Name);
        Assert.Equal("http://127.0.0.1:1234/v1", provider.BaseUrl);

        // 整条替换的代价：没写进覆盖的字段就用默认值，这一点要有意识地接受。
        Assert.Equal(["my-model"], provider.Models);
    }

    [Fact]
    public async Task GetEffective_HidesRemovedProvidersAndAddsPersonalOnes()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers =
            [
                Provider("public-api", "公网接口", "https://api.deepseek.com/v1", "deepseek-chat"),
                Provider("home", "家里的", models: "qwen"),
            ],
        });

        await host.AiSettings.SaveOverrideAsync(userId, new AiSettingsOverride
        {
            RemovedProviderIds = ["public-api"],
            Providers = [Provider("my-only", "只给我用的", models: "mine")],
        });

        var effective = await host.AiSettings.GetEffectiveAsync(userId);
        var ids = effective.Providers.Select(p => p.Id).ToList();

        Assert.DoesNotContain("public-api", ids);
        Assert.Contains("home", ids);
        Assert.Contains("my-only", ids);
    }

    [Fact]
    public async Task GetEffective_OverrideIsInvisibleToOtherAccounts()
    {
        await using var host = new TestHost();
        var alice = await host.CreateUserAsync("alice@example.com");
        var bob = await host.CreateUserAsync("bob@example.com");

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers = [Provider("home", "家里的", models: "qwen")],
        });

        await host.AiSettings.SaveOverrideAsync(alice, new AiSettingsOverride
        {
            DefaultModel = "alice-only-model",
            RemovedProviderIds = ["home"],
        });

        var aliceEffective = await host.AiSettings.GetEffectiveAsync(alice);
        var bobEffective = await host.AiSettings.GetEffectiveAsync(bob);

        Assert.Equal("alice-only-model", aliceEffective.DefaultModel);
        Assert.Empty(aliceEffective.Providers);

        // Bob 完全不受影响。
        Assert.Null(bobEffective.DefaultModel);
        Assert.Single(bobEffective.Providers);
    }

    [Fact]
    public async Task GetEffective_FallsBackToGlobalDefaultsWhenOverrideLeavesThemBlank()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "global-one",
            DefaultModel = "g-model",
            Providers = [Provider("global-one", "全局端点", models: "g-model")],
        });

        // 空白的覆盖项必须继续跟随全局：管理员换了全局地址，没覆盖的人应该自动跟着变。
        await host.AiSettings.SaveOverrideAsync(userId, new AiSettingsOverride
        {
            DefaultProviderId = "  ",
            RemovedProviderIds = [],
        });

        var effective = await host.AiSettings.GetEffectiveAsync(userId);

        Assert.Equal("global-one", effective.DefaultProviderId);
        Assert.Equal("g-model", effective.DefaultModel);
    }

    [Fact]
    public async Task SaveOverride_NullRowMeansFullyFollowingGlobal()
    {
        await using var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        await host.AiSettings.SaveOverrideAsync(userId, new AiSettingsOverride { DefaultModel = "temp" });
        Assert.NotNull(await host.AiSettings.GetOverrideAsync(userId));

        await host.AiSettings.SaveOverrideAsync(userId, null);
        Assert.Null(await host.AiSettings.GetOverrideAsync(userId));
    }

    [Fact]
    public async Task GetOverride_ReturnsNullForAnUnknownAccount()
    {
        await using var host = new TestHost();

        Assert.Null(await host.AiSettings.GetOverrideAsync("nobody"));
    }

    // ------------------------------------------------------------------
    // 坏输入不能把应用带走
    // ------------------------------------------------------------------

    [Fact]
    public async Task BrokenFile_KeepsTheLastGoodConfiguration()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "home",
            Providers = [Provider("home", "家里的", models: "qwen")],
        });

        var path = host.AiSettings.GlobalInfo.Path;
        await File.WriteAllTextAsync(path, "{ 这不是 JSON ");

        var info = host.AiSettings.ReloadGlobal();

        // 读失败要「看得见」，但正在生效的配置必须原样保留——
        // 否则家长手抖打错一个逗号，全家都用不了 AI。
        Assert.NotNull(info.LastError);
        Assert.Equal("home", host.AiSettings.GetGlobal().DefaultProviderId);
        Assert.Single(host.AiSettings.GetGlobal().Providers);
    }

    [Fact]
    public async Task BrokenFile_RecoversOnceItIsFixed()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            Providers = [Provider("home", "家里的", models: "qwen")],
        });

        var path = host.AiSettings.GlobalInfo.Path;
        await File.WriteAllTextAsync(path, "not json at all");
        Assert.NotNull(host.AiSettings.ReloadGlobal().LastError);

        await File.WriteAllTextAsync(path,
            """
            {
              "version": 1,
              "defaultProviderId": "fixed",
              "providers": [
                { "id": "fixed", "name": "修好了", "baseUrl": "http://127.0.0.1:1234/v1", "enabled": true, "models": ["m"] }
              ]
            }
            """);

        var info = host.AiSettings.ReloadGlobal();

        Assert.Null(info.LastError);
        Assert.Equal("fixed", host.AiSettings.GetGlobal().DefaultProviderId);
    }

    [Fact]
    public async Task FileWatcher_PicksUpExternalEdits()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "before",
            Providers = [Provider("before", "改之前", models: "m")],
        });

        var path = host.AiSettings.GlobalInfo.Path;
        await File.WriteAllTextAsync(path,
            """
            {
              "version": 1,
              "defaultProviderId": "after",
              "providers": [
                { "id": "after", "name": "改之后", "baseUrl": "http://127.0.0.1:1234/v1", "enabled": true, "models": ["m2"] }
              ]
            }
            """);

        // 这就是「不重启服务改全局配置」的那条路：FTP 上传 / vim 保存 / 挂载卷外面改都走它。
        await WaitForFileWatcherAsync(host.AiSettings, () =>
            host.AiSettings.GetGlobal().DefaultProviderId == "after");

        Assert.Equal("after", host.AiSettings.GetGlobal().DefaultProviderId);
    }

    [Fact]
    public async Task FileWatcher_SurvivesABrokenIntermediateSave()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "good",
            Providers = [Provider("good", "好的", models: "m")],
        });

        var path = host.AiSettings.GlobalInfo.Path;

        // 模拟编辑器「先清空再写入」造成的半截文件——这正是热加载最容易踩的坑。
        await File.WriteAllTextAsync(path, "{ \"version\": 1, \"providers\": [");
        await Task.Delay(700);

        Assert.Equal("good", host.AiSettings.GetGlobal().DefaultProviderId);

        // 写完整之后仍然能恢复（监听器没有被异常打断）。
        // 注意这里必须给一个可用的端点：Normalize 会把没有对应端点的 defaultProviderId 清掉，
        // 只写 defaultProviderId 的话读回来永远是 null，看着像「监听没生效」。
        await File.WriteAllTextAsync(path,
            """
            {
              "version": 1,
              "defaultProviderId": "recovered",
              "providers": [
                { "id": "recovered", "name": "恢复了", "baseUrl": "http://127.0.0.1:1234/v1", "enabled": true, "models": ["m"] }
              ]
            }
            """);

        await WaitForFileWatcherAsync(host.AiSettings, () =>
            host.AiSettings.GetGlobal().DefaultProviderId == "recovered");

        Assert.Null(host.AiSettings.GlobalInfo.LastError);
    }

    [Fact]
    public async Task GlobalInfo_ReportsWhereTheFileIsAndWhetherItIsWritable()
    {
        await using var host = new TestHost();

        var info = host.AiSettings.GlobalInfo;

        // 设置页要把这三件事显示出来，家长才知道「到底改的是哪个文件」。
        Assert.False(string.IsNullOrWhiteSpace(info.Path));
        Assert.True(info.FileExists);
        Assert.True(info.Writable);
        Assert.Null(info.LastError);
    }

    [Fact]
    public async Task SaveGlobal_NormalizesIdsAndUrls()
    {
        await using var host = new TestHost();

        await host.AiSettings.SaveGlobalAsync(new AiSettingsDocument
        {
            DefaultProviderId = "a",
            Providers =
            [
                new AiProviderConfig
                {
                    Id = "  a  ",
                    Name = "  A  ",
                    // 结尾多一个斜杠会让拼出来的地址变成 //chat/completions，
                    // 有些网关会当成两个不同的路径直接 404。
                    BaseUrl = "  http://127.0.0.1:1234/v1/  ",
                    ApiKey = "   ",
                    Models = ["  m1  ", "m1", ""],
                },
            ],
        });

        var provider = Assert.Single(host.AiSettings.GetGlobal().Providers);

        Assert.Equal("a", provider.Id);
        Assert.Equal("A", provider.Name);
        Assert.Equal("http://127.0.0.1:1234/v1", provider.BaseUrl);
        Assert.Null(provider.ApiKey);

        // 模型名去重去空白，空字符串不留在列表里。
        Assert.Equal(["m1"], provider.Models);
    }
}
