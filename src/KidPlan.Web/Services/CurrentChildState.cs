namespace KidPlan.Web.Services;

/// <summary>
/// 记住当前正在查看哪个小孩（每个浏览器连接一份，保存在 Blazor 的作用域服务里）。
///
/// 注意：页面 URL（/children/{id}/growth）才是权威来源，顶部导航和切换器都直接从 URL 解析，
/// 这个服务只负责记住“上次看过谁”，用于首页和导航的默认值。
/// </summary>
public sealed class CurrentChildState
{
    /// <summary>当前小孩 Id，未选择时为 null。</summary>
    public int? ChildId { get; private set; }

    /// <summary>记住当前小孩。只有值真的变化时才更新。</summary>
    public void Select(int? childId) => ChildId = childId;

    /// <summary>退出登录或切换账号时清空。</summary>
    public void Clear() => ChildId = null;
}
