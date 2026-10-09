namespace KidPlan.Web.Services;

/// <summary>
/// 访问不属于当前账号的小孩时抛出。所有业务服务都会先做归属校验，
/// 保证账号之间数据严格隔离。
/// </summary>
public sealed class ChildAccessDeniedException(int childId)
    : Exception($"小孩 #{childId} 不存在，或者不属于当前账号。")
{
    public int ChildId { get; } = childId;
}
