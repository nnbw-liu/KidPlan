using KidPlan.Web.Data;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MimeKit;

namespace KidPlan.Web.Services;

/// <summary>
/// 通过 SMTP 发送账号相关邮件（邮箱确认、重置密码）。
///
/// Identity 在需要发信时会从 DI 里取 <see cref="IEmailSender{TUser}"/>，
/// 不注册这个实现的话注册流程会在发确认邮件时直接报错。
/// </summary>
public sealed class SmtpEmailSender : IEmailSender<ApplicationUser>
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailSettings> settings, ILogger<SmtpEmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(
            email,
            "确认你的 KidPlan 邮箱",
            $"""
             <p>请点击下面的链接确认这个邮箱，确认后就能登录 KidPlan 了。</p>
             <p style="margin:22px 0">
               <a href="{confirmationLink}"
                  style="background:#2e7d32;color:#fff;padding:11px 22px;border-radius:6px;text-decoration:none">
                 确认邮箱
               </a>
             </p>
             <p style="color:#666;font-size:13px">链接 24 小时内有效。如果不是你本人注册的，忽略这封邮件即可。</p>
             """,
            $"请打开下面的链接确认你的 KidPlan 邮箱（24 小时内有效）：{confirmationLink}",
            $"确认 KidPlan 邮箱 - {Describe(user)}");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(
            email,
            "重置你的 KidPlan 密码",
            $"""
             <p>我们收到了重置 KidPlan 密码的请求。点击下面的链接设置新密码：</p>
             <p style="margin:22px 0">
               <a href="{resetLink}"
                  style="background:#2e7d32;color:#fff;padding:11px 22px;border-radius:6px;text-decoration:none">
                 设置新密码
               </a>
             </p>
             <p style="color:#666;font-size:13px">
               链接 24 小时内有效。如果不是你本人操作的，忽略这封邮件即可，你的密码不会被修改。
             </p>
             """,
            $"请打开下面的链接重置你的 KidPlan 密码（24 小时内有效）：{resetLink}",
            $"重置 KidPlan 密码 - {Describe(user)}");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendAsync(
            email,
            "重置你的 KidPlan 密码",
            $"""
             <p>重置密码的验证码是：</p>
             <p style="font-size:26px;font-weight:700;letter-spacing:3px;margin:18px 0">{resetCode}</p>
             <p style="color:#666;font-size:13px">如果不是你本人操作的，忽略这封邮件即可。</p>
             """,
            $"你的 KidPlan 重置密码验证码是：{resetCode}",
            $"重置 KidPlan 密码 - {Describe(user)}");

    private async Task SendAsync(string to, string subject, string htmlBody, string textBody, string logSubject)
    {
        if (!_settings.Enabled)
        {
            throw new InvalidOperationException("邮件发送已被配置关闭（Smtp:Enabled=false）。");
        }

        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.EffectiveFromAddress))
        {
            throw new InvalidOperationException(
                "SMTP 未配置完整：请设置 Smtp:Host 与 Smtp:FromAddress（或 Smtp:UserName）。");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.EffectiveFromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = Wrap(htmlBody), TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = _settings.TimeoutSeconds * 1000 };

        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, _settings.ParseSecurity());

            // 有些内网中继不需要认证，所以只在配了用户名时才登录。
            if (!string.IsNullOrWhiteSpace(_settings.UserName))
            {
                await client.AuthenticateAsync(_settings.UserName, _settings.Password ?? string.Empty);
            }

            await client.SendAsync(message);
            await client.DisconnectAsync(quit: true);

            _logger.LogInformation("已发送邮件「{Subject}」到 {To}", logSubject, to);
        }
        catch (Exception ex)
        {
            // 这里把 SMTP 的原始报错完整记进日志（方便排查授权码、端口、被墙等问题），
            // 但不把它抛给页面——报错里可能带账号信息。
            _logger.LogError(ex, "发送邮件「{Subject}」到 {To} 失败（SMTP {Host}:{Port}）。",
                logSubject, to, _settings.Host, _settings.Port);

            throw new InvalidOperationException(
                $"邮件发送失败（{_settings.Host}:{_settings.Port}），请检查 SMTP 授权码与端口配置。", ex);
        }
    }

    private static string Describe(ApplicationUser user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? "KidPlan 用户" : user.DisplayName;

    /// <summary>套一层最简单的邮件外壳，保证在手机客户端上也不难看。</summary>
    private static string Wrap(string body) => $"""
        <div style="font-family:-apple-system,'Segoe UI','Microsoft YaHei',sans-serif;
                    font-size:15px;line-height:1.7;color:#222;max-width:560px">
          <div style="font-size:19px;font-weight:700;margin-bottom:16px">🌱 KidPlan</div>
          {body}
          <hr style="border:none;border-top:1px solid #eee;margin:26px 0 12px" />
          <div style="color:#999;font-size:12px">KidPlan · 孩子的成长、身体与饮食记录</div>
        </div>
        """;
}
