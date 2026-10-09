using System.Net.WebSockets;
using System.Text;
using KidPlan.Web.Services;
using KidPlan.Web.Services.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidPlan.Tests;

/// <summary>
/// 中继节点的令牌管理。
///
/// 要点是「库里不留明文」：数据库被拖走（或者运维顺手把备份发出去）之后，
/// 拿到库的人也不能直接冒充节点连上服务器。所以有一半用例盯着存储形态。
/// </summary>
public class RelayNodeServiceTests
{
    private static async Task<(TestHost Host, string UserId, IRelayNodeService Nodes)> ArrangeAsync()
    {
        var host = new TestHost();
        var userId = await host.CreateUserAsync("parent@example.com");

        // 服务是 Scoped，作用域用完就丢也没关系：它只依赖单例注册表和 DB 工厂。
        var nodes = host.CreateScope().ServiceProvider.GetRequiredService<IRelayNodeService>();
        return (host, userId, nodes);
    }

    // ------------------------------------------------------------------
    // 令牌
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsAPlaintextTokenExactlyOnce()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, token) = await nodes.CreateAsync(userId, "书房的台式机");

        Assert.StartsWith("kpr_", token);
        Assert.Equal("书房的台式机", node.Name);
        Assert.True(node.Enabled);

        // 32 字节随机数 → 43 个 base64url 字符，加前缀共 47。够长才经得起离线爆破。
        Assert.True(token.Length >= 46, $"令牌太短了：{token.Length}");

        // 列表接口只回实体，明文再也拿不回来。
        var listed = Assert.Single(await nodes.ListAsync(userId));
        Assert.Equal(node.Id, listed.Id);
    }

    [Fact]
    public async Task Create_StoresOnlyAHashNeverThePlaintext()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, token) = await nodes.CreateAsync(userId, "家里的");

        Assert.StartsWith("kpr_", node.TokenPrefix);

        // 前缀给人认的（界面上显示 kpr_xxxxxxxx…），但不等于整条令牌。
        Assert.NotEqual(token, node.TokenPrefix);
        Assert.Equal(64, node.TokenHash.Length); // SHA-256 的十六进制

        // 核心断言：库里任何一列都不能出现明文令牌的后半段。
        var secret = token[12..];
        Assert.DoesNotContain(secret, node.TokenHash);
        Assert.DoesNotContain(secret, node.TokenPrefix);
        Assert.DoesNotContain(secret, node.Name);
    }

    [Fact]
    public async Task Authenticate_AcceptsTheTokenAndRejectsEverythingElse()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, token) = await nodes.CreateAsync(userId, "家里的");

        var authenticated = await nodes.AuthenticateAsync(token);
        Assert.NotNull(authenticated);
        Assert.Equal(node.Id, authenticated.Id);

        // 从聊天窗口复制常会带上空格，不该因此连不上。
        Assert.NotNull(await nodes.AuthenticateAsync("  " + token + "  "));

        Assert.Null(await nodes.AuthenticateAsync("kpr_wrong"));
        Assert.Null(await nodes.AuthenticateAsync(""));
        Assert.Null(await nodes.AuthenticateAsync("   "));
        Assert.Null(await nodes.AuthenticateAsync(null!));

        // 前缀不对就直接退掉，连哈希都不算。
        Assert.Null(await nodes.AuthenticateAsync(token[4..]));
    }

    [Fact]
    public async Task Authenticate_RejectsADisabledNode()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, token) = await nodes.CreateAsync(userId, "家里的");
        await nodes.SetEnabledAsync(userId, node.Id, false);

        // 「我怀疑这台机器丢了」的第一反应就是停用，旧令牌必须立刻失效。
        Assert.Null(await nodes.AuthenticateAsync(token));

        await nodes.SetEnabledAsync(userId, node.Id, true);
        Assert.NotNull(await nodes.AuthenticateAsync(token));
    }

    [Fact]
    public async Task Delete_MakesTheTokenStopWorking()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, token) = await nodes.CreateAsync(userId, "家里的");

        await nodes.DeleteAsync(userId, node.Id);

        Assert.Empty(await nodes.ListAsync(userId));
        Assert.Null(await nodes.AuthenticateAsync(token));
    }

    [Fact]
    public async Task Mutations_RefuseNodesOwnedBySomebodyElse()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var other = await host.CreateUserAsync("other@example.com");
        var (node, token) = await nodes.CreateAsync(userId, "家里的");

        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => nodes.SetEnabledAsync(other, node.Id, false));
        await Assert.ThrowsAsync<ChildAccessDeniedException>(() => nodes.DeleteAsync(other, node.Id));

        Assert.NotNull(await nodes.AuthenticateAsync(token));
        Assert.True(Assert.Single(await nodes.ListAsync(userId)).Enabled);
    }

    [Fact]
    public async Task Create_RejectsABlankName()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        // 没有名字的话两个节点在界面上长得一样，根本分不清该删哪个。
        await Assert.ThrowsAsync<ArgumentException>(() => nodes.CreateAsync(userId, "   "));
    }

    // ------------------------------------------------------------------
    // 列表与心跳
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_IsolatesOwnersAndPutsRecentlySeenNodesFirst()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var other = await host.CreateUserAsync("other@example.com");

        var (first, _) = await nodes.CreateAsync(userId, "先建的");
        var (second, _) = await nodes.CreateAsync(userId, "后建的");
        await nodes.CreateAsync(other, "别人的");

        var mine = await nodes.ListAsync(userId);

        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, n => n.Name == "别人的");

        // 都没连过时按 Id 升序，顺序是稳定的。
        Assert.Equal([first.Id, second.Id], mine.Select(n => n.Id));

        // 心跳过的排最前面——家长最想知道的是「哪台机器现在在线」。
        await nodes.TouchAsync(second.Id, "DESKTOP-ABC", "1.0.0");
        Assert.Equal(second.Id, (await nodes.ListAsync(userId))[0].Id);
    }

    [Fact]
    public async Task Touch_RecordsMachineInfoAndTruncatesOverlongValues()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, _) = await nodes.CreateAsync(userId, "家里的");

        await nodes.TouchAsync(node.Id, new string('x', 500), new string('v', 100));

        var listed = Assert.Single(await nodes.ListAsync(userId));

        // 列宽是 100 / 30。不截断的话在 MySQL 严格模式下会直接抛异常，
        // 表现成「心跳莫名其妙断了」，很难查。
        Assert.Equal(100, listed.MachineName!.Length);
        Assert.Equal(30, listed.AgentVersion!.Length);
        Assert.NotNull(listed.LastSeenAt);
    }

    [Fact]
    public async Task Touch_DoesNotEraseKnownInfoWithBlankValues()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        var (node, _) = await nodes.CreateAsync(userId, "家里的");

        await nodes.TouchAsync(node.Id, "DESKTOP-ABC", "1.0.0");
        await nodes.TouchAsync(node.Id, null, "  ");

        var listed = Assert.Single(await nodes.ListAsync(userId));

        // 老版本客户端可能不上报这些字段，不能把已经知道的信息擦掉。
        Assert.Equal("DESKTOP-ABC", listed.MachineName);
        Assert.Equal("1.0.0", listed.AgentVersion);
    }

    [Fact]
    public async Task Touch_OnAMissingNodeIsANoOp()
    {
        var (host, userId, nodes) = await ArrangeAsync();
        await using var _ = host;

        await nodes.CreateAsync(userId, "家里的");

        // 「节点刚被删掉、心跳紧接着到达」是很正常的竞态，后台任务不能因此抛异常。
        await nodes.TouchAsync(9999, "DESKTOP-ABC", "1.0.0");
    }
}

