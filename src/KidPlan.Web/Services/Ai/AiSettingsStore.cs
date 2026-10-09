using System.Text.Json;
using KidPlan.Web.Data;
using KidPlan.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace KidPlan.Web.Services.Ai;

/// <summary>
/// 全局 AI 配置文件的位置。单独包一个 record 是为了让单元测试能指到临时目录，
/// 不用去伪造一个 <c>IWebHostEnvironment</c>。
/// </summary>
public sealed record AiConfigFile(string Path);

/// <summary>一次全局配置读取的结果说明，界面用它显示「配置文件在哪、上次读成功没有」。</summary>
public sealed record AiGlobalLoadInfo(string Path, bool FileExists, bool Writable, DateTimeOffset? LastLoadedAt, string? LastError);

public interface IAiSettingsStore
{
    /// <summary>全局配置（<c>config/ai.json</c> 的内容）。不区分账号。</summary>
    AiSettingsDocument GetGlobal();

    AiGlobalLoadInfo GlobalInfo { get; }

    /// <summary>某个账号实际生效的配置：全局 + 该账号的覆盖。</summary>
    Task<AiSettingsDocument> GetEffectiveAsync(string userId, CancellationToken ct = default);

    /// <summary>写回全局配置文件。写盘之后下一次读取立即生效，不需要重启。</summary>
    Task SaveGlobalAsync(AiSettingsDocument document, CancellationToken ct = default);

    /// <summary>手动重新从磁盘读一次（配置文件被外部改了、但没触发监听时的兜底）。</summary>
    AiGlobalLoadInfo ReloadGlobal();

    Task<AiSettingsOverride?> GetOverrideAsync(string userId, CancellationToken ct = default);

    Task SaveOverrideAsync(string userId, AiSettingsOverride? overrides, CancellationToken ct = default);

    /// <summary>
    /// 把配置里的 API Key 换成掩码（全局配置给非本人看的时候用）。
    /// 只留后 4 位，够认出是哪把钥匙，又不会把它漏出去。
    /// </summary>
    static string Mask(string? key) => string.IsNullOrEmpty(key)
        ? string.Empty
        : key.Length <= 4 ? "••••" : $"••••••••{key[^4..]}";

    /// <summary>是不是掩码（保存时要把原来的钥匙填回去，不能把掩码写成真钥匙）。</summary>
    static bool IsMasked(string? key) => !string.IsNullOrEmpty(key) && key.StartsWith('•');
}

/// <summary>
/// 全局 AI 配置的持有者。
///
/// 为什么不用 <c>AddJsonFile(path, reloadOnChange: true)</c>：
/// 配置系统在文件变更回调里解析失败会直接抛异常，那个异常跑在线程池线程上，
/// 没人接得住 —— 也就是说**在配置文件里打错一个逗号就能把整个应用搞崩**。
/// 所以这里自己拿 <see cref="FileSystemWatcher"/>，解析失败只记日志、
/// 继续用上一份好配置，并且把错误显示在设置页上。
/// </summary>
public sealed class AiSettingsStore : IAiSettingsStore, IDisposable
{
    /// <summary>
    /// 用 <see cref="IServiceScopeFactory"/> 而不是直接注入 <see cref="IDbContextFactory{TContext}"/>：
    /// 本类必须注册成单例（要长期持有文件监听器），而上下文工厂是 Scoped 的，
    /// 单例直接注入 Scoped 服务会在解析时抛异常。需要读库时自己开一个作用域。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiSettingsStore> _logger;
    private readonly object _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounce;

    private AiSettingsDocument _global = CreateDefault();
    private string? _lastError;
    private DateTimeOffset? _lastLoadedAt;
    private bool _disposed;

