using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidPlan.Relay;

/// <summary>
/// 中继的运行参数。存成 exe 旁边的 <c>relay.json</c>，
/// 这样 <c>--install</c> 注册的计划任务里不用把令牌写进命令行——
/// 命令行参数会出现在任务计划程序的界面上，也可能被别人从进程列表里看到。
/// </summary>
public sealed class RelayOptions
{
    /// <summary>服务器地址，例如 <c>wss://kidplan.example.com</c> 或 <c>ws://127.0.0.1:7061</c>。</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>节点令牌（<c>kpr_</c> 开头），在网页的「模型设置 → 本机中继」里生成。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>心跳间隔（秒）。服务器那边按 60 秒节流落库，所以没必要更密。</summary>
    public int HeartbeatSeconds { get; set; } = 20;

    /// <summary>单次 HTTP 转发的超时（秒）。本地大模型首次加载可能很慢，默认给得宽松。</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>日志文件路径。留空表示只打印到控制台。</summary>
    public string? LogFile { get; set; }

    /// <summary>
    /// 允许转发到的目标。
    ///
    /// 默认只放行本机和内网地址。这是有意的：令牌一旦泄漏，别人就能借你的电脑
    /// 去访问任何地址（包括只有你能访问的内部服务）。真要连公网上的模型服务，
    /// 应当用「服务端直连」而不是中继——中继的用途就是解决「服务器连不到我的内网」。
    /// </summary>
    public bool AllowPublicTargets { get; set; }

    [JsonIgnore]
    public Uri ServerUri => ParseServer(Server);

    public static Uri ParseServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new ArgumentException("服务器地址不能为空，例如 wss://kidplan.example.com。", nameof(server));
        }

        var text = server.Trim();

        // 允许只写主机名：scheme 和路径由这里补齐，省得家长拼错。
        if (!text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            text = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "wss://" + text["https://".Length..]
                : text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "ws://" + text["http://".Length..]
                    : "wss://" + text;
        }

        var uri = new Uri(text, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Path = RelayProtocol.ConnectPath,
            Query = string.Empty,
        };

        return builder.Uri;
    }

    // ------------------------------------------------------------------
    // 读写
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>配置文件路径：优先用 exe 旁边的，写不进去（Program Files）就退回用户目录。</summary>
    public static string ResolveConfigPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "relay.json");

        try
        {
            // 探测一下能不能写：安装在 Program Files 下的程序目录普通用户写不了。
            var probe = beside + ".probe";
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return beside;
        }
        catch
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KidPlan.Relay");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "relay.json");
        }
    }

    public static RelayOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new RelayOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<RelayOptions>(File.ReadAllText(path), Json) ?? new RelayOptions();
        }
        catch (Exception ex)
        {
            // 配置读坏了不该直接崩——不然用户连「改回来」的机会都没有。
            Console.Error.WriteLine($"警告：读取 {path} 失败（{ex.Message}），先用空配置继续。");
            return new RelayOptions();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
