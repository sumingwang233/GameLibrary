using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 唯一宿主持有的 SQLite 库存储（ADR-0004）：每连接启用外键/WAL/busy_timeout；
/// 打开即校验 schema 版本；损坏进入 RecoveryRequired，绝不静默重建空库。
/// 本类不是线程安全的队列化写入器；调用方（Host）负责单写入队列。
/// </summary>
public sealed class SqliteLibraryStore : IAsyncDisposable
{
    public const string DatabaseFileName = "library.db";
    public const string BackupsFolderName = "backups";

    private const int BusyTimeoutMs = 5000;
    private static readonly int[] CorruptionCodes = [11, 26];

    private readonly SqliteConnection _connection;
    private readonly SqliteLibraryStoreOptions _options;

    private SqliteLibraryStore(SqliteConnection connection, SqliteLibraryStoreOptions options, LibraryDatabaseInfo info)
    {
        _connection = connection;
        _options = options;
        Info = info;
    }

    public LibraryDatabaseInfo Info { get; private set; }

    public string DatabasePath => _connection.DataSource;

    /// <summary>宿主内部组件（事件持久化等）共享的连接；仅限 Host/Infrastructure 组合内部使用，外部不得直接执行 SQL。</summary>
    public SqliteConnection DatabaseConnection => _connection;

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
    public RequestReceipt? TryGetReceipt(string actor, string operationId, string idempotencyKey) =>
        RequestReceiptStore.TryGet(_connection, Info.LibraryInstanceId, actor, operationId, idempotencyKey);

    public void InsertPreparedReceipt(RequestReceipt receipt) =>
        RequestReceiptStore.InsertPrepared(_connection, receipt);

    public void UpdateReceiptAttempt(RequestReceipt receipt, string attemptJson) =>
        RequestReceiptStore.UpdateAttempt(_connection, receipt, attemptJson);

    public void CompleteReceipt(RequestReceipt receipt, string resultJson) =>
        RequestReceiptStore.Complete(_connection, receipt, resultJson);

    // T11 目录存储转发：候选/游戏/忽略规则操作绑定当前库连接。

    public bool UpsertCandidate(PersistedCandidate candidate) =>
        LibraryCatalogStore.UpsertCandidate(_connection, candidate);

    public void PromoteRescannedCandidate(string physicalPath, DateTime utcNow) =>
        LibraryCatalogStore.PromoteRescannedCandidate(_connection, physicalPath, utcNow);

    public PersistedCandidate? TryGetCandidate(string candidateId) =>
        LibraryCatalogStore.TryGetCandidate(_connection, candidateId);

    public IReadOnlyList<PersistedCandidate> ListCandidates() =>
        LibraryCatalogStore.ListCandidates(_connection);

    public PersistedCandidate? TransitionCandidate(
        string candidateId, string fromState, string toState, int expectedRevision, string? gameId, DateTime utcNow) =>
        LibraryCatalogStore.TransitionCandidate(_connection, candidateId, fromState, toState, expectedRevision, gameId, utcNow);

    public void InsertGame(GameCard game) => LibraryCatalogStore.InsertGame(_connection, game);

    public GameCard? TryGetGame(string gameId) => LibraryCatalogStore.TryGetGame(_connection, gameId);

    public IReadOnlyList<GameCard> ListGames() => LibraryCatalogStore.ListGames(_connection);

    public GameCard? TryGetGameByRootPath(string rootPath) =>
        LibraryCatalogStore.TryGetGameByRootPath(_connection, rootPath);

    public void InsertIgnoreRule(IgnoreRule rule) => LibraryCatalogStore.InsertIgnoreRule(_connection, rule);

    // T13 收藏与翻译策略转发。

    public int? SetFavorite(string gameId, bool favorite, int expectedRevision, DateTime utcNow) =>
        LibraryCatalogStore.SetFavorite(_connection, gameId, favorite, expectedRevision, utcNow);

    public int? SetTranslationOverride(string gameId, string? overrideValue, int expectedRevision, DateTime utcNow) =>
        LibraryCatalogStore.SetTranslationOverride(_connection, gameId, overrideValue, expectedRevision, utcNow);

    // T17 可用性核对与重关联转发。

    public bool UpdateAvailability(string gameId, string availability, DateTime? missingSinceUtc, DateTime utcNow) =>
        LibraryCatalogStore.UpdateAvailability(_connection, gameId, availability, missingSinceUtc, utcNow);

    public int? RelinkGame(string gameId, string newRootPath, int expectedRevision, DateTime utcNow) =>
        LibraryCatalogStore.RelinkGame(_connection, gameId, newRootPath, expectedRevision, utcNow);

    // T18 通知批转发。

    public (NotificationBatch Batch, bool Created)? EnsureCandidateBatch(DateTime utcNow) =>
        NotificationStore.EnsureCandidateBatch(_connection, utcNow);

