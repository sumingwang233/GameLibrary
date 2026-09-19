using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 唯一宿主持有的 SQLite 库存储（ADR-0004）：每连接启用外键/WAL/busy_timeout；
/// 打开即校验 schema 版本；损坏进入 RecoveryRequired，绝不静默重建空库。
/// v1 审查修复：宿主级单写队列——本类内部以对象锁串行化全部连接访问，
/// 允许多客户端并发请求与后台作业共用同一连接而不出现嵌套事务/竞态；
/// 事件持久化等直连组件必须经 <see cref="ReadExclusive{T}"/>/<see cref="WriteExclusive"/>。
/// </summary>
public sealed class SqliteLibraryStore : IAsyncDisposable
{
    public const string DatabaseFileName = "library.db";
    public const string BackupsFolderName = "backups";

    private const int BusyTimeoutMs = 5000;
    private static readonly int[] CorruptionCodes = [11, 26];

    private readonly SqliteConnection _connection;
    private readonly SqliteLibraryStoreOptions _options;

    /// <summary>单写锁：同一连接上的全部读写（含事务）经此串行（C# monitor 同线程可重入）。</summary>
    private readonly object _sync = new();

    private SqliteLibraryStore(SqliteConnection connection, SqliteLibraryStoreOptions options, LibraryDatabaseInfo info)
    {
        _connection = connection;
        _options = options;
        Info = info;
    }

    public LibraryDatabaseInfo Info { get; private set; }

    public string DatabasePath => _connection.DataSource;

    /// <summary>
    /// 共享连接。仅限 Host/Infrastructure 组合内部一次性初始化使用；
    /// 业务与事件持久化必须走本类的转发方法或互斥访问原语，禁止在锁外直接执行 SQL。
    /// </summary>
    public SqliteConnection DatabaseConnection => _connection;

    /// <summary>锁外读取快照（info/epoch 等不可变值）。</summary>
    public T ReadExclusive<T>(Func<SqliteConnection, LibraryDatabaseInfo, T> func)
    {
        lock (_sync)
        {
            return func(_connection, Info);
        }
    }

    /// <summary>锁外执行写入（事件持久化等直连组件唯一入口）。</summary>
    public void WriteExclusive(Action<SqliteConnection, LibraryDatabaseInfo> action)
    {
        lock (_sync)
        {
            action(_connection, Info);
        }
    }

    /// <summary>
    /// 维护切换（backups.restore / REC-02）：关闭当前连接 → 用暂存库文件替换当前 library.db
    /// （连带清除 WAL/SHM 残留）→ 重新打开并走完整校验/迁移路径。
    /// 替换失败时旧库文件保持原样（先复制后删残留，覆盖是原子性的 Copy(overwrite) 不保证——
    /// 因此恢复前调用方必须已生成当前状态安全备份）。
    /// </summary>
    public static async Task<SqliteLibraryStore> SwapFromStagedAsync(
        string canonicalDataDirectory,
        string stagedDatabasePath,
        SqliteLibraryStoreOptions options,
        CancellationToken ct)
    {
        var target = Path.Combine(canonicalDataDirectory, DatabaseFileName);
        File.Copy(stagedDatabasePath, target, overwrite: true);
        foreach (var residue in new[] { target + "-wal", target + "-shm" })
        {
            if (File.Exists(residue))
            {
                File.Delete(residue);
            }
        }

        var reopened = await TryOpenAsync(canonicalDataDirectory, options, ct);
        if (!reopened.IsOpened)
        {
            throw new InvalidOperationException(
                $"恢复后的库无法打开（{reopened.Status}）：{reopened.Detail}");
        }

        return reopened.Store!;
    }

    /// <summary>幂等收据查询（契约 7.1）；收据属于当前库实例。</summary>
    public RequestReceipt? TryGetReceipt(string actor, string operationId, string idempotencyKey)
    {
        lock (_sync)
        {
            return RequestReceiptStore.TryGet(_connection, Info.LibraryInstanceId, actor, operationId, idempotencyKey);
        }
    }

    public void InsertPreparedReceipt(RequestReceipt receipt)
    {
        lock (_sync)
        {
            RequestReceiptStore.InsertPrepared(_connection, receipt);
        }
    }

