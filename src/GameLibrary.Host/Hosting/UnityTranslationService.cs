using System.Security.Cryptography;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Host.Launching;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

public sealed record UnityTranslationState
{
    public required string GameId { get; init; }
    public required string Title { get; init; }
    public required string AttemptId { get; init; }
    public required string DataEpoch { get; init; }
    public string State { get; init; } = "queued";
    public string Provider { get; init; } = "deepseek";
    public string? Reason { get; init; }
    public string? JobId { get; init; }
    public string? ProfileId { get; init; }
    public string? ExecutablePath { get; init; }
    public string? BoundTagId { get; init; }
    public string? BackupId { get; init; }
    public string? InstallRoot { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>One serial queue uses existing jobs and session leases; state survives restart, credentials do not enter job data.</summary>
public sealed class UnityTranslationService
{
    private readonly HostRuntimeState _host;
    private readonly UnityTranslationPayload _payload;
    private readonly string? _vaultBase;
    private string? _startedScope;
    // ponytail: one process-wide FIFO, split by physical game only if installation throughput matters.
    private static readonly object QueueLock = new();
    private static Task _tail = Task.CompletedTask;

    internal UnityTranslationService(HostRuntimeState host, UnityTranslationPayload? payload = null, string? vaultBase = null)
    { _host = host; _payload = payload ?? new(); _vaultBase = vaultBase; }

    private UnityTranslationVault Vault(SqliteLibraryStore store) => new(Path.GetFullPath(_host.DataDirectory).ToUpperInvariant(), _vaultBase);
    private static UnityTranslationSettings Settings(SqliteLibraryStore store) =>
        UnityTranslationPersistence.Read<UnityTranslationSettings>(store, UnityTranslationPersistence.SettingsKey) ?? new();
    private static UnityTranslationSettings BindTag(SqliteLibraryStore store, UnityTranslationSettings settings)
    {
        if (settings.BoundTagId is not null || store.TryGetTagByName("user", "未翻译") is not { } tag) return settings;
        settings = settings with { BoundTagId = tag.TagId };
        UnityTranslationPersistence.Write(store, UnityTranslationPersistence.SettingsKey, settings);
        return settings;
    }
    private static UnityTranslationState? ReadState(SqliteLibraryStore store, string id) =>
        UnityTranslationPersistence.Read<UnityTranslationState>(store, UnityTranslationPersistence.StatePrefix + id) is { } state
            && state.DataEpoch == store.Info.DataEpoch ? state : null;
    internal static bool IsConfiguring(SqliteLibraryStore store, string gameId) =>
        ReadState(store, gameId)?.State is "queued" or "inspecting" or "installing";

    private static PersistedTag? BoundTag(SqliteLibraryStore store, UnityTranslationSettings settings) =>
        settings.BoundTagId is not null ? store.TryGetTag(settings.BoundTagId) : store.TryGetTagByName("user", "未翻译");
    private static bool HasTag(SqliteLibraryStore store, GameCard game, PersistedTag? tag) => tag is { Kind: "user" }
        && store.ListGameTags(game.GameId).Any(item => item.Kind == "user" && item.Name == tag.Name);
    private static bool IsActiveUnityGame(GameCard? game) => game is { Membership: "active" }
        && string.Equals(game.Engine, "unity", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        var store = _host.Library.Store;
        if (store is null) return;
        var scope = store.Info.LibraryInstanceId + ":" + store.Info.DataEpoch;
        if (_startedScope == scope) return;
        _startedScope = scope;
        BindTag(store, Settings(store));
        TryImportExisting(store);
        foreach (var state in UnityTranslationPersistence.States(store).Where(s => s.State is "queued" or "inspecting" or "installing"))
        {
            try
            {
                if (state.BackupId is not null && state.ExecutablePath is not null)
                {
                    if (UnityTranslationInspection.IsRunning(state.ExecutablePath)) throw new InvalidDataException("游戏正在运行");
                    new UnityTranslationTransaction(Vault(store), state.BackupId, state.InstallRoot ?? Path.GetDirectoryName(state.ExecutablePath)!).Restore();
                }
                Save(store, state with { State = "failed", Reason = "上次配置被中断，已回滚；可从详情重试" });
            }
            catch { Save(store, state with { State = "blocked", Reason = "上次配置被中断，原文件无法安全恢复；请先检查或恢复" }); }
        }
        foreach (var game in store.ListGames().Where(IsActiveUnityGame))
        {
            RequestForTaggedGame(game.GameId);
        }
    }

    /// <summary>TagsHandler calls only after a real assignment. Does not throw into a successful tag write.</summary>
    public void RequestForTaggedGame(string gameId)
    {
        try
        {
            var store = _host.Library.Store;
            if (store is null || _host.MaintenanceMode) return;
            var settings = BindTag(store, Settings(store));
            var game = store.TryGetGame(gameId);
            if (!settings.Enabled || game is null || !IsActiveUnityGame(game)) return;
            var existing = ReadState(store, gameId);
            var tagged = HasTag(store, game, BoundTag(store, settings));
            var ownedConfiguration = existing is { BackupId: not null, State: "configured" or "confirmed" };
            if (!tagged && !ownedConfiguration) return;
            if (existing?.State is "queued" or "inspecting" or "installing" or "declined") return;
            var profile = _host.Launches.GetDefaultProfile(gameId);
            var layout = profile is { ToolId: null } && _host.Roots.Contains(profile.ExecutablePath)
                ? UnityTranslationInspection.Inspect(profile.ExecutablePath) : null;
            // v1.7.6 wrote the Rei config path for BepInEx. Repair only our tracked installations.
            var repair = ownedConfiguration && layout is { Reason: null, Loader: "bepinex" }
                && File.Exists(Path.Combine(layout.Root, "AutoTranslator", "Config.ini"))
                && !UnityTranslationInspection.IsConfiguredChineseTranslator(layout);
            if (!tagged && !repair) return;
            if (repair && UnityTranslationInspection.IsRunning(profile!.ExecutablePath)) return;
            if (layout is not null && UnityTranslationInspection.IsConfiguredChineseTranslator(layout))
            {
                if (existing?.State == "configured") return; // Keep first-install confirmation until the user reports actual success.
                CompleteExisting(store, NewState(store, game, settings));
                return;
            }
            if (existing?.State == "configured" && !repair) return;
            if (!settings.Configured || !Vault(store).HasKey(settings.CredentialId))
            {
                Save(store, NewState(store, game, settings) with { State = "needs_settings", Reason = "请先配置翻译服务商" });
                return;
            }
            Queue(store, [gameId], allowUntaggedRepair: repair && !tagged);
        }
        catch { /* Tag mutation succeeded; automation is retried through the explicit detail operation. */ }
    }

    public void QueueTaggedGame(string gameId) => RequestForTaggedGame(gameId);

    private void CompleteExisting(SqliteLibraryStore store, UnityTranslationState state)
    {
        Save(store, state with { State = "confirmed", Reason = "已检测到已启用的中文翻译插件" });
        if (state.BoundTagId is not null && store.TryGetTag(state.BoundTagId) is { Kind: "user" }
            && store.UnassignTag(state.GameId, state.BoundTagId) == "user")
            _host.Events.Publish("game.updated", "game:" + state.GameId,
                new { gameId = state.GameId, fields = new[] { "tags" } }, DateTime.UtcNow);
    }

    public Envelope<object> Handle(IpcRequest request)
    {
        var store = _host.Library.Store;
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        try
        {
            return request.OperationId switch
            {
                "unity_translation.settings.get" => Ok(request, SettingsDto(store)),
                "unity_translation.settings.set" => SetSettings(request, store),
                "unity_translation.settings.import" => ImportSettings(request, store),
                "unity_translation.status" => Status(request, store, pendingOnly: false),
                "unity_translation.pending" => Status(request, store, pendingOnly: true),
                "unity_translation.configure" => Configure(request, store),
                "unity_translation.confirm" => Confirm(request, store),
                "unity_translation.restore" => Restore(request, store),
                _ => IpcRequests.InvalidArgument(request, "不支持的 Unity 翻译操作"),
            };
        }
        catch (InvalidDataException ex) { return IpcRequests.InvalidArgument(request, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
            or JsonException or InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        { return IpcRequests.Failure(request, ErrorCodes.InvalidArgument, "无法安全完成 Unity 翻译操作；请检查路径、权限和当前用户安全存储"); }
    }

    private object SettingsDto(SqliteLibraryStore store)
    {
        var settings = Settings(store);
        return new
        {
            provider = settings.Provider == "custom" ? "openai" : settings.Provider,
            endpoint = settings.BaseUrl,
            model = settings.Model,
            hasKey = Vault(store).HasKey(settings.CredentialId),
            boundTagId = BoundTag(store, settings)?.TagId,
            enabled = settings.Enabled,
            configured = settings.Configured,
            sourceLanguage = "auto",
            targetLanguage = "zh",
            sources = !Vault(store).HasKey(settings.CredentialId) ? DiscoverSources(store).Select(SourceDto).ToArray() : []
        };
    }

    private Envelope<object> SetSettings(IpcRequest request, SqliteLibraryStore store)
    {
        var previous = BindTag(store, Settings(store));
        var provider = String(request, "provider") ?? (previous.Provider == "custom" ? "openai" : previous.Provider);
        if (provider is not ("deepseek" or "openai")) throw new InvalidDataException("provider 必须为 deepseek 或 openai");
        var settings = (previous with
        {
            Provider = provider == "openai" ? "custom" : "deepseek",
            BaseUrl = String(request, "endpoint") ?? previous.BaseUrl,
            Model = String(request, "model") ?? previous.Model,
            BoundTagId = String(request, "boundTagId") ?? previous.BoundTagId,
            Enabled = Boolean(request, "enabled") ?? previous.Enabled,
            Configured = true,
        }).Validate();
        if (settings.BoundTagId is not null && store.TryGetTag(settings.BoundTagId) is not { Kind: "user" })
            throw new InvalidDataException("boundTagId 必须为用户标签");
        var vault = Vault(store);
        var key = String(request, "apiKey");
        if (key is not null && (settings.Model.Contains(key, StringComparison.Ordinal) || settings.BaseUrl.Contains(key, StringComparison.Ordinal)))
            throw new InvalidDataException("服务商元数据不得包含密钥");
        if (key is null && (previous.Provider != settings.Provider
            || new Uri(previous.BaseUrl).GetLeftPart(UriPartial.Authority) != new Uri(settings.BaseUrl).GetLeftPart(UriPartial.Authority)))
            throw new InvalidDataException("更换服务商或地址必须重新提供密钥");
        if (key is null && !vault.HasKey(settings.CredentialId)) throw new InvalidDataException("首次配置需要 apiKey，或从本机现有配置导入");
        var retainedKey = key ?? vault.ReadKey(settings.CredentialId);
        if (settings.Model.Contains(retainedKey, StringComparison.Ordinal) || settings.BaseUrl.Contains(retainedKey, StringComparison.Ordinal))
            throw new InvalidDataException("服务商元数据不得包含密钥");
        if (key is not null) settings = settings with { CredentialId = vault.WriteKey(key) };
        UnityTranslationPersistence.Write(store, UnityTranslationPersistence.SettingsKey, settings);
        _host.Events.Publish("unity_translation.settings_changed", "unity_translation:settings", new { configured = true }, DateTime.UtcNow);
        QueueExisting(store);
        return Ok(request, SettingsDto(store));
    }

    private Envelope<object> ImportSettings(IpcRequest request, SqliteLibraryStore store)
    {
        // This is the sole import entry. Agents/tests never read a user's Config.ini.
        var path = String(request, "configPath");
        if (path is null)
        {
            var sources = DiscoverSources(store);
            var unique = UniqueSources(sources);
            if (unique.Count != 1) return Ok(request, new { imported = false, sources = sources.Select(SourceDto).ToArray() });
            path = unique[0].Path;
        }
        if (!Path.IsPathFullyQualified(path) || !(Path.GetFileName(path).Equals("Config.ini", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("AutoTranslatorConfig.ini", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("configPath 必须为本机 Config.ini 的绝对路径");
        if (!File.Exists(path)) throw new InvalidDataException("本机翻译配置不存在");
        var ini = UnityTranslationIni.Read(path);
        var key = ini.Get("DeepSeek", "ApiKey") ?? throw new InvalidDataException("现有配置缺少 DeepSeek ApiKey");
        UnityTranslationSettings.ValidateKey(key);
        var endpoint = ini.Get("DeepSeek", "Endpoint") ?? "https://api.deepseek.com/chat/completions";
        var settings = (BindTag(store, Settings(store)) with
        {
            Provider = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                && uri.Host == "api.deepseek.com" ? "deepseek" : "custom",
            BaseUrl = endpoint,
            Model = ini.Get("DeepSeek", "Model") ?? "deepseek-flash",
            Configured = true
        }).Validate();
        if (settings.BaseUrl.Contains(key, StringComparison.Ordinal) || settings.Model.Contains(key, StringComparison.Ordinal))
            throw new InvalidDataException("导入元数据包含密钥，拒绝导入");
        settings = settings with { CredentialId = Vault(store).WriteKey(key) };
        UnityTranslationPersistence.Write(store, UnityTranslationPersistence.SettingsKey, settings);
        _host.Events.Publish("unity_translation.settings_changed", "unity_translation:settings", new { configured = true }, DateTime.UtcNow);
        QueueExisting(store);
        return Ok(request, SettingsDto(store));
    }

    private void QueueExisting(SqliteLibraryStore store)
    {
        var settings = Settings(store);
        if (!settings.Enabled) return;
        var tag = BoundTag(store, settings);
        var ids = store.ListGames().Where(game => IsActiveUnityGame(game) && HasTag(store, game, tag)
            && (ReadState(store, game.GameId)?.State is null or "needs_settings")).Select(game => game.GameId).ToArray();
        if (ids.Length > 0) Queue(store, ids);
    }

    // Keys are intentionally private fields, never part of a serializable record/DTO or exception.
    private sealed class ImportSource
    {
        public required string Path { get; init; }
        public required string GameId { get; init; }
        public required string Title { get; init; }
        public required UnityTranslationSettings Settings { get; init; }
        public required string Key;
    }
    private static object SourceDto(ImportSource source) => new
    {
        configPath = source.Path,
        gameId = source.GameId,
        title = source.Title,
        provider = source.Settings.Provider == "custom" ? "openai" : "deepseek",
        endpoint = source.Settings.BaseUrl,
        model = source.Settings.Model,
        hasKey = true
    };
    private static IReadOnlyList<ImportSource> UniqueSources(IReadOnlyList<ImportSource> sources) => sources
        .GroupBy(source => (source.Settings.Provider, source.Settings.BaseUrl, source.Settings.Model, source.Key))
        .Select(group => group.First()).ToArray();
    private IReadOnlyList<ImportSource> DiscoverSources(SqliteLibraryStore store)
    {
        var result = new List<ImportSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in store.ListGames().Where(IsActiveUnityGame))
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in _host.Launches.ListProfiles(game.GameId))
                if (Path.GetDirectoryName(profile.ExecutablePath) is { } parent) AddPaths(parent);
            if (Directory.Exists(game.RootPath))
            {
                AddPaths(game.RootPath);
                if (game.RootPath.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(game.RootPath) is { } parent)
                    AddPaths(parent);
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(game.RootPath).Take(200))
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                        if (Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Any())
                            AddPaths(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            void AddPaths(string directory)
            {
                paths.Add(Path.Combine(directory, "BepInEx", "config", "AutoTranslatorConfig.ini"));
                paths.Add(Path.Combine(directory, "AutoTranslator", "Config.ini"));
            }
            foreach (var path in paths.Where(path => _host.Roots.Contains(path) && File.Exists(path) && seen.Add(path)))
            {
                try
                {
                    UnityTranslationInspection.RejectReparse(Path.GetPathRoot(path)!, path);
                    var ini = UnityTranslationIni.Read(path);
                    var key = ini.Get("DeepSeek", "ApiKey");
                    if (key is null) continue;
                    UnityTranslationSettings.ValidateKey(key);
                    var endpoint = ini.Get("DeepSeek", "Endpoint") ?? "https://api.deepseek.com/chat/completions";
                    var settings = (Settings(store) with
                    {
                        BaseUrl = endpoint,
                        Model = ini.Get("DeepSeek", "Model") ?? "deepseek-flash",
                        Provider = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host == "api.deepseek.com" ? "deepseek" : "custom",
                        Configured = true
                    }).Validate();
                    if (settings.BaseUrl.Contains(key, StringComparison.Ordinal) || settings.Model.Contains(key, StringComparison.Ordinal)) continue;
                    result.Add(new() { Path = path, GameId = game.GameId, Title = game.Title, Key = key, Settings = settings });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return result;
    }
    private void TryImportExisting(SqliteLibraryStore store)
    {
        if (Vault(store).HasKey(Settings(store).CredentialId)) return;
        try
        {
            var sources = UniqueSources(DiscoverSources(store));
            if (sources.Count != 1) return;
            var imported = sources[0].Settings with { CredentialId = Vault(store).WriteKey(sources[0].Key) };
            UnityTranslationPersistence.Write(store, UnityTranslationPersistence.SettingsKey, imported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
    }

    private Envelope<object> Status(IpcRequest request, SqliteLibraryStore store, bool pendingOnly)
    {
        var gameId = String(request, "gameId");
        var settings = Settings(store);
        var states = UnityTranslationPersistence.States(store).Where(s => gameId is null || s.GameId == gameId)
            .Where(s => !pendingOnly || s.State is "needs_settings" or "configured")
            .Where(s => store.TryGetGame(s.GameId)?.Membership == "active")
            .OrderBy(s => s.UpdatedUtc).Select(StateDto).ToArray();
        return Ok(request, new { items = states, needsSettings = !settings.Configured || !Vault(store).HasKey(settings.CredentialId) });
    }

    private Envelope<object> Configure(IpcRequest request, SqliteLibraryStore store)
    {
        if (!IpcRequests.TryGetStringListParameter(request, "gameIds", out var ids) || ids.Count is < 1 or > 1000
            || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200))
            return IpcRequests.InvalidArgument(request, "gameIds 必须为 1–1000 个游戏 ID");
        if (ids.Any(id => store.TryGetGame(id) is null)) return IpcRequests.NotFound(request, "游戏不存在");
        if (ids.Any(id => ReadState(store, id)?.State is "queued" or "inspecting" or "installing"))
            return IpcRequests.InvalidArgument(request, "游戏已有配置作业，请等待或取消后重试");
        var jobId = Queue(store, ids.Distinct(StringComparer.Ordinal).ToArray());
        return new()
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, gameIds = ids.Distinct().ToArray() }
        };
    }

    private static (Task Previous, TaskCompletionSource Completion) Reserve()
    {
        lock (QueueLock)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = _tail;
            _tail = completion.Task;
            return (previous, completion);
        }
    }

    private string Queue(SqliteLibraryStore store, IReadOnlyList<string> ids, bool allowUntaggedRepair = false)
    {
        var settings = Settings(store);
        var states = ids.Select(id => NewState(store, store.TryGetGame(id)!, settings)).ToArray();
        var reserved = Reserve();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string jobId;
        try
        {
            jobId = _host.Jobs.Create("unityTranslation", async context =>
            {
                try
                {
                    await reserved.Previous; // Do not cancel ahead of the predecessor: later jobs must remain serial.
                    await ready.Task;
                    for (var i = 0; i < states.Length; i++)
                    {
                        context.Token.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(store, _host.Library.Store)) return JobOutcome.Cancelled();
                        context.ReportProgress(new { completed = i, total = states.Length, gameId = states[i].GameId, state = "inspecting" });
                        await ConfigureOne(store, states[i], context, allowUntaggedRepair);
                    }
                    context.ReportProgress(new { completed = states.Length, total = states.Length, items = states.Select(s => StateDto(ReadState(store, s.GameId) ?? s)).ToArray() });
                    return JobOutcome.Succeeded();
                }
                finally
                {
                    try
                    {
                        foreach (var item in states)
                            if (ReadState(store, item.GameId) is { } current && current.AttemptId == item.AttemptId
                                && current.State is "queued" or "inspecting" or "installing")
                                Save(store, current with { State = "failed", Reason = "配置作业已结束或取消；可从详情重试" });
                    }
                    finally { reserved.Completion.TrySetResult(); }
                }
            }, new { completed = 0, total = states.Length, state = "queued" });
        }
        catch
        {
            _ = reserved.Previous.ContinueWith(_ => reserved.Completion.TrySetResult(), TaskScheduler.Default);
            throw;
        }
        try
        {
            for (var i = 0; i < states.Length; i++)
            {
                states[i] = states[i] with { JobId = jobId };
                Save(store, states[i]);
            }
        }
        finally { ready.TrySetResult(); }
        return jobId;
    }

    private UnityTranslationState NewState(SqliteLibraryStore store, GameCard game, UnityTranslationSettings settings)
    {
        var profile = _host.Launches.GetDefaultProfile(game.GameId);
        var prior = ReadState(store, game.GameId);
        return new()
        {
            GameId = game.GameId,
            Title = game.Title,
            AttemptId = Guid.NewGuid().ToString("N"),
            DataEpoch = store.Info.DataEpoch,
            Provider = settings.Provider == "custom" ? "openai" : settings.Provider,
            BoundTagId = BoundTag(store, settings)?.TagId,
            ProfileId = profile?.ProfileId,
            ExecutablePath = profile?.ExecutablePath,
            BackupId = prior?.BackupId,
            InstallRoot = prior?.InstallRoot
        };
    }

    private async Task ConfigureOne(SqliteLibraryStore store, UnityTranslationState state, JobContext context, bool allowUntaggedRepair)
    {
        UnityTranslationTransaction? transaction = null;
        try
        {
            if (ReadState(store, state.GameId)?.AttemptId != state.AttemptId) return;
            var game = store.TryGetGame(state.GameId);
            var settings = Settings(store);
            state = state with { Provider = settings.Provider == "custom" ? "openai" : settings.Provider };
            if (game is null || !IsActiveUnityGame(game)) { Block("仅支持当前库中的 Unity 游戏"); return; }
            if (!settings.Enabled || BoundTag(store, settings)?.TagId != state.BoundTagId)
            { Block("自动翻译已禁用或标签绑定已改变，保留现有配置"); return; }
            if (!allowUntaggedRepair && !HasTag(store, game, state.BoundTagId is null ? null : store.TryGetTag(state.BoundTagId)))
            { Block("游戏未绑定未翻译用户标签"); return; }
            var profile = state.ProfileId is null ? null : _host.Launches.GetProfile(state.ProfileId);
            if (profile is null || profile.ExecutablePath != state.ExecutablePath || profile.GameId != state.GameId)
            { Block("未找到稳定的默认启动配置；请先选择实际游戏 EXE"); return; }
            if (profile.ToolId is not null) { Block("启动配置已有外部翻译工具，保留现有配置"); return; }
            if (!_host.Roots.Contains(profile.ExecutablePath)) { Block("所选 EXE 不在已注册库根内"); return; }
            if (UnityTranslationInspection.IsRunning(profile.ExecutablePath)) { Block("游戏正在运行；关闭游戏后重试"); return; }
            state = state with { State = "inspecting" };
            Save(store, state);
            var layout = UnityTranslationInspection.Inspect(profile.ExecutablePath);
            if (layout.Reason is not null) { Block(layout.Reason); return; }
            if (state.BackupId is null && UnityTranslationInspection.IsConfiguredChineseTranslator(layout))
            { CompleteExisting(store, state); return; }
            if (!settings.Configured || !Vault(store).HasKey(settings.CredentialId))
            { Save(store, state with { State = "needs_settings", Reason = "请先配置翻译服务商" }); return; }
            var key = Vault(store).ReadKey(settings.CredentialId); // Capture with metadata before awaits; provider changes must not mix credentials.
            var config = UnityTranslationIni.Read(layout.Config);
            var originalConfigHash = FileHash(layout.Config);
            var originalBootstrapHash = FileHash(layout.Bootstrap);
            var targets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var expectedFiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (layout.Runtime == "il2cpp" && layout.Loader is "none" or "bepinex-empty")
            {
                var files = await _payload.Il2CppAsync(layout.Architecture!, includeLoader: layout.Loader == "none", context.Token);
                foreach (var pair in files)
                    if (!AddDependency(Path.Combine(Path.GetDirectoryName(profile.ExecutablePath)!, pair.Key), pair.Value)) return;
            }
            else if (layout.Loader == "none")
            {
                var runtime = await _payload.RuntimeAsync(context.Token);
                foreach (var pair in runtime)
                {
                    var path = Path.Combine(layout.Managed, pair.Key);
                    if (!AddDependency(path, pair.Value)) return;
                }
                targets.Add(layout.Bootstrap, await _payload.PatchAsync(layout, runtime, context.Token));
            }
            // Keep existing loader, core and endpoint versions; only install the endpoint if it is missing.
            var endpoint = Path.Combine(layout.Translators, "DeepSeekTranslate.dll");
            UnityTranslationInspection.RejectReparse(layout.Root, endpoint);
            expectedFiles[endpoint] = FileHash(endpoint);
            if (!File.Exists(endpoint)) targets.Add(endpoint, await _payload.EndpointAsync(context.Token));
            else if (!UnityTranslationInspection.IsAssembly(endpoint, "DeepSeekTranslate", "DeepSeekTranslateEndpoint"))
            { Block("现有 DeepSeekTranslate 无法验证，保留插件版本并等待人工检查"); return; }
            targets.Add(layout.Config, config.Configure(settings, key));
            foreach (var path in targets.Keys)
            {
                UnityTranslationInspection.RejectReparse(layout.Root, path);
                if (!_host.Roots.Contains(path)) { Block("翻译目标不在已注册库根内，拒绝修改"); return; }
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                { Block("目标文件为只读，保留原始文件"); return; }
            }
            context.Token.ThrowIfCancellationRequested();
            var currentGame = store.TryGetGame(state.GameId);
            var currentProfile = _host.Launches.GetDefaultProfile(state.GameId);
            if (!ReferenceEquals(store, _host.Library.Store) || ReadState(store, state.GameId)?.AttemptId != state.AttemptId) return;
            if (currentGame is null || !IsActiveUnityGame(currentGame)
                || !allowUntaggedRepair && !HasTag(store, currentGame, store.TryGetTag(state.BoundTagId!))
                || currentProfile != profile || Settings(store) != settings)
            { Block("游戏、标签或启动配置在准备期间已改变，取消安装"); return; }
            if (FileHash(layout.Config) != originalConfigHash || FileHash(layout.Bootstrap) != originalBootstrapHash)
            { Block("游戏文件在准备期间已改变，保留文件并取消安装"); return; }
            if (expectedFiles.Any(pair => FileHash(pair.Key) != pair.Value))
            { Block("运行依赖在准备期间已改变，保留文件并取消安装"); return; }
            if (UnityTranslationInspection.IsRunning(profile.ExecutablePath)) { Block("游戏已开始运行，取消配置"); return; }
            if (ReadState(store, state.GameId)?.AttemptId != state.AttemptId) return;
            state = state with { State = "installing", BackupId = state.BackupId ?? Guid.NewGuid().ToString("N"), InstallRoot = layout.Root };
            Save(store, state);
            transaction = new(Vault(store), state.BackupId!, layout.Root);
            foreach (var path in targets.Keys) transaction.Capture(path);
            foreach (var pair in targets) { context.Token.ThrowIfCancellationRequested(); transaction.Write(pair.Key, pair.Value); }
            await _payload.VerifyEndpointAsync(layout, context.Token);
            var expectedLoader = layout.Runtime == "il2cpp" ? "bepinex" : layout.Loader == "none" ? "rei" : layout.Loader;
            var installed = UnityTranslationInspection.Inspect(profile.ExecutablePath);
            if (installed.Reason is not null || installed.Loader != expectedLoader || installed.Runtime != layout.Runtime
                || !UnityTranslationInspection.IsConfiguredChineseTranslator(installed))
                throw new InvalidDataException("加载器离线验证失败");
            transaction.Commit();
            Save(store, state with { State = "configured", Reason = layout.Runtime == "il2cpp"
                ? "IL2CPP 插件已配置；首次启动可能需要联网准备组件，请耐心等待，并在游戏内确认翻译效果" : null });

            bool AddDependency(string path, byte[] bytes)
            {
                UnityTranslationInspection.RejectReparse(layout.Root, path);
                var before = FileHash(path);
                if (before is not null && before != Convert.ToHexString(SHA256.HashData(bytes)))
                { Block("运行依赖与未知现有模组文件重名，保留文件并等待人工检查"); return false; }
                expectedFiles.Add(path, before);
                targets.Add(path, bytes);
                return true;
            }
        }
        catch (Exception ex)
        {
            var reason = ex is InvalidDataException ? ex.Message : ex is UnauthorizedAccessException ? "游戏目录不可写，保留原始文件"
                : ex is HttpRequestException ? "翻译组件下载失败，请检查网络后重试；未调用翻译 API"
                : ex is OperationCanceledException ? "配置已取消，原始文件已回滚" : "翻译配置失败；未启动游戏或调用翻译 API";
            try { transaction?.Restore(); }
            catch { reason = "配置失败且检测到文件变更，无法安全回滚；请检查后恢复"; }
            if (ReadState(store, state.GameId)?.AttemptId == state.AttemptId)
                Save(store, state with { State = ex is UnauthorizedAccessException ? "blocked" : "failed", Reason = reason });
            if (ex is OperationCanceledException) throw;
        }
        void Block(string reason) => Save(store, state with { State = "blocked", Reason = reason });
    }

    private Envelope<object> Confirm(IpcRequest request, SqliteLibraryStore store)
    {
        var id = String(request, "gameId") ?? throw new InvalidDataException("需要 gameId");
        var attempt = String(request, "attemptId") ?? throw new InvalidDataException("需要 attemptId");
        var success = Boolean(request, "success") ?? throw new InvalidDataException("success 必须为布尔值");
        var state = ReadState(store, id);
        if (state is null || state.AttemptId != attempt || state.State != "configured")
            return IpcRequests.Failure(request, ErrorCodes.RevisionConflict, "配置尝试已失效或已确认；请刷新状态");
        if (store.TryGetGame(id) is not { Membership: "active" }) return IpcRequests.NotFound(request, "游戏已不在当前库");
        if (success)
        {
            if (!_host.Launches.History(id).Any(launch => launch.ProfileId == state.ProfileId
                && launch.ExecutablePath == state.ExecutablePath && launch.CreatedUtc >= state.UpdatedUtc
                && launch.ProcessStartedUtc is { } started && started >= state.UpdatedUtc && launch.ProcessId > 0
                && launch.State is "processCreated" or "exited"))
                return IpcRequests.Failure(request, ErrorCodes.InvalidArgument, "配置完成后尚未实际启动此游戏，不能确认翻译成功");
            var settings = Settings(store);
            if (BoundTag(store, settings)?.TagId != state.BoundTagId)
                return IpcRequests.Failure(request, ErrorCodes.RevisionConflict, "未翻译标签绑定已变更，拒绝删除其他标签");
            if (state.BoundTagId is not null && store.TryGetTag(state.BoundTagId) is { Kind: "user" })
            {
                store.UnassignTag(id, state.BoundTagId);
                _host.Events.Publish("game.updated", "game:" + id, new { gameId = id, fields = new[] { "tags" } }, DateTime.UtcNow);
            }
        }
        state = state with { State = success ? "confirmed" : "declined", Reason = success ? null : "用户未确认翻译成功；保留配置和标签" };
        Save(store, state);
        return Ok(request, StateDto(state));
    }

    private Envelope<object> Restore(IpcRequest request, SqliteLibraryStore store)
    {
        var id = String(request, "gameId") ?? throw new InvalidDataException("需要 gameId");
        var state = ReadState(store, id) ?? throw new InvalidDataException("没有可恢复的翻译配置");
        if (state.State is "queued" or "inspecting" or "installing") throw new InvalidDataException("游戏配置正在进行，请先取消作业或等待结束");
        if (state.BackupId is null || state.ExecutablePath is null) throw new InvalidDataException("没有本机原始备份");
        if (UnityTranslationInspection.IsRunning(state.ExecutablePath)) throw new InvalidDataException("游戏正在运行，不能恢复插件");
        TaskCompletionSource completion;
        lock (QueueLock)
        {
            if (!_tail.IsCompleted) throw new InvalidDataException("全局配置队列尚未结束，请等待后恢复");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _tail = completion.Task;
        }
        try
        {
            Save(store, state with { State = "installing", Reason = "正在恢复翻译文件，请等待恢复完成" });
            new UnityTranslationTransaction(Vault(store), state.BackupId, state.InstallRoot ?? Path.GetDirectoryName(state.ExecutablePath)!).Restore();
            if (state.BoundTagId is not null && store.TryGetTag(state.BoundTagId) is { Kind: "user" })
                store.AssignTag(id, state.BoundTagId, DateTime.UtcNow);
            var restored = state with { State = "restored", BackupId = null, Reason = "已恢复安装前文件；保留未翻译标签" };
            Save(store, restored);
            return Ok(request, StateDto(restored));
        }
        catch
        {
            Save(store, state with { Reason = "恢复未完成，请检查原文件后重试" });
            throw;
        }
        finally { completion.TrySetResult(); }
    }

    private void Save(SqliteLibraryStore store, UnityTranslationState state)
    {
        state = state with { UpdatedUtc = DateTime.UtcNow };
        UnityTranslationPersistence.Write(store, UnityTranslationPersistence.StatePrefix + state.GameId, state);
        _host.Events.Publish(state.State == "configured" ? "unity_translation.configured" : "unity_translation.updated",
            "game:" + state.GameId, StateDto(state), DateTime.UtcNow);
    }
    private static object StateDto(UnityTranslationState state) => new
    {
        gameId = state.GameId,
        title = state.Title,
        attemptId = state.AttemptId,
        state = state.State,
        provider = state.Provider,
        reason = state.Reason,
        jobId = state.JobId,
        profileId = state.ProfileId
    };
    private static string? FileHash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    private static string? String(IpcRequest request, string name) => IpcRequests.TryGetStringParameter(request, name, out var value) ? value : null;
    private static bool? Boolean(IpcRequest request, string name) => IpcRequests.TryGetBoolParameter(request, name, out var value) ? value : null;
    private static Envelope<object> Ok(IpcRequest request, object data) => new()
    {
        RequestId = request.RequestId,
        Ok = true,
        Status = OperationStatus.Completed,
        Data = data
    };
}
