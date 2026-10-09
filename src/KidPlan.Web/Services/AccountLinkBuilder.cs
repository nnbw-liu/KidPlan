using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace KidPlan.Web.Services;

/// <summary>
/// 拼装邮件里的账号链接（邮箱确认、重置密码）。
///
/// 两件必须做对的事：
/// 1) 域名。默认用当前请求的 BaseUri；部署在宝塔 Nginx 后面时，如果反代没有正确转发
///    X-Forwarded-Proto / X-Forwarded-Host，就用 <see cref="EmailSettings.PublicBaseUrl"/> 兜底。
/// 2) 编码。Identity 的 token 里可能出现 + / = 等字符，必须走
///    <see cref="NavigationManager.GetUriWithQueryParameters(string, IReadOnlyDictionary{string, object?})"/>
///    正确转义，手工拼接很容易把 token 弄坏。
/// </summary>
public sealed class AccountLinkBuilder(IOptions<EmailSettings> settings, NavigationManager navigationManager)
{
    public string Build(string relativePath, IReadOnlyDictionary<string, object?> queryParameters)
    {
        var configured = settings.Value.PublicBaseUrl;
        var baseUri = string.IsNullOrWhiteSpace(configured)
            ? navigationManager.BaseUri
            : configured.EndsWith('/') ? configured : configured + "/";

        var absolute = new Uri(new Uri(baseUri, UriKind.Absolute), relativePath);
        return navigationManager.GetUriWithQueryParameters(absolute.ToString(), queryParameters);
    }
}