    public void UpdateReceiptAttempt(RequestReceipt receipt, string attemptJson)
    {
        lock (_sync)
        {
            RequestReceiptStore.UpdateAttempt(_connection, receipt, attemptJson);
        }
    }

    public void CompleteReceipt(RequestReceipt receipt, string resultJson)
    {
        lock (_sync)
        {
            RequestReceiptStore.CompleteWithPrune(_connection, receipt, resultJson, DateTime.UtcNow);
        }
    }

    // T11 目录存储转发：候选/游戏/忽略规则操作绑定当前库连接。

    public bool UpsertCandidate(PersistedCandidate candidate)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.UpsertCandidate(_connection, candidate);
        }
    }

    public void PromoteRescannedCandidate(string physicalPath, DateTime utcNow)
    {
        lock (_sync)
        {
            LibraryCatalogStore.PromoteRescannedCandidate(_connection, physicalPath, utcNow);
        }
    }

    public PersistedCandidate? TryGetCandidate(string candidateId)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.TryGetCandidate(_connection, candidateId);
        }
    }

    public IReadOnlyList<PersistedCandidate> ListCandidates()
    {
        lock (_sync)
        {
            return LibraryCatalogStore.ListCandidates(_connection);
        }
    }

    public (int Total, IReadOnlyList<PersistedCandidate> Items) QueryCandidates(
        string? jobId,
        string? state,
        int limit,
        int offset)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.QueryCandidates(_connection, jobId, state, limit, offset);
        }
    }

    public PersistedCandidate? TransitionCandidate(
        string candidateId, string fromState, string toState, int expectedRevision, string? gameId, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.TransitionCandidate(_connection, candidateId, fromState, toState, expectedRevision, gameId, utcNow);
        }
    }

    public void InsertGame(GameCard game)
    {
        lock (_sync)
        {
            LibraryCatalogStore.InsertGame(_connection, game);
        }
    }

    public int? RemoveGame(string gameId, int expectedRevision, IgnoreRule ignore, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.RemoveGame(_connection, gameId, expectedRevision, ignore, utcNow);
        }
    }

    public GameCard? TryGetGame(string gameId)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.TryGetGame(_connection, gameId);
        }
    }

    public IReadOnlyList<GameCard> ListGames()
    {
        lock (_sync)
        {
            return LibraryCatalogStore.ListGames(_connection);
        }
    }

    /// <summary>数据库侧检索（阶段三）：搜索/收藏/标签过滤 + 排序 + 分页；limit &lt;= 0 全量。</summary>
    public (int Total, IReadOnlyList<GameCard> Items) QueryGames(
        string? search, bool favoriteOnly, string? tagId, string? sort, int limit, int offset)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.QueryGames(_connection, search, favoriteOnly, tagId, sort, limit, offset);
        }
    }

    /// <summary>列表页批量充实（R41）：单锁一次取回字段/封面/标签，取代逐游戏 4 次查询。</summary>
    public IReadOnlyDictionary<string, GameCardEnrichment> EnrichGameCards(
        IReadOnlyList<GameCard> games)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.EnrichGameCards(_connection, games);
        }
    }

    // T-collections 标签转发（v18）。

    public IReadOnlyList<PersistedTag> ListTags()
    {
        lock (_sync)
        {
            return TagStore.ListTags(_connection);
        }
    }

    public PersistedTag? TryGetTag(string tagId)
    {
        lock (_sync)
        {
            return TagStore.TryGetTag(_connection, tagId);
        }
    }

    public PersistedTag? TryGetTagByName(string kind, string name)
    {
        lock (_sync)
        {
            return TagStore.TryGetTagByName(_connection, kind, name);
        }
    }

    public void CreateTag(PersistedTag tag)
    {
        lock (_sync)
        {
            TagStore.CreateTag(_connection, tag);
        }
    }

    public int? UpdateTag(string tagId, string? name, string? color, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return TagStore.UpdateTag(_connection, tagId, name, color, expectedRevision, utcNow);
        }
    }

    public IReadOnlyList<string> RemoveTag(string tagId)
    {
        lock (_sync)
        {
            return TagStore.RemoveTag(_connection, tagId);
        }
    }

    public bool AssignTag(string gameId, string tagId, DateTime utcNow)
    {
        lock (_sync)
        {
            return TagStore.AssignTag(_connection, gameId, tagId, utcNow);
        }
    }

    public string? UnassignTag(string gameId, string tagId)
    {
        lock (_sync)
        {
            return TagStore.UnassignTag(_connection, gameId, tagId);
        }
    }

    public void SetTagOverride(string gameId, string tagKind, string tagName, string action, DateTime utcNow)
    {
        lock (_sync)
        {
            TagStore.SetOverride(_connection, gameId, tagKind, tagName, action, utcNow);
        }
    }

    public bool ClearTagOverride(string gameId, string tagKind, string tagName, DateTime utcNow)
    {
        lock (_sync)
        {
            return TagStore.ClearOverride(_connection, gameId, tagKind, tagName, utcNow);
        }
    }

    public IReadOnlyList<(string Kind, string Name)> ListGameTags(string gameId)
    {
        lock (_sync)
        {
            return TagStore.ListGameTags(_connection, gameId);
        }
    }

    public bool EnsureEngineTagAssigned(string gameId, string engine, DateTime utcNow)
    {
        lock (_sync)
        {
            return TagStore.EnsureEngineTagAssigned(_connection, gameId, engine, utcNow);
        }
    }

    public GameCard? TryGetGameByRootPath(string rootPath)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.TryGetGameByRootPath(_connection, rootPath);
        }
    }

    public void InsertIgnoreRule(IgnoreRule rule)
    {
        lock (_sync)
        {
            LibraryCatalogStore.InsertIgnoreRule(_connection, rule);
        }
    }

    // T13 收藏与翻译策略转发。

    public int? SetFavorite(string gameId, bool favorite, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.SetFavorite(_connection, gameId, favorite, expectedRevision, utcNow);
        }
    }

    public int? SetTranslationOverride(string gameId, string? overrideValue, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.SetTranslationOverride(_connection, gameId, overrideValue, expectedRevision, utcNow);
        }
    }

    // T17 可用性核对与重关联转发。

    public bool UpdateAvailability(string gameId, string availability, DateTime? missingSinceUtc, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.UpdateAvailability(_connection, gameId, availability, missingSinceUtc, utcNow);
        }
    }

    public int? RelinkGame(string gameId, string newRootPath, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.RelinkGame(_connection, gameId, newRootPath, expectedRevision, utcNow);
        }
    }

    // T18 通知批转发。

    public (NotificationBatch Batch, bool Created)? EnsureCandidateBatch(DateTime utcNow)
    {
        lock (_sync)
        {
            return NotificationStore.EnsureCandidateBatch(_connection, utcNow);
        }
    }

    public IReadOnlyList<NotificationBatch> ListNotifications(string? state)
    {
        lock (_sync)
        {
            return NotificationStore.ListBatches(_connection, state);
        }
    }

    public NotificationBatch? TryGetNotification(string notificationId)
    {
        lock (_sync)
        {
            return NotificationStore.TryGetBatch(_connection, notificationId);
        }
    }

    public NotificationBatch? TransitionNotification(string notificationId, string toState, DateTime utcNow)
    {
        lock (_sync)
        {
            return NotificationStore.TransitionBatch(_connection, notificationId, toState, utcNow);
        }
    }

    // settings 转发。

    public AppSettingsSnapshot ReadSettings()
    {
        lock (_sync)
        {
            return SettingsStore.Read(_connection);
        }
    }

    public int WriteSettingsKeys(IEnumerable<(string Key, string? Value)> keys, DateTime utcNow)
    {
        lock (_sync)
        {
            return SettingsStore.WriteKeys(_connection, keys, utcNow);
        }
    }

    public int ResetSettings(DateTime utcNow)
    {
        lock (_sync)
        {
            return SettingsStore.ResetAll(_connection, utcNow);
        }
    }

    // T15-C 自定义视图转发。

    public void InsertView(LibraryView view)
    {
        lock (_sync)
        {
            LibraryViewStore.InsertView(_connection, view);
        }
    }

    public LibraryView? TryGetView(string viewId)
    {
        lock (_sync)
        {
            return LibraryViewStore.TryGetView(_connection, viewId);
        }
    }

    public IReadOnlyList<LibraryView> ListViews()
    {
        lock (_sync)
        {
            return LibraryViewStore.ListViews(_connection);
        }
    }

    public int? UpdateView(
        string viewId, string? name, string? search, bool? favoriteOnly, string? sort, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return LibraryViewStore.UpdateView(_connection, viewId, name, search, favoriteOnly, sort, expectedRevision, utcNow);
        }
    }

    public bool DeleteView(string viewId)
    {
        lock (_sync)
        {
            return LibraryViewStore.DeleteView(_connection, viewId);
        }
    }

    public IReadOnlyList<IgnoreRule> ListIgnoreRules()
    {
        lock (_sync)
        {
            return LibraryCatalogStore.ListIgnoreRules(_connection);
        }
    }

    public IReadOnlyList<string> RemoveIgnoreRule(string ignoreId)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.RemoveIgnoreRule(_connection, ignoreId);
        }
    }

    public bool IsSuppressedByIgnoreRule(string physicalPath, string? boundGameId)
    {
        lock (_sync)
        {
            return LibraryCatalogStore.IsSuppressedByIgnoreRule(_connection, physicalPath, boundGameId);
        }
    }

    // T14 资料与封面转发。

    public int? SetGameField(string gameId, string fieldKey, string? value, string source, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return GameProfileStore.SetGameField(_connection, gameId, fieldKey, value, source, expectedRevision, utcNow);
        }
    }

    public int? ResetGameField(string gameId, string fieldKey, string autoValue, int expectedRevision, DateTime utcNow)
    {
        lock (_sync)
        {
            return GameProfileStore.ResetGameField(_connection, gameId, fieldKey, autoValue, expectedRevision, utcNow);
        }
    }

    public (string? Value, string Source) EffectiveField(string gameId, string fieldKey, string fallback)
    {
        lock (_sync)
        {
            return GameProfileStore.EffectiveField(_connection, gameId, fieldKey, fallback);
        }
    }

    public GameAsset ImportAsset(string gameId, string importedFilePath, DateTime utcNow)
    {
        lock (_sync)
        {
            return GameProfileStore.ImportAsset(_connection, gameId, importedFilePath, utcNow);
        }
    }

    public IReadOnlyList<GameAsset> ListAssets(string gameId)
    {
        lock (_sync)
        {
            return GameProfileStore.ListAssets(_connection, gameId);
        }
    }

    public GameAsset? TryGetAsset(string assetId)
    {
        lock (_sync)
        {
            return GameProfileStore.TryGetAsset(_connection, assetId);
        }
    }

    public void ChooseAsset(string gameId, string assetId)
    {
        lock (_sync)
        {
            GameProfileStore.ChooseAsset(_connection, gameId, assetId);
        }
    }

    public string? ResetCover(string gameId)
    {
        lock (_sync)
        {
            return GameProfileStore.ResetCover(_connection, gameId);
        }
    }

    public string? RemoveAsset(string assetId)
    {
        lock (_sync)
        {
            return GameProfileStore.RemoveAsset(_connection, assetId);
        }
    }

    public void WriteAutoField(string gameId, string fieldKey, string value, DateTime utcNow)
    {
        lock (_sync)
        {
            GameProfileStore.WriteAutoField(_connection, gameId, fieldKey, value, utcNow);
        }
    }

    // 运行态持久化转发（v13+：库根/Profile/启动历史/作业记录）。

    public IReadOnlyList<PersistedRoot> ReadRoots()
    {
        lock (_sync)
        {
            return RuntimeStateStore.ReadRoots(_connection);
        }
    }

    public void UpsertRoot(PersistedRoot root, DateTime utcNow)
    {
        lock (_sync)
        {
            RuntimeStateStore.UpsertRoot(_connection, root, utcNow);
        }
    }

    public void DeleteRoot(string rootId)
    {
        lock (_sync)
        {
            RuntimeStateStore.DeleteRoot(_connection, rootId);
        }
    }

    public IReadOnlyList<PersistedProfile> ReadProfiles()
    {
        lock (_sync)
        {
            return RuntimeStateStore.ReadProfiles(_connection);
        }
    }

    public void UpsertProfile(PersistedProfile profile, DateTime utcNow)
    {
        lock (_sync)
        {
            RuntimeStateStore.UpsertProfile(_connection, profile, utcNow);
        }
    }

    public void DeleteProfile(string profileId)
    {
        lock (_sync)
        {
            RuntimeStateStore.DeleteProfile(_connection, profileId);
        }
    }

    public IReadOnlyList<PersistedLaunchAttempt> ReadLaunchAttempts()
    {
        lock (_sync)
        {
            return RuntimeStateStore.ReadAttempts(_connection);
        }
    }

    public void UpsertLaunchAttempt(PersistedLaunchAttempt attempt)
    {
        lock (_sync)
        {
            RuntimeStateStore.UpsertAttempt(_connection, attempt);
        }
    }

    public IReadOnlyList<PersistedJobRecord> ReadJobRecords()
    {
        lock (_sync)
        {
            return RuntimeStateStore.ReadJobs(_connection);
        }
    }

    public void UpsertJobRecord(PersistedJobRecord job)
    {
        lock (_sync)
        {
            RuntimeStateStore.UpsertJob(_connection, job);
        }
    }

    public int MarkInterruptedJobs(DateTime utcNow)
    {
        lock (_sync)
        {
            return RuntimeStateStore.MarkInterruptedJobs(_connection, utcNow);
        }
    }

    // T08 验证记录转发。

    public void InsertVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record)
    {
        lock (_sync)
        {
            VerificationStore.Insert(_connection, record);
        }
    }

    public GameLibrary.Domain.Tools.ToolVerificationRecord? TryGetVerification(string recordId)
    {
        lock (_sync)
        {
            return VerificationStore.TryGet(_connection, recordId);
        }
    }

    public IReadOnlyList<GameLibrary.Domain.Tools.ToolVerificationRecord> ListVerifications(string? toolId)
    {
        lock (_sync)
        {
            return VerificationStore.List(_connection, toolId);
        }
    }

    public void UpdateVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record)
    {
        lock (_sync)
        {
            VerificationStore.Update(_connection, record);
        }
    }

    /// <summary>一致性备份到新文件（SQLite 备份 API，WAL 下同样一致）。目标已存在则拒绝。</summary>
    public async Task CreateBackupAsync(string targetPath, CancellationToken ct)
    {
        if (File.Exists(targetPath))
        {
            throw new IOException($"备份目标已存在：{targetPath}");
        }

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var target = new SqliteConnection($"Data Source={targetPath}{ConnectionSuffix}");
        await target.OpenAsync(ct);
        lock (_sync)
        {
            _connection.BackupDatabase(target);
        }
    }

    /// <summary>更换数据纪元（备份恢复后调用）；旧 Revision/游标/计划随之失效。</summary>
    public async Task<string> RenewDataEpochAsync(CancellationToken ct)
    {
        var epoch = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        // lock 体内不能 await：纪元更新是单条本地命令，同步执行（与读路径同代价）。
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE schema_info SET data_epoch = $e, updated_utc = $u WHERE id = 1";
            command.Parameters.AddWithValue("$e", epoch);
            command.Parameters.AddWithValue("$u", now);
            command.ExecuteNonQuery();

            Info = Info with { DataEpoch = epoch };
        }

        await Task.CompletedTask;
        return epoch;
    }

    /// <summary>显式建库（library.init）。库已存在返回 AlreadyInitialized；0 字节残留文件可安全重建。</summary>
    public static async Task<LibraryOpenResult> InitializeAsync(
        string canonicalDataDirectory,
        SqliteLibraryStoreOptions options,
        CancellationToken ct)
    {
        ValidateConsecutiveVersions(options.Migrations);
        var dbPath = Path.Combine(canonicalDataDirectory, DatabaseFileName);
        if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
        {
            return LibraryOpenResult.Fail(
                LibraryOpenStatus.AlreadyInitialized,
                $"库已存在：{dbPath}");
        }

        try
        {
            Directory.CreateDirectory(canonicalDataDirectory);
            Directory.CreateDirectory(Path.Combine(canonicalDataDirectory, BackupsFolderName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LibraryOpenResult.Fail(LibraryOpenStatus.IoError, $"无法创建数据目录：{ex.Message}");
        }

        SqliteConnection? connection = null;
        try
        {
            connection = await OpenConnectionAsync(dbPath, ct);
            await ApplyPragmasAsync(connection, ct);

            var now = DateTime.UtcNow;
            var instanceId = Guid.NewGuid().ToString("N");
            var epoch = Guid.NewGuid().ToString("N");
            var version = options.Migrations.Max(m => m.Version);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var migration in options.Migrations)
            {
                await ExecuteAsync(connection, transaction, migration.Sql, ct);
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO schema_info
                    (id, schema_version, app_version, api_version, library_instance_id, data_epoch, created_utc, updated_utc)
                VALUES (1, $version, $app, $api, $instance, $epoch, $created, $updated)
                """,
                ct,
                ("$version", version),
                ("$app", options.AppVersion),
                ("$api", options.ApiVersion),
                ("$instance", instanceId),
                ("$epoch", epoch),
                ("$created", now.ToString("O", CultureInfo.InvariantCulture)),
                ("$updated", now.ToString("O", CultureInfo.InvariantCulture)));
            await transaction.CommitAsync(ct);

            var info = new LibraryDatabaseInfo(
                version, options.AppVersion, options.ApiVersion, instanceId, epoch, now);
            return LibraryOpenResult.Opened(new SqliteLibraryStore(connection, options, info));
        }
        catch (Exception ex)
        {
            await DisposeConnectionAsync(connection);
            if (ex is IOException or UnauthorizedAccessException)
            {
                return LibraryOpenResult.Fail(LibraryOpenStatus.IoError, ex.Message);
            }

            return LibraryOpenResult.Fail(LibraryOpenStatus.RecoveryRequired, $"建库失败：{ex.Message}");
        }
    }

    /// <summary>打开已存在的库：校验结构、版本；不自动建库；损坏返回 RecoveryRequired。</summary>
    public static async Task<LibraryOpenResult> TryOpenAsync(
        string canonicalDataDirectory,
        SqliteLibraryStoreOptions options,
        CancellationToken ct)
    {
        var dbPath = Path.Combine(canonicalDataDirectory, DatabaseFileName);
        if (!File.Exists(dbPath))
        {
            return LibraryOpenResult.Fail(LibraryOpenStatus.NeedsInitialization, $"库不存在：{dbPath}");
        }

        if (new FileInfo(dbPath).Length == 0)
        {
            return LibraryOpenResult.Fail(
                LibraryOpenStatus.RecoveryRequired,
                "library.db 为 0 字节残留；拒绝静默按新库打开，需显式初始化或恢复");
        }

        SqliteConnection? connection = null;
        try
        {
            connection = await OpenConnectionAsync(dbPath, ct);
            await ApplyPragmasAsync(connection, ct);

            var info = await ReadInfoAsync(connection, ct);
            if (info.SchemaVersion > options.MaxSupportedSchemaVersion)
            {
                await DisposeConnectionAsync(connection);
                return LibraryOpenResult.Fail(
                    LibraryOpenStatus.SchemaTooNew,
                    $"库 schema 版本 {info.SchemaVersion} 高于程序支持 {options.MaxSupportedSchemaVersion}；拒绝写入，不自动降级");
            }

            if (info.SchemaVersion < options.MaxSupportedSchemaVersion)
            {
                return await UpgradeAsync(connection, options, info, canonicalDataDirectory, ct);
            }

            return LibraryOpenResult.Opened(new SqliteLibraryStore(connection, options, info));
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            await DisposeConnectionAsync(connection);
            return LibraryOpenResult.Fail(LibraryOpenStatus.RecoveryRequired, $"数据库损坏：{ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            await DisposeConnectionAsync(connection);
            return LibraryOpenResult.Fail(
                LibraryOpenStatus.RecoveryRequired,
                $"库结构无法解释（缺 schema_info 或字段非法）：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DisposeConnectionAsync(connection);
            return LibraryOpenResult.Fail(LibraryOpenStatus.IoError, $"文件系统错误：{ex.Message}");
        }
    }

    /// <summary>
    /// 升级旧库：迁移前先用 SQLite 备份 API 生成一致快照；每条迁移单事务执行
    /// （DDL 在 SQLite 中可回滚）；失败即中止，库停留在最后成功的版本，不半升级。
    /// 迁移完成后执行 PRAGMA foreign_key_check 一致性核查，有孤立引用即视为失败。
    /// </summary>
    private static async Task<LibraryOpenResult> UpgradeAsync(
        SqliteConnection connection,
        SqliteLibraryStoreOptions options,
        LibraryDatabaseInfo current,
        string canonicalDataDirectory,
        CancellationToken ct)
    {
        ValidateConsecutiveVersions(options.Migrations);
        var pending = options.Migrations
            .Where(m => m.Version > current.SchemaVersion)
            .OrderBy(m => m.Version)
            .ToList();

        var snapshotPath = Path.Combine(
            canonicalDataDirectory,
            BackupsFolderName,
            $"pre-migration-v{current.SchemaVersion}-to-v{options.MaxSupportedSchemaVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

        try
        {
            await CreateSnapshotAsync(connection, snapshotPath, ct);

            foreach (var migration in pending)
            {
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, transaction, migration.Sql, ct);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE schema_info SET schema_version = $v, updated_utc = $u WHERE id = 1",
                    ct,
                    ("$v", migration.Version),
                    ("$u", now));
                await transaction.CommitAsync(ct);
            }

            // 一致性核查（v1 审查意见：外键开关必须真实有约束兜底）。
            var violations = await ReadForeignKeyViolationsAsync(connection, ct);
            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    $"外键一致性核查失败：{string.Join("; ", violations.Take(5))}");
            }

            // DELETE 只释放页供复用，不缩小文件；声明 RequiresVacuum 的迁移在此回收磁盘。
            // VACUUM 不能在事务内执行，故放在上面逐条迁移的事务全部提交之后。
            // 失败前的 pre-migration 快照已在方法开头生成，VACUUM 自身失败则整体判 MigrationFailed。
            if (pending.Any(m => m.RequiresVacuum))
            {
                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync(ct);
            }

            var info = await ReadInfoAsync(connection, ct);
            return LibraryOpenResult.Opened(new SqliteLibraryStore(connection, options, info));
        }
        catch (Exception ex)
        {
            await DisposeConnectionAsync(connection);
            return LibraryOpenResult.Fail(
                LibraryOpenStatus.MigrationFailed,
                $"迁移失败，库保留在版本 {current.SchemaVersion}；迁移前快照：{snapshotPath}；原因：{ex.Message}");
        }
    }

    /// <summary>读取 PRAGMA foreign_key_check 违规行（表名 + rowid），最多返回前 20 条。</summary>
    private static async Task<IReadOnlyList<string>> ReadForeignKeyViolationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var problems = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct) && problems.Count < 20)
        {
            problems.Add($"{reader.GetString(0)} row {reader.GetValue(1)}");
        }

        return problems;
    }

    /// <summary>迁移集必须是从 1 开始的连续整数版本，缺失中间版本属于程序缺陷。</summary>
    private static void ValidateConsecutiveVersions(IReadOnlyList<DatabaseMigration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new ArgumentException(
                    $"迁移版本不连续：第 {i + 1} 项应为版本 {i + 1}，实际 {migrations[i].Version}");
            }
        }
    }

    /// <summary>SQLite 备份 API 一致快照：WAL 模式下也获得一致副本，不复制正在运行的主库文件。</summary>
    private static async Task CreateSnapshotAsync(SqliteConnection source, string targetPath, CancellationToken ct)
    {
        await using var target = new SqliteConnection($"Data Source={targetPath}{ConnectionSuffix}");
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
    }

    private const string ConnectionSuffix = ";Pooling=False";

    private static Task<SqliteConnection> OpenConnectionAsync(string dbPath, CancellationToken ct)
    {
        // 单宿主单连接长持；关闭连接池，避免 Dispose 后仍持有文件句柄
        // （影响备份、恢复与测试清理的独占语义）。
        var connection = new SqliteConnection($"Data Source={dbPath}{ConnectionSuffix}");
        return Task.FromResult(connection);
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", ct);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", ct);
        await ExecuteAsync(connection, null, "PRAGMA synchronous = NORMAL;", ct);
        await ExecuteAsync(connection, null, $"PRAGMA busy_timeout = {BusyTimeoutMs};", ct);
    }

    private static async Task<LibraryDatabaseInfo> ReadInfoAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, app_version, api_version, library_instance_id, data_epoch, created_utc
            FROM schema_info WHERE id = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("schema_info 缺少单例行");
        }

        return new LibraryDatabaseInfo(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool IsCorruption(SqliteException ex) =>
        CorruptionCodes.Contains(ex.SqliteErrorCode);

    private static async Task DisposeConnectionAsync(SqliteConnection? connection)
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
