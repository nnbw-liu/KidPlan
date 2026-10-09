using System.Security.Cryptography;
using System.Text;
using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services.Ai;

public interface IRelayNodeService
{
    Task<IReadOnlyList<RelayNode>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>新建一个中继节点，返回明文令牌（只此一次）。</summary>
    Task<(RelayNode Node, string Token)> CreateAsync(string userId, string name, CancellationToken ct = default);

    Task DeleteAsync(string userId, int nodeId, CancellationToken ct = default);

    Task SetEnabledAsync(string userId, int nodeId, bool enabled, CancellationToken ct = default);

    /// <summary>用令牌换节点（中继建立连接时用）。令牌无效 / 被禁用返回 null。</summary>
    Task<RelayNode?> AuthenticateAsync(string token, CancellationToken ct = default);

    /// <summary>更新心跳与上报信息。</summary>
    Task TouchAsync(int nodeId, string? machineName, string? agentVersion, CancellationToken ct = default);
}

/// <summary>
/// 中继节点的令牌管理。
/// 库里只存 SHA-256，明文只在创建时显示一次——数据库被拖走也不能直接拿去连服务器。
/// </summary>
public sealed class RelayNodeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IRelayConnectionRegistryAccessor registry) : IRelayNodeService
{
    private const string TokenPrefix = "kpr_";

    public async Task<IReadOnlyList<RelayNode>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.RelayNodes
            .AsNoTracking()
            .Where(n => n.OwnerId == userId)
            .OrderByDescending(n => n.LastSeenAt)
            .ThenBy(n => n.Id)
            .ToListAsync(ct);
    }

    public async Task<(RelayNode Node, string Token)> CreateAsync(
        string userId,
        string name,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("请给中继起个名字，例如「书房的台式机」。", nameof(name));
        }

        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        var node = new RelayNode
        {
            OwnerId = userId,
            Name = name.Trim(),
            TokenHash = Hash(token),
            TokenPrefix = token[..Math.Min(12, token.Length)],
            CreatedAt = DateTimeOffset.Now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.RelayNodes.Add(node);
        await db.SaveChangesAsync(ct);

        return (node, token);
    }

    public async Task DeleteAsync(string userId, int nodeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var node = await db.RelayNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        db.RelayNodes.Remove(node);
        await db.SaveChangesAsync(ct);

        registry.Kick(nodeId);
    }

    public async Task SetEnabledAsync(string userId, int nodeId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var node = await db.RelayNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.OwnerId == userId, ct)
            ?? throw new ChildAccessDeniedException(0);

        node.Enabled = enabled;
        await db.SaveChangesAsync(ct);

        if (!enabled)
        {
            // 禁用之后必须把已经挂着的长连接踢掉，否则「禁用」只是改了个字段。
            registry.Kick(nodeId);
        }
    }

    public async Task<RelayNode?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        // 先 Trim 再判前缀：令牌常常是从聊天窗口 / 记事本里复制过来的，前后带空格很常见。
        // 顺序反了的话会直接判「格式不对」，报错信息还指向「令牌无效」，很难查。
        var trimmed = token?.Trim() ?? string.Empty;

        if (!trimmed.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(trimmed);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.RelayNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.TokenHash == hash && n.Enabled, ct);
    }

    public async Task TouchAsync(
        int nodeId,
        string? machineName,
        string? agentVersion,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var node = await db.RelayNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null)
        {
            return;
        }

        node.LastSeenAt = DateTimeOffset.Now;

        if (!string.IsNullOrWhiteSpace(machineName))
        {
            node.MachineName = Truncate(machineName, 100);
        }

        if (!string.IsNullOrWhiteSpace(agentVersion))
        {
            node.AgentVersion = Truncate(agentVersion, 30);
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// 把 <see cref="RelayConnectionRegistry"/> 包一层接口。
/// 原因是 <c>ChildAccessDeniedException</c> 之类的业务异常不该和连接管理耦合，
/// 而且中继服务要能被单元测试单独构造。这里只暴露「踢掉某节点」一个动作。
/// </summary>
public interface IRelayConnectionRegistryAccessor
{
    void Kick(int nodeId);
}

public sealed class RelayConnectionRegistryAccessor(RelayConnectionRegistry registry)
    : IRelayConnectionRegistryAccessor
{
    public void Kick(int nodeId) => registry.Kick(nodeId);
}
