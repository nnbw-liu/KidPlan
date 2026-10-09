using System.Text.RegularExpressions;

namespace KidPlan.Web.Components.Shared;

/// <summary>
/// 从地址里解析出当前正在查看的小孩 Id。
///
/// 业务页面的地址形如 /children/12/growth、/children/12/meals、/children/12/edit，
/// 所以 URL 本身就是“当前小孩”的权威来源，导航栏和顶部切换器都从这里取值，
/// 而不是依赖组件之间的事件通知（事件顺序在导航时并不稳定）。
/// </summary>
public static partial class UrlChildId
{
    public static int? TryParse(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        var match = ChildIdPattern().Match(uri);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    [GeneratedRegex(@"/children/(\d+)(?:/|$|\?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChildIdPattern();
}
