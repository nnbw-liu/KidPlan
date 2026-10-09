namespace KidPlan.Web.Services;

/// <summary>
/// SMTP 发信配置（appsettings.json 的 "Smtp" 节）。
///
/// 敏感值（<see cref="Password"/>，通常还有 <see cref="UserName"/>）不要写进 appsettings.json：
/// 那个文件会被打进 Docker 镜像。本机开发用 user-secrets，服务器上用 .env / 环境变量：
/// <code>
/// dotnet user-secrets set "Smtp:UserName" "you@qq.com"
/// dotnet user-secrets set "Smtp:Password" "授权码"
/// </code>
/// 注意 QQ 邮箱 / 163 邮箱这类服务用的是「SMTP 授权码」而不是登录密码。
/// </summary>
public sealed class EmailSettings
{
    public const string SectionName = "Smtp";

    /// <summary>是否启用发信。关掉之后所有发信调用都会抛异常，而不是静默失败。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>SMTP 服务器，QQ 邮箱是 smtp.qq.com。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>QQ 邮箱用 465（隐式 SSL）或 587（STARTTLS）。</summary>
    public int Port { get; set; } = 465;

    /// <summary>
    /// 加密方式，取值是 MailKit 的 SecureSocketOptions 名称：
    /// None / Auto / StartTls / StartTlsWhenAvailable / SslOnConnect。
    /// 465 端口配 SslOnConnect，587 端口配 StartTls。
    /// </summary>
    public string Security { get; set; } = "SslOnConnect";

    /// <summary>SMTP 登录用户名，QQ 邮箱就是完整邮箱地址。</summary>
    public string? UserName { get; set; }

    /// <summary>SMTP 授权码。</summary>
    public string? Password { get; set; }

    /// <summary>发件人地址。留空时退回 <see cref="UserName"/>（大多数邮箱两者相同）。</summary>
    public string? FromAddress { get; set; }

    /// <summary>发件人显示名。</summary>
    public string FromName { get; set; } = "KidPlan";

    /// <summary>连接与发送的超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// 邮件里链接要用的外部地址，例如 https://kidplan.example.com。
    /// 留空时用当前请求的 BaseUri —— 宝塔 Nginx 反代下需要正确转发
    /// X-Forwarded-Proto / X-Forwarded-Host，链接里的协议和域名才对。
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>实际使用的发件人地址。</summary>
    public string EffectiveFromAddress =>
        string.IsNullOrWhiteSpace(FromAddress) ? UserName ?? string.Empty : FromAddress;

    /// <summary>配置是否完整到可以发信。用于启动时给出明确提示。</summary>
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(EffectiveFromAddress)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>把配置里的字符串解析成 MailKit 的枚举，写错时退回 SslOnConnect。</summary>
    public MailKit.Security.SecureSocketOptions ParseSecurity() =>
        Enum.TryParse<MailKit.Security.SecureSocketOptions>(Security, ignoreCase: true, out var parsed)
            ? parsed
            : MailKit.Security.SecureSocketOptions.SslOnConnect;
}
