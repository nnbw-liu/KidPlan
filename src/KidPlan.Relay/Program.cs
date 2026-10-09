using System.Diagnostics;
using System.Text;

namespace KidPlan.Relay;

/// <summary>
/// KidPlan 本机中继。
///
/// 解决的问题：应用部署在云端，模型跑在家里的电脑上，服务器**永远**连不到你家的 127.0.0.1。
/// 这是网络拓扑决定的，改配置解决不了。所以把方向反过来——
/// 这台电脑主动连到服务器挂一条长连接，服务器把模型请求顺着连接发下来，
/// 由中继在本地发起真实请求。于是「手机在外面用家里的模型」也顺带成立了：
/// 手机访问的是云端页面，模型请求走「云端 → 这台电脑 → 本地模型」。
/// </summary>
public static class Program
{
    public const string Version = "1.0.0";

    private const string TaskName = "KidPlan.Relay";

    private static readonly CancellationTokenSource Shutdown = new();

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "--version" or "-v"))
        {
            Console.WriteLine($"KidPlan.Relay {Version}");
            return 0;
        }

        var configPath = RelayOptions.ResolveConfigPath();
        var options = RelayOptions.Load(configPath);

        // 命令行参数覆盖配置文件：临时试一下很方便，而 --install 会把它们存进配置文件。
        var fromArgs = ParseArgs(args);
        if (fromArgs.Server is not null)
        {
            options.Server = fromArgs.Server;
        }

        if (fromArgs.Token is not null)
        {
            options.Token = fromArgs.Token;
        }

        if (fromArgs.AllowPublicTargets is { } allowPublic)
        {
            options.AllowPublicTargets = allowPublic;
        }

        if (fromArgs.TimeoutSeconds is { } timeout)
        {
            options.RequestTimeoutSeconds = timeout;
        }

        if (args.Any(a => a == "--install"))
        {
            options.Save(configPath);
            return Install(configPath, options);
        }

        if (args.Any(a => a == "--uninstall"))
        {
            return Uninstall();
        }

        if (args.Any(a => a == "--status"))
        {
            return Status(configPath, options);
        }

        // 命令行里明确给了地址或令牌，就顺手记下来：
        // 否则家长按 README 敲一次、下次再双击 exe 又变成「没配置」，很容易以为坏了。
        if (fromArgs.Server is not null || fromArgs.Token is not null)
        {
            try
            {
                options.Save(configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"警告：参数已生效，但写入 {configPath} 失败（{ex.Message}），下次启动要重新输入。");
            }
        }

        return await RunAsync(configPath, options);
    }

    // ------------------------------------------------------------------
    // 正常运行
    // ------------------------------------------------------------------

    private static async Task<int> RunAsync(string configPath, RelayOptions options)
    {
        var logger = new Logger(options.LogFile);

        logger.Info($"KidPlan.Relay {Version} 启动。");
        logger.Info($"配置文件：{configPath}");

        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.Token))
        {
            logger.Error("还没有配置服务器地址或令牌。");
            Console.WriteLine();
            Console.WriteLine("请先这样启动一次：");
            Console.WriteLine("  KidPlan.Relay.exe --server wss://你的域名 --token kpr_xxxxxxxx");
            Console.WriteLine();
            Console.WriteLine("令牌在网页的「模型设置 → 本机中继」里生成。");
            return 2;
        }

        try
        {
            logger.Info($"服务器：{options.ServerUri.GetLeftPart(UriPartial.Path)}");
        }
        catch (ArgumentException ex)
        {
            logger.Error(ex.Message);
            return 2;
        }

        // Ctrl+C 和「窗口被关掉」都要能干净退出，否则计划任务里会留下一个半死的进程。
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.Info("收到退出信号，正在断开…");
            Shutdown.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Cancel();

        using var forwarder = new LocalForwarder(options, logger.Info);
        var client = new RelayClient(options, forwarder, logger.Info);

        try
        {
            await client.RunAsync(Shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }

        logger.Info("已退出。");
        return 0;
    }

    // ------------------------------------------------------------------
    // 开机自启（Windows 计划任务）
    // ------------------------------------------------------------------

    /// <summary>
    /// 用计划任务而不是 Windows 服务：服务需要管理员权限注册，
    /// 而且服务里跑的东西看不到界面、出问题不好查。计划任务「登录时启动」够用，
    /// 家长在任务管理器里也看得见、关得掉。
    /// </summary>
    private static int Install(string configPath, RelayOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.Token))
        {
            Console.Error.WriteLine("--install 需要同时给出 --server 和 --token（它们会被写进配置文件）。");
            return 2;
        }

        var exe = Environment.ProcessPath ?? "KidPlan.Relay.exe";

        Console.WriteLine($"配置文件：{configPath}");
        Console.WriteLine($"程序：{exe}");

        var result = RunSchtasks(
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{exe}\" --run",
            "/SC", "ONLOGON",
            // 不用 /RL HIGHEST：中继只是发 HTTP 请求，不需要管理员权限，
            // 要求提权只会让家长在 UAC 弹窗面前卡住。
            "/RL", "LIMITED",
            "/F");

        if (result is not 0)
        {
            Console.Error.WriteLine("注册计划任务失败。可以手动到「任务计划程序」里创建一个「登录时」触发的任务。");
            return result;
        }

        Console.WriteLine("✅ 已注册开机自启（计划任务名：KidPlan.Relay，登录时启动）。");
        Console.WriteLine("   想立刻跑一次：schtasks /Run /TN KidPlan.Relay");
        Console.WriteLine("   想取消：KidPlan.Relay.exe --uninstall");
        return 0;
    }

    private static int Uninstall()
    {
        var result = RunSchtasks("/Delete", "/TN", TaskName, "/F");

        Console.WriteLine(result is 0
            ? "✅ 已删除开机自启任务（配置文件保留，可以删掉 relay.json 彻底清干净）。"
            : "删除失败：可能本来就没有这个任务。");

        return 0;
    }

    private static int Status(string configPath, RelayOptions options)
    {
        Console.WriteLine($"KidPlan.Relay {Version}");
        Console.WriteLine($"配置文件：{configPath}");
        Console.WriteLine($"服务器：{(string.IsNullOrWhiteSpace(options.Server) ? "(未配置)" : options.Server)}");
        Console.WriteLine($"令牌：{(string.IsNullOrWhiteSpace(options.Token) ? "(未配置)" : Mask(options.Token))}");
        Console.WriteLine($"允许公网目标：{options.AllowPublicTargets}");
        Console.WriteLine($"转发超时：{options.RequestTimeoutSeconds} 秒");

        var query = RunSchtasks("/Query", "/TN", TaskName);
        Console.WriteLine(query is 0 ? "开机自启：已注册" : "开机自启：未注册");

        return 0;
    }

    private static int RunSchtasks(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            // 一定要把输出读掉：管道写满会死锁，schtasks 也可能因为中文输出比较大。
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"调用 schtasks 失败：{ex.Message}");
            return -1;
        }
    }

    // ------------------------------------------------------------------
    // 参数
    // ------------------------------------------------------------------

    private sealed record ParsedArgs(
        string? Server,
        string? Token,
        bool? AllowPublicTargets,
        int? TimeoutSeconds);

    private static ParsedArgs ParseArgs(string[] args)
    {
        string? server = null;
        string? token = null;
        bool? allowPublic = null;
        int? timeout = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" when i + 1 < args.Length:
                    server = args[++i];
                    break;

                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;

                case "--allow-public":
                    allowPublic = true;
                    break;

                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    timeout = seconds;
                    i++;
                    break;
            }
        }

        return new ParsedArgs(server, token, allowPublic, timeout);
    }

    private static string Mask(string token)
        => token.Length <= 8 ? "••••" : $"{token[..8]}••••{token[^4..]}";

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            KidPlan.Relay {Version} —— 让云端应用用上你本机（或内网）的模型。

            用法：
              KidPlan.Relay.exe --server <地址> --token <令牌>      正常启动（会记住这两个参数）
              KidPlan.Relay.exe --install ...                      注册为开机自启（计划任务）
              KidPlan.Relay.exe --uninstall                        取消开机自启
              KidPlan.Relay.exe --status                           看看当前的配置和自启状态

            参数：
              --server <地址>     服务器地址，例如 wss://kidplan.example.com
                                  （写 https://… 会自动换成 wss://，不带协议默认按 wss）
              --token <令牌>      节点令牌，在网页「模型设置 → 本机中继」里生成
              --timeout <秒>      单次转发超时，默认 300（本地大模型冷启动可能很慢）
              --allow-public      允许转发到公网地址（默认只放行本机和内网）
              --help / --version

            说明：
              中继默认只允许访问本机和内网地址。它相当于「服务器借你的电脑去访问网络」，
              把门关小一点更安心；真要连公网模型服务，用「服务端直连」更合适。

            配置存在 exe 旁边的 relay.json（写不进去就放到 %APPDATA%\KidPlan.Relay\）。
            """);
    }

    // ------------------------------------------------------------------
    // 日志
    // ------------------------------------------------------------------

    /// <summary>
    /// 极简日志：控制台 + 可选文件。
    /// 不用 ILogger 是因为那要引 Microsoft.Extensions.Logging 一整套，
    /// 而这里只需要「有时间戳、能写字、别把磁盘写满」。
    /// </summary>
    private sealed class Logger
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private readonly string? _path;
        private readonly object _gate = new();

        public Logger(string? path)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public void Info(string message) => Write("INFO", message);

        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

            Console.WriteLine(line);

            if (_path is null)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 超过 2MB 就滚一次，只留一个 .old。
                    // 挂在家里常驻的东西，日志无限长总有一天会把 C 盘吃掉。
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Move(_path, _path + ".old", overwrite: true);
                    }

                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch
                {
                    // 日志写不进去不该影响转发。
                }
            }
        }
    }
}