    public IReadOnlyList<NotificationBatch> ListNotifications(string? state) =>
        NotificationStore.ListBatches(_connection, state);

    public NotificationBatch? TryGetNotification(string notificationId) =>
        NotificationStore.TryGetBatch(_connection, notificationId);

    public NotificationBatch? TransitionNotification(string notificationId, string toState, DateTime utcNow) =>
        NotificationStore.TransitionBatch(_connection, notificationId, toState, utcNow);

    // settings 转发。

    public AppSettingsSnapshot ReadSettings() => SettingsStore.Read(_connection);

    public int WriteSettingsKeys(IEnumerable<(string Key, string? Value)> keys, DateTime utcNow) =>
        SettingsStore.WriteKeys(_connection, keys, utcNow);

    public int ResetSettings(DateTime utcNow) => SettingsStore.ResetAll(_connection, utcNow);

    // T15-C 自定义视图转发。

    public void InsertView(LibraryView view) => LibraryViewStore.InsertView(_connection, view);

    public LibraryView? TryGetView(string viewId) => LibraryViewStore.TryGetView(_connection, viewId);

    public IReadOnlyList<LibraryView> ListViews() => LibraryViewStore.ListViews(_connection);

    public int? UpdateView(
        string viewId, string? name, string? search, bool? favoriteOnly, string? sort, int expectedRevision, DateTime utcNow) =>
        LibraryViewStore.UpdateView(_connection, viewId, name, search, favoriteOnly, sort, expectedRevision, utcNow);

    public bool DeleteView(string viewId) => LibraryViewStore.DeleteView(_connection, viewId);

    public IReadOnlyList<IgnoreRule> ListIgnoreRules() => LibraryCatalogStore.ListIgnoreRules(_connection);

    public IReadOnlyList<string> RemoveIgnoreRule(string ignoreId) =>
        LibraryCatalogStore.RemoveIgnoreRule(_connection, ignoreId);

    public bool IsSuppressedByIgnoreRule(string physicalPath, string? boundGameId) =>
        LibraryCatalogStore.IsSuppressedByIgnoreRule(_connection, physicalPath, boundGameId);

    // T14 资料与封面转发。

    public int? SetGameField(string gameId, string fieldKey, string? value, string source, int expectedRevision, DateTime utcNow) =>
        GameProfileStore.SetGameField(_connection, gameId, fieldKey, value, source, expectedRevision, utcNow);

    public int? ResetGameField(string gameId, string fieldKey, string autoValue, int expectedRevision, DateTime utcNow) =>
        GameProfileStore.ResetGameField(_connection, gameId, fieldKey, autoValue, expectedRevision, utcNow);

    public (string? Value, string Source) EffectiveField(string gameId, string fieldKey, string fallback) =>
        GameProfileStore.EffectiveField(_connection, gameId, fieldKey, fallback);

    public GameAsset ImportAsset(string gameId, string importedFilePath, DateTime utcNow) =>
        GameProfileStore.ImportAsset(_connection, gameId, importedFilePath, utcNow);

    public IReadOnlyList<GameAsset> ListAssets(string gameId) =>
        GameProfileStore.ListAssets(_connection, gameId);

    public GameAsset? TryGetAsset(string assetId) =>
        GameProfileStore.TryGetAsset(_connection, assetId);

    public void ChooseAsset(string gameId, string assetId) =>
        GameProfileStore.ChooseAsset(_connection, gameId, assetId);

    public string? ResetCover(string gameId) =>
        GameProfileStore.ResetCover(_connection, gameId);

    public string? RemoveAsset(string assetId) =>
        GameProfileStore.RemoveAsset(_connection, assetId);

    public void WriteAutoField(string gameId, string fieldKey, string value, DateTime utcNow) =>
        GameProfileStore.WriteAutoField(_connection, gameId, fieldKey, value, utcNow);

    // T08 验证记录转发。

    public void InsertVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record) =>
        VerificationStore.Insert(_connection, record);

    public GameLibrary.Domain.Tools.ToolVerificationRecord? TryGetVerification(string recordId) =>
        VerificationStore.TryGet(_connection, recordId);

    public IReadOnlyList<GameLibrary.Domain.Tools.ToolVerificationRecord> ListVerifications(string? toolId) =>
        VerificationStore.List(_connection, toolId);

    public void UpdateVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record) =>
        VerificationStore.Update(_connection, record);

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
        _connection.BackupDatabase(target);
    }

    /// <summary>更换数据纪元（备份恢复后调用）；旧 Revision/游标/计划随之失效。</summary>
    public async Task<string> RenewDataEpochAsync(CancellationToken ct)
    {
        var epoch = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await ExecuteAsync(
            _connection,
            null,
            "UPDATE schema_info SET data_epoch = $e, updated_utc = $u WHERE id = 1",
            ct,
            ("$e", epoch),
            ("$u", now));

        Info = Info with { DataEpoch = epoch };
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