/// <summary>
/// 在线连接注册表。
///
/// 这一层的职责是「服务器手上的那根线现在还能不能用」，
/// 出错的表现都很隐蔽：要么往死连接上一直发请求，要么界面一直转圈。
/// </summary>
public class RelayConnectionRegistryTests
{
    /// <summary>只记录「被写进去什么」的假 WebSocket，测试不需要真开端口。</summary>
    private sealed class FakeSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public List<string> Sent { get; } = [];

        public bool CloseOutputCalled { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public void Break() => _state = WebSocketState.Aborted;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            }

            return Task.CompletedTask;
        }
    }

    private static RelayConnection Connection(int nodeId, string ownerId, FakeSocket socket, string name = "家里的")
        => new(nodeId, ownerId, name, socket, NullLogger.Instance);

    private static AiHttpRequest Request() => new(
        "POST",
        "http://127.0.0.1:1234/v1/chat/completions",
        new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        "{\"model\":\"m\"}");

    [Fact]
    public void Register_ReplacesTheOldConnectionForTheSameNode()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var first = Connection(1, "owner", new FakeSocket());
        var second = Connection(1, "owner", new FakeSocket());

        registry.Register(first);
        registry.Register(second);

        // 家里断电重启时，旧连接的 TCP 往往还没超时、新连接已经上来了。
        // 不替换的话，服务器会一直往那条死线上发请求。
        Assert.Same(second, registry.Find(1));
        Assert.True(registry.IsOnline(1));
        Assert.Single(registry.ListForOwner("owner"));
    }

    [Fact]
    public async Task Register_FailsRequestsStillWaitingOnTheReplacedConnection()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var first = Connection(1, "owner", new FakeSocket());
        registry.Register(first);

        var pending = first.SendAsync(Request(), TimeSpan.FromMinutes(5), CancellationToken.None);

        registry.Register(Connection(1, "owner", new FakeSocket()));

        // 界面不能一直转圈：换连接时要把还挂着的请求一次性放掉。
        var response = await pending;

        Assert.Equal(0, response.StatusCode);
        Assert.Contains("重新连接", response.Error);
    }

    [Fact]
    public async Task Kick_FailsPendingRequestsAndTakesTheNodeOffline()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var socket = new FakeSocket();
        var connection = Connection(1, "owner", socket);
        registry.Register(connection);

        var pending = connection.SendAsync(Request(), TimeSpan.FromMinutes(5), CancellationToken.None);

        // 停用 / 删除节点走的就是这条路。只改数据库字段是不够的：
        // 已握上的长连接不会自己断，表现就是「我明明禁用了，它还在用」。
        registry.Kick(1);

        Assert.False(registry.IsOnline(1));
        Assert.Null(registry.Find(1));
        Assert.Contains("停用", (await pending).Error);

        // 光看注册表是不够的：注册表里删掉了，服务端那条接收循环可能还在
        // 原来的 socket 上等消息。端到端验证时就是这里出的问题——
        // 界面提示「连接已经断开」，中继那边却一直以为自己连着。
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public void Register_AbortsTheSocketOfTheReplacedConnection()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var stale = new FakeSocket();
        registry.Register(Connection(1, "owner", stale));

        // 家里断电重启：旧 socket 的 TCP 往往还没超时。不主动断掉的话，
        // 那条死连接会一直挂在服务器上占着资源。
        registry.Register(Connection(1, "owner", new FakeSocket()));

        Assert.Equal(WebSocketState.Aborted, stale.State);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var connection = Connection(1, "owner", new FakeSocket());

        // 正常收尾时 Dispose 会被调用两次：一次来自 Kick / 替换，一次来自
        // PumpAsync 的 finally。第二次必须是无害的（内部要释放的信号量只放一次）。
        connection.Dispose();
        connection.Dispose();
    }

    [Fact]
    public void Kick_OnAnOfflineNodeIsANoOp()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        registry.Kick(42);
    }

    [Fact]
    public void Unregister_OnlyRemovesTheConnectionItWasGiven()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var stale = Connection(1, "owner", new FakeSocket());
        var current = Connection(1, "owner", new FakeSocket());

        // 旧连接的收尾逻辑迟到一步，不能把新连接顺手删掉。
        registry.Register(current);
        registry.Unregister(1, stale);

        Assert.Same(current, registry.Find(1));

        registry.Unregister(1, current);
        Assert.False(registry.IsOnline(1));
    }

    [Fact]
    public void FindForOwner_HonoursThePreferredNode()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);

        var first = Connection(1, "alice", new FakeSocket(), "书房");
        var second = Connection(2, "alice", new FakeSocket(), "客厅");
        var bobs = Connection(3, "bob", new FakeSocket(), "别人的");

        registry.Register(first);
        registry.Register(second);
        registry.Register(bobs);

        Assert.Same(second, registry.FindForOwner("alice", 2));

        // 指定了一个不属于自己的节点，不能顺手把别人的连接给他。
        Assert.Null(registry.FindForOwner("alice", 3));

        // 不指定就取最近活跃的那个。
        second.LastSeenAt = DateTimeOffset.Now.AddMinutes(5);
        Assert.Same(second, registry.FindForOwner("alice"));

        Assert.Null(registry.FindForOwner("nobody"));
        Assert.Equal(2, registry.ListForOwner("alice").Count);
    }

    [Fact]
    public async Task SendAsync_RoundTripsARequestAndCompletesWithTheResponse()
    {
        var registry = new RelayConnectionRegistry(NullLogger<RelayConnectionRegistry>.Instance);
        var socket = new FakeSocket();
        var connection = Connection(1, "owner", socket);

        registry.Register(connection);

        var pending = connection.SendAsync(Request(), TimeSpan.FromSeconds(10), CancellationToken.None);

        // 等到报文真的写进 socket，再取它的 id 来「回传」响应。
        RelayMessage? sent = null;
        var deadline = DateTimeOffset.Now.AddSeconds(5);

        while (sent is null && DateTimeOffset.Now < deadline)
        {
            lock (socket.Sent)
            {
                if (socket.Sent.Count > 0)
                {
                    sent = System.Text.Json.JsonSerializer.Deserialize<RelayMessage>(
                        socket.Sent[0], AiJson.Compact);
                }
            }

            if (sent is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(sent);
        Assert.Equal("http", sent.Type);
        Assert.NotNull(sent.Id);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", sent.Url);
        Assert.Equal("{\"model\":\"m\"}", sent.Body);

        connection.Complete(sent.Id!, new AiHttpResponse(200, "{\"ok\":true}"));

        var response = await pending;
        Assert.True(response.IsSuccess);
        Assert.Equal("{\"ok\":true}", response.Body);
    }

    [Fact]
    public async Task SendAsync_ReturnsATimeoutInsteadOfHanging()
    {
        var socket = new FakeSocket();
        var connection = Connection(1, "owner", socket);

        // 本机模型加载慢是常态，所以要能超时退出，而不是让界面永远转圈。
        var response = await connection.SendAsync(Request(), TimeSpan.FromMilliseconds(80), CancellationToken.None);

        Assert.Equal(0, response.StatusCode);
        Assert.Contains("超时", response.Error);
    }

    [Fact]
    public async Task SendAsync_ReportsAFailureWhenTheSocketIsGone()
    {
        var socket = new FakeSocket();
        var connection = Connection(1, "owner", socket);

        socket.Break();

        var response = await connection.SendAsync(Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, response.StatusCode);
        Assert.Contains("断开", response.Error);
    }

    [Fact]
    public async Task Complete_IgnoresAnUnknownOrLateResponse()
    {
        var connection = Connection(1, "owner", new FakeSocket());

        // 超时之后响应才回来是常事，不能因此抛异常。
        connection.Complete("no-such-id", new AiHttpResponse(200, "{}"));

        var response = await connection.SendAsync(Request(), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        connection.Complete("whatever", new AiHttpResponse(200, "{}"));

        Assert.Equal(0, response.StatusCode);
    }
}
