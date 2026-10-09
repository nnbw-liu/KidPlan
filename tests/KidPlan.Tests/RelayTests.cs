using KidPlan.Relay;

namespace KidPlan.Tests;

/// <summary>
/// 本机中继。
///
/// 这里只测不依赖网络的两块：服务器地址怎么规范化、以及「允许转发到哪里」这条防线。
/// 后者的重要性容易被低估——中继本质上是**服务器借你的电脑去访问网络**，
/// 门没关好就成了一台放在你家内网里的开放代理。
/// </summary>
public class RelayTests
{
    // ------------------------------------------------------------------
    // 服务器地址规范化
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("wss://kidplan.example.com", "wss://kidplan.example.com/api/relay/connect")]
    [InlineData("ws://127.0.0.1:7061", "ws://127.0.0.1:7061/api/relay/connect")]
    // 家长很自然会照抄浏览器地址栏，所以 https/http 也要认。
    [InlineData("https://kidplan.example.com", "wss://kidplan.example.com/api/relay/connect")]
    [InlineData("http://127.0.0.1:7061", "ws://127.0.0.1:7061/api/relay/connect")]
    // 只写域名时默认按 wss（公网部署几乎一定在 TLS 后面）。
    [InlineData("kidplan.example.com", "wss://kidplan.example.com/api/relay/connect")]
    // 尾巴上的斜杠不该拼出双斜杠。
    [InlineData("wss://kidplan.example.com/", "wss://kidplan.example.com/api/relay/connect")]
    public void ParseServer_NormalizesEveryFormAParentMightType(string input, string expected)
        => Assert.Equal(expected, RelayOptions.ParseServer(input).ToString());

    [Fact]
    public void ParseServer_DropsAnyQueryTheUserPastedByMistake()
    {
        // 用户可能把带令牌的地址整条粘进来；查询串由程序自己拼，不能让两处都写。
        var uri = RelayOptions.ParseServer("wss://kidplan.example.com/some/path?token=stale");

        Assert.Equal("/api/relay/connect", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseServer_RejectsEmptyInput(string input)
        => Assert.Throws<ArgumentException>(() => RelayOptions.ParseServer(input));

    // ------------------------------------------------------------------
    // 转发目标的允许范围
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://LocalHost:1234/v1")]
    [InlineData("http://[::1]:1234/v1")]
    [InlineData("http://10.0.0.8:1234/v1")]
    [InlineData("http://192.168.1.50:1234/v1")]
    [InlineData("http://172.16.0.1:1234/v1")]
    [InlineData("http://172.31.255.254:1234/v1")]
    [InlineData("http://169.254.1.1:1234/v1")]
    [InlineData("http://nas.local:1234/v1")]
    public void IsPrivateTarget_AllowsLoopbackAndPrivateRanges(string url)
        => Assert.True(LocalForwarder.IsPrivateTarget(new Uri(url)));

    [Theory]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://8.8.8.8/v1")]
    // 172.32 已经出了 172.16/12 的范围，别把整个 172 段都放过。
    [InlineData("http://172.32.0.1:1234/v1")]
    // 192.169 不是 192.168/16。
    [InlineData("http://192.169.1.1:1234/v1")]
    public void IsPrivateTarget_BlocksPublicTargets(string url)
        => Assert.False(LocalForwarder.IsPrivateTarget(new Uri(url)));

    [Fact]
    public async Task ForwardAsync_RefusesPublicTargetAndSaysWhy()
    {
        var options = new RelayOptions { Server = "wss://example.com", Token = "kpr_x" };
        using var forwarder = new LocalForwarder(options, _ => { });

        var result = await forwarder.ForwardAsync(new RelayMessage
        {
            Type = "http",
            Id = "1",
            Method = "POST",
            Url = "https://api.deepseek.com/v1/chat/completions",
            Body = "{}",
        }, CancellationToken.None);

        Assert.Equal(0, result.Status);
        Assert.NotNull(result.Error);

        // 拒绝的理由要能指导下一步：要么改直连，要么显式开开关。
        Assert.Contains("内网", result.Error);
        Assert.Contains("allowPublicTargets", result.Error);
    }

    [Fact]
    public async Task ForwardAsync_AllowsPublicTargetWhenExplicitlyOptedIn()
    {
        // 打开开关之后不该再被那道门拦下——所以这次请求会真的发到网络层。
        // 具体结果取决于这台机器的网络环境，两种都算通过：
        //   · 域名解析不出来 / 端口不通 → 连接失败（Status = 0，错误里是「连不上 …」）；
        //   · 机器上跑着透明代理（TUN 模式那种）→ 代理替它回一个 502，拿到的是真实 HTTP 响应。
        // 原来这里断言的是 Status == 0，装了透明代理的机器上就会随机变红——
        // 要断言的是「没被公网闸门拦下」，不是「一定连不上」。
        var options = new RelayOptions
        {
            Server = "wss://example.com",
            Token = "kpr_x",
            AllowPublicTargets = true,
            RequestTimeoutSeconds = 5,
        };

        using var forwarder = new LocalForwarder(options, _ => { });

        var result = await forwarder.ForwardAsync(new RelayMessage
        {
            Type = "http",
            Id = "1",
            Method = "POST",
            // .invalid 是保留域名，正常情况下解析不出来，测试不会真的碰到外网。
            Url = "http://kidplan-test.invalid/v1/chat/completions",
        }, CancellationToken.None);

        var gateFired = result.Error is not null
            && (result.Error.Contains("allowPublicTargets") || result.Error.Contains("内网"));

        Assert.False(
            gateFired,
            $"打开 allowPublicTargets 之后不该再被公网闸门拦下，实际：status={result.Status}，error={result.Error}");

        // 「真的发出去了」也有证据：要么拿到 HTTP 响应，要么是连接层面的失败。
        Assert.True(
            result.Status != 0 || (result.Error is not null && result.Error.Contains("连不上")),
            $"既没有 HTTP 响应、也不是连接失败，这次转发到底发生了什么？status={result.Status}，error={result.Error}");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task ForwardAsync_RejectsGarbageUrls(string url)
    {
        var options = new RelayOptions { Server = "wss://example.com", Token = "kpr_x" };
        using var forwarder = new LocalForwarder(options, _ => { });

        var result = await forwarder.ForwardAsync(
            new RelayMessage { Type = "http", Id = "1", Method = "POST", Url = url },
            CancellationToken.None);

        Assert.Equal(0, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ForwardAsync_RejectsNonHttpSchemes()
    {
        var options = new RelayOptions { Server = "wss://example.com", Token = "kpr_x" };
        using var forwarder = new LocalForwarder(options, _ => { });

        // file:// 之类要是放过去，就变成「让服务器读我硬盘上的文件」了。
        var result = await forwarder.ForwardAsync(new RelayMessage
        {
            Type = "http",
            Id = "1",
            Method = "GET",
            Url = "file:///C:/Windows/win.ini",
        }, CancellationToken.None);

        Assert.Equal(0, result.Status);
        Assert.Contains("http", result.Error);
    }

    // ------------------------------------------------------------------
    // 报文与配置
    // ------------------------------------------------------------------

    [Fact]
    public void ProtocolConstants_MatchTheServerSide()
    {
        // 这两处是对着写的两份定义（中继刻意不引用 Web 项目），
        // 所以用一个测试把它们钉在一起：改了一边忘了另一边，这里会红。
        Assert.Equal(KidPlan.Web.Services.Ai.RelayEndpoint.Path, RelayProtocol.ConnectPath);
        Assert.Equal(KidPlan.Web.Services.Ai.RelayEndpoint.ProtocolVersion, RelayProtocol.Version);
    }

    [Fact]
    public void RelayMessage_UsesCamelCaseOnTheWire()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new RelayMessage { Type = "hello", AgentVersion = "1.0.0", MachineName = "PC" },
            RelayProtocol.Json);

        Assert.Contains("\"type\":\"hello\"", json);
        Assert.Contains("\"agentVersion\":\"1.0.0\"", json);

        // null 字段不该发出去，报文能小一点。
        Assert.DoesNotContain("nodeId", json);
    }

    [Fact]
    public void RelayMessage_DeserializesWhatTheServerSends()
    {
        var message = System.Text.Json.JsonSerializer.Deserialize<RelayMessage>(
            """
            {"type":"http","id":"abc","method":"POST","url":"http://127.0.0.1:1234/v1/chat/completions",
             "headers":{"Authorization":"Bearer sk-x","Content-Type":"application/json"},"body":"{\"model\":\"m\"}"}
            """,
            RelayProtocol.Json);

        Assert.NotNull(message);
        Assert.Equal("http", message.Type);
        Assert.Equal("abc", message.Id);
        Assert.Equal("POST", message.Method);
        Assert.Equal("Bearer sk-x", message.Headers!["Authorization"]);
        Assert.Contains("model", message.Body);
    }

    [Fact]
    public void Options_RoundTripThroughAFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kidplan-relay-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "relay.json");

        try
        {
            new RelayOptions
            {
                Server = "wss://kidplan.example.com",
                Token = "kpr_abcdefghijklmnop",
                RequestTimeoutSeconds = 600,
                AllowPublicTargets = true,
                HeartbeatSeconds = 30,
            }.Save(path);

            var loaded = RelayOptions.Load(path);

            Assert.Equal("wss://kidplan.example.com", loaded.Server);
            Assert.Equal("kpr_abcdefghijklmnop", loaded.Token);
            Assert.Equal(600, loaded.RequestTimeoutSeconds);
            Assert.True(loaded.AllowPublicTargets);
            Assert.Equal(30, loaded.HeartbeatSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsDefaultsForAMissingFile()
    {
        var loaded = RelayOptions.Load(
            Path.Combine(Path.GetTempPath(), "kidplan-relay-tests", "definitely-missing.json"));

        Assert.Equal(string.Empty, loaded.Server);
        // 默认只放行内网：这是安全默认值，不该因为文件缺失就变成「全放开」。
        Assert.False(loaded.AllowPublicTargets);
        Assert.Equal(20, loaded.HeartbeatSeconds);
    }

    [Fact]
    public void Load_DoesNotThrowOnBrokenJson()
    {
        // 配置写坏了必须还能启动：不然家长连「改成对的」这一步都做不到，
        // 只能靠手工找文件删掉。
        var directory = Path.Combine(Path.GetTempPath(), "kidplan-relay-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "relay.json");

        try
        {
            File.WriteAllText(path, "{ this is not json ");

            var loaded = RelayOptions.Load(path);

            Assert.Equal(string.Empty, loaded.Server);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
