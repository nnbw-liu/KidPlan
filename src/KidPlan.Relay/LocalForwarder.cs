using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KidPlan.Relay;

/// <summary>转发结果。成功和失败都走这里，调用方统一回一个 <c>http-result</c> 给服务器。</summary>
public readonly record struct ForwardResult(int Status, string Body, string? Error)
{
    public static ForwardResult Ok(int status, string body) => new(status, body, null);

    public static ForwardResult Fail(string error) => new(0, string.Empty, error);
}

/// <summary>
/// 把服务器发来的 HTTP 请求在本地发出去。
///
/// 这里是整个中继唯一「照别人的话去访问网络」的地方，所以门的把守放在这里：
/// 默认只允许访问本机和内网地址。令牌一旦泄漏，攻击者能借这台电脑访问的东西
/// 就被限制在「本来也碰不到外网」的范围内。
/// </summary>
public sealed class LocalForwarder : IDisposable
{
    private readonly HttpClient _http;
    private readonly RelayOptions _options;
    private readonly Action<string> _log;

    public LocalForwarder(RelayOptions options, Action<string> log)
    {
        _options = options;
        _log = log;

        _http = new HttpClient(new SocketsHttpHandler
        {
            // 转发是「服务器等结果」的同步语义，超时由自己控制（见下面的 CancellationToken）。
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"KidPlan.Relay/{Program.Version}");
    }

    public async Task<ForwardResult> ForwardAsync(RelayMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.Url)
            || !Uri.TryCreate(message.Url, UriKind.Absolute, out var uri))
        {
            return ForwardResult.Fail($"服务器发来的地址不合法：{message.Url}");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return ForwardResult.Fail($"只支持 http / https，收到的是 {uri.Scheme}。");
        }

        if (!_options.AllowPublicTargets && !IsPrivateTarget(uri))
        {
            // 明确的拒绝而不是静默超时：家长在「测试连接」里应该看到原因。
            return ForwardResult.Fail(
                $"出于安全考虑，中继默认只转发到本机和内网地址，拒绝了 {uri.Host}。" +
                "如果确实需要连公网服务，请改用「服务端直连」；" +
                "非要用中继，就在 relay.json 里设 allowPublicTargets: true。");
        }

        using var request = new HttpRequestMessage(new HttpMethod(message.Method ?? "POST"), uri);

        if (!string.IsNullOrEmpty(message.Body))
        {
            request.Content = new StringContent(message.Body, Encoding.UTF8, "application/json");
        }

        if (message.Headers is not null)
        {
            foreach (var (key, value) in message.Headers)
            {
                // Content-Type 要落在内容上，放到请求头上会被 HttpClient 拒掉。
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null)
                    {
                        request.Content.Headers.Remove(key);
                        request.Content.Headers.TryAddWithoutValidation(key, value);
                    }

                    continue;
                }

                if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds)));

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token);

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            _log($"{message.Method} {uri} → {(int)response.StatusCode}（{body.Length} 字节）");

            return ForwardResult.Ok((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ForwardResult.Fail(
                $"等本地模型超过 {_options.RequestTimeoutSeconds} 秒还没回。" +
                "如果模型在冷启动，可以在 relay.json 里把 requestTimeoutSeconds 调大。");
        }
        catch (HttpRequestException ex)
        {
            // 最常见的一句就是「目标计算机积极拒绝」= 本地模型服务没开，
            // 所以把原始信息带上，别让家长去猜。
            return ForwardResult.Fail(
                $"连不上 {uri}：{ex.Message}。" +
                "请确认这个地址是「从这台电脑」能打开的，并且模型服务已经启动。");
        }
        catch (Exception ex)
        {
            return ForwardResult.Fail($"转发失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 目标是不是本机 / 内网。
    ///
    /// 只看主机名不够：<c>localhost</c>、<c>127.0.0.1</c>、<c>::1</c>、
    /// <c>192.168.x.x</c>、<c>10.x.x.x</c>、<c>172.16~31.x.x</c> 都得放行，
    /// 而 <c>example.com</c> 解析出来如果是内网地址（DNS 重绑定那种），
    /// 也应当在放行之列——反正它最终访问的就是内网。
    /// </summary>
    public static bool IsPrivateTarget(Uri uri)
    {
        // 用 DnsSafeHost 而不是 Host：IPv6 地址在 Host 里带方括号（[::1]），
        // 直接丢给 IPAddress.TryParse 会解析失败，于是 ::1 被误判成「公网」。
        var host = uri.DnsSafeHost;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            // 主机名解析一下再判断。解析不出来（DNS 挂了）就先当成不允许，
            // 反正真正转发时也会失败并给出明确原因。
            try
            {
                var resolved = Dns.GetHostAddresses(host);
                return resolved.Length > 0 && resolved.All(IsPrivateAddress);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        return IsPrivateAddress(address);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 的链路本地 fe80::/10 和唯一本地 fc00::/7。
            var bytes = address.GetAddressBytes();
            return (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                || (bytes[0] & 0xfe) == 0xfc
                // IPv4 映射地址（::ffff:192.168.1.1）拆出来再判一次。
                || (address.IsIPv4MappedToIPv6 && IsPrivateAddress(address.MapToIPv4()));
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 => octets[1] == 254,                       // 链路本地
            172 => octets[1] >= 16 && octets[1] <= 31,     // 172.16.0.0/12
            192 => octets[1] == 168,                       // 192.168.0.0/16
            // 0.0.0.0 之类的「本机任意网卡」也放过。
            0 => true,
            _ => false,
        };
    }

    public void Dispose() => _http.Dispose();
}