    public AiSettingsStore(
        AiConfigFile file,
        IServiceScopeFactory scopeFactory,
        ILogger<AiSettingsStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        Path = file.Path;

        // 目录可能还不存在（本地开发 / 首次部署），先建出来，
        // 否则 FileSystemWatcher 构造就会抛，后面的自动生效也就没了。
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "创建 AI 配置目录 {Directory} 失败。", directory);
            }
        }

        ReloadGlobal();

        try
        {
            if (Directory.Exists(directory) && !string.IsNullOrEmpty(System.IO.Path.GetFileName(Path)))
            {
                _watcher = new FileSystemWatcher(directory!, System.IO.Path.GetFileName(Path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            // 监听失败不影响功能：还有设置页的「重新从文件加载」按钮兜底。
            _logger.LogWarning(ex, "监听 AI 配置文件失败，将无法自动感知外部修改。");
        }

        _debounce = new Timer(_ => ReloadGlobal(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Path { get; }

    public AiGlobalLoadInfo GlobalInfo
    {
        get
        {
            lock (_gate)
            {
                return new AiGlobalLoadInfo(Path, File.Exists(Path), IsWritable(), _lastLoadedAt, _lastError);
            }
        }
    }

    public AiSettingsDocument GetGlobal()
    {
        lock (_gate)
        {
            return Clone(_global);
        }
    }

    public AiGlobalLoadInfo ReloadGlobal()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    // 首次运行：落一份带注释说明的空模板，家长知道该去哪儿改。
                    WriteFile(_global);
                    _lastError = null;
                    _lastLoadedAt = DateTimeOffset.Now;
                    return GlobalInfoUnsafe();
                }

                var json = ReadFileWithRetry();
                var parsed = JsonSerializer.Deserialize<AiSettingsDocument>(json, AiJson.Options);

                if (parsed is not null)
                {
                    parsed.Providers ??= [];
                    parsed.Recommendation ??= new AiRecommendationOptions();
                    Normalize(parsed);
                    _global = parsed;
                }

                _lastError = null;
                _lastLoadedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                // 关键：解析失败**不**改变当前生效的配置，只记下来给界面看。
                _lastError = ex.Message;
                _lastLoadedAt = DateTimeOffset.Now;
                _logger.LogError(ex, "读取 AI 配置文件 {Path} 失败，继续沿用上一份可用配置。", Path);
            }

            return GlobalInfoUnsafe();
        }
    }

    public async Task<AiSettingsDocument> GetEffectiveAsync(string userId, CancellationToken ct = default)
    {
        var global = GetGlobal();
        var overrides = await GetOverrideAsync(userId, ct);

        if (overrides is null)
        {
            return global;
        }

        var removed = new HashSet<string>(overrides.RemovedProviderIds ?? [], StringComparer.OrdinalIgnoreCase);

        var providers = global.Providers
            .Where(p => !removed.Contains(p.Id))
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var provider in overrides.Providers ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.Id))
            {
                continue;
            }

            // 整条替换而不是逐字段合并：覆盖页面上看到什么就是什么，
            // 不会出现「我明明把 BaseUrl 改了，怎么还是全局那个」这种难查的问题。
            providers[provider.Id] = provider;
        }

        return new AiSettingsDocument
        {
            Version = global.Version,
            DefaultProviderId = string.IsNullOrWhiteSpace(overrides.DefaultProviderId)
                ? global.DefaultProviderId
                : overrides.DefaultProviderId,
            DefaultModel = string.IsNullOrWhiteSpace(overrides.DefaultModel)
                ? global.DefaultModel
                : overrides.DefaultModel,
            // 推荐参数刻意按整块替换：int/double 没法区分「没填」和「填了默认值」，
            // 逐字段合并会变成「想改超时，温度也被顺手重置」。界面会预填全局值，观感是一样的。
            Recommendation = overrides.Recommendation ?? global.Recommendation,
            Providers = providers.Values
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public async Task SaveGlobalAsync(AiSettingsDocument document, CancellationToken ct = default)
    {
        document.Providers ??= [];
        document.Recommendation ??= new AiRecommendationOptions();
        Normalize(document);

        lock (_gate)
        {
            // 保存时前端回传的可能是掩码，不能被真的写进文件。
            var previous = _global.Providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var provider in document.Providers)
            {
                if (IAiSettingsStore.IsMasked(provider.ApiKey)
                    && previous.TryGetValue(provider.Id, out var old))
                {
                    provider.ApiKey = old.ApiKey;
                }
            }

            WriteFile(document);
            _global = Clone(document);
            _lastError = null;
            _lastLoadedAt = DateTimeOffset.Now;
        }

        await Task.CompletedTask;
    }

    public async Task<AiSettingsOverride?> GetOverrideAsync(string userId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        var row = await db.AiUserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerId == userId, ct);

        if (row is null || string.IsNullOrWhiteSpace(row.OverrideJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiSettingsOverride>(row.OverrideJson, AiJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "账号 {UserId} 的 AI 覆盖配置解析失败，按「没有覆盖」处理。", userId);
            return null;
        }
    }

    public async Task SaveOverrideAsync(string userId, AiSettingsOverride? overrides, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync(ct);

        var row = await db.AiUserSettings.FirstOrDefaultAsync(s => s.OwnerId == userId, ct);

        var json = overrides is null ? string.Empty : JsonSerializer.Serialize(overrides, AiJson.Options);

        if (row is null)
        {
            db.AiUserSettings.Add(new AiUserSettings
            {
                OwnerId = userId,
                OverrideJson = json,
                UpdatedAt = DateTimeOffset.Now,
            });
        }
        else
        {
            row.OverrideJson = json;
            row.UpdatedAt = DateTimeOffset.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
        // 编辑器保存一次常常触发好几个事件，去抖一下再读，省得读到写了一半的文件。
        => _debounce?.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);

    private string ReadFileWithRetry()
    {
        // 编辑器/宝塔文件管理器写入的瞬间文件可能被独占，重试几次就好。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(120);
            }
        }
    }

    private void WriteFile(AiSettingsDocument document)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(document, AiJson.Options);

        // 先写临时文件再替换：避免监听线程读到半个 JSON 而误报错误。
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, Path, overwrite: true);
    }

    private bool IsWritable()
    {
        try
        {
            if (File.Exists(Path))
            {
                using var _ = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return true;
            }

            var directory = System.IO.Path.GetDirectoryName(Path);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
        }
        catch
        {
            return false;
        }
    }

    private AiGlobalLoadInfo GlobalInfoUnsafe()
        => new(Path, File.Exists(Path), IsWritable(), _lastLoadedAt, _lastError);

    private static void Normalize(AiSettingsDocument document)
    {
        foreach (var provider in document.Providers)
        {
            provider.Id = provider.Id?.Trim() ?? string.Empty;
            provider.Name = provider.Name?.Trim() ?? string.Empty;
            provider.BaseUrl = provider.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
            provider.Models ??= [];
            provider.ExtraHeaders ??= [];

            // 全是空白等于没填。留着 "   " 会让「这个端点有没有配钥匙」的判断出错，
            // 还会把一个空白 Authorization 头真的发出去。
            provider.ApiKey = string.IsNullOrWhiteSpace(provider.ApiKey)
                ? null
                : provider.ApiKey.Trim();

            provider.Models = provider.Models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        document.Providers = document.Providers
            .Where(p => p.Id.Length > 0)
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // 默认端点指到一个不存在 / 被停用的 Id 上是最常见的配置错误，
        // 这里直接修好，省得运行时才报「找不到默认模型」。
        var enabled = document.Providers.Where(p => p.Enabled).ToList();
        if (document.DefaultProviderId is null
            || enabled.All(p => !string.Equals(p.Id, document.DefaultProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            document.DefaultProviderId = enabled.FirstOrDefault()?.Id;
        }
    }

    private static AiSettingsDocument CreateDefault() => new()
    {
        Version = 1,
        DefaultProviderId = null,
        DefaultModel = null,
        Recommendation = new AiRecommendationOptions(),
        Providers = [],
    };

    private static AiSettingsDocument Clone(AiSettingsDocument source)
        => JsonSerializer.Deserialize<AiSettingsDocument>(
            JsonSerializer.Serialize(source, AiJson.Options), AiJson.Options) ?? CreateDefault();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }

        _debounce?.Dispose();
    }
}
