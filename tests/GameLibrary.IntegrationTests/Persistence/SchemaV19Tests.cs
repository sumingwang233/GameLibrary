using System.Globalization;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v19 迁移：查询索引落地、事件/收据按计数裁剪（prepared 收据绝不删除）、
/// 以及 RequiresVacuum 真正回收磁盘（DELETE 只释放页供复用，不缩小文件）。
/// </summary>
public sealed class SchemaV19Tests
{
    private static readonly string[] ExpectedIndexes =
    [
        "idx_games_active_updated",
        "idx_games_active_title",
        "idx_games_active_accepted",
        "idx_games_active_root",
        "idx_game_assets_game",
        "idx_candidates_review_state",
        "idx_game_tags_tag",
        "idx_request_receipts_prune",
    ];

    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options(params DatabaseMigration[] migrations) =>
        new() { AppVersion = "0.1.0-dev", ApiVersion = "1", Migrations = migrations };

    private static SqliteLibraryStoreOptions Options(IReadOnlyList<DatabaseMigration> migrations) =>
        new() { AppVersion = "0.1.0-dev", ApiVersion = "1", Migrations = migrations };

    private static string DbPath(string dataDir) => Path.Combine(dataDir, "library.db");

    [Fact]
    public async Task FreshLibrary_CreatesAllV19Indexes()
    {
        var dataDir = FreshDataDir("v19-indexes");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(dataDir, Options(DatabaseMigrations.All), CancellationToken.None);
            Assert.True(result.IsOpened, result.Detail);
            Assert.True(result.Store!.Info.SchemaVersion >= 19, $"schema_version={result.Store.Info.SchemaVersion}");
            await result.Store.DisposeAsync();

            var names = await IndexNamesAsync(dataDir);
            foreach (var expected in ExpectedIndexes)
            {
                Assert.Contains(expected, names);
            }
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task UpgradeFromV18_TrimsByCountKeepsPreparedAndShrinksFile()
    {
        var dataDir = FreshDataDir("v19-trim");
        try
        {
            // 建一个停在 v18 的库。
            var v18 = DatabaseMigrations.All.Take(18).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v18), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            Assert.Equal(18, seeded.Store!.Info.SchemaVersion);
            await seeded.Store.DisposeAsync();

            // 灌入超量事件与收据：10_500 事件（上限 10_000）、2_100 completed + 3 prepared。
            const int eventCount = 10_500;
            const int completedCount = 2_100;
            var payload = new string('x', 200);
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

                await using (var insertEvent = connection.CreateCommand())
                {
                    insertEvent.Transaction = transaction;
                    insertEvent.CommandText =
                        "INSERT INTO event_records (sequence, data_epoch, type, entity_key, payload_json, timestamp_utc) " +
                        "VALUES ($seq, 'epochA', 'game.created', $key, $payload, $ts)";
                    var seq = insertEvent.Parameters.Add("$seq", SqliteType.Integer);
                    var key = insertEvent.Parameters.Add("$key", SqliteType.Text);
                    insertEvent.Parameters.AddWithValue("$payload", payload);
                    insertEvent.Parameters.AddWithValue("$ts", "2026-09-19T00:00:00.0000000Z");
                    for (var i = 1; i <= eventCount; i++)
                    {
                        seq.Value = i;
                        key.Value = "game-" + i.ToString(CultureInfo.InvariantCulture);
                        await insertEvent.ExecuteNonQueryAsync();
                    }
                }

                await using (var insertReceipt = connection.CreateCommand())
                {
                    insertReceipt.Transaction = transaction;
                    insertReceipt.CommandText =
                        "INSERT INTO request_receipts (library_instance_id, actor, operation_id, idempotency_key, " +
                        "request_digest, status, attempt_json, result_json, created_utc, updated_utc) " +
                        "VALUES ('inst', 'ui', 'candidates.accept', $key, 'd', $status, NULL, $result, $created, $created)";
                    var key = insertReceipt.Parameters.Add("$key", SqliteType.Text);
                    var status = insertReceipt.Parameters.Add("$status", SqliteType.Text);
                    var result = insertReceipt.Parameters.Add("$result", SqliteType.Text);
                    var created = insertReceipt.Parameters.Add("$created", SqliteType.Text);
                    for (var i = 0; i < completedCount; i++)
                    {
                        key.Value = "k" + i.ToString(CultureInfo.InvariantCulture);
                        status.Value = "completed";
                        result.Value = payload;
                        // 让 created_utc 有先后顺序，便于断言保留的是最近的一批。
                        created.Value = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc)
                            .AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
                        await insertReceipt.ExecuteNonQueryAsync();
                    }

                    for (var i = 0; i < 3; i++)
                    {
                        key.Value = "prepared-" + i.ToString(CultureInfo.InvariantCulture);
                        status.Value = "prepared";
                        result.Value = DBNull.Value;
                        created.Value = "2020-01-01T00:00:00.0000000Z";
                        await insertReceipt.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
            }

            var sizeBefore = new FileInfo(DbPath(dataDir)).Length;

            // 升级到 v19。
            var upgraded = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(DatabaseMigrations.All), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 19, $"schema_version={upgraded.Store.Info.SchemaVersion}");
            await upgraded.Store.DisposeAsync();

            Assert.Equal(10_000, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM event_records"));
            Assert.Equal(2_000, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM request_receipts WHERE status = 'completed'"));

            // prepared 是未定态意图，即使 created_utc 是 2020 年也必须原样保留。
            Assert.Equal(3, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM request_receipts WHERE status = 'prepared'"));

            // 保留的是序号最大的一批事件（丢弃最旧的 500 条）。
            Assert.Equal(501, await ScalarAsync(dataDir, "SELECT MIN(sequence) FROM event_records"));
            Assert.Equal(eventCount, await ScalarAsync(dataDir, "SELECT MAX(sequence) FROM event_records"));

            // VACUUM 生效：文件确实变小，而不只是把页标记为可复用。
            var sizeAfter = new FileInfo(DbPath(dataDir)).Length;
            Assert.True(
                sizeAfter < sizeBefore,
                $"VACUUM 未回收磁盘：before={sizeBefore} after={sizeAfter}");

            foreach (var expected in ExpectedIndexes)
            {
                Assert.Contains(expected, await IndexNamesAsync(dataDir));
            }
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task PruneExpired_DeletesOnlyExpiredCompletedReceipts()
    {
        var dataDir = FreshDataDir("v19-prune");
        try
        {
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(DatabaseMigrations.All), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            await seeded.Store!.DisposeAsync();

            var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
            await using var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False");
            await connection.OpenAsync();

            await InsertReceiptAsync(connection, "expired-completed", "completed", now.AddDays(-40));
            await InsertReceiptAsync(connection, "fresh-completed", "completed", now.AddDays(-1));
            await InsertReceiptAsync(connection, "ancient-prepared", "prepared", now.AddDays(-400));

            var deleted = RequestReceiptStore.PruneExpired(connection, now);

            Assert.Equal(1, deleted);
            Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM request_receipts"));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM request_receipts WHERE status = 'prepared'"));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM request_receipts WHERE idempotency_key = 'fresh-completed'"));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    private static async Task InsertReceiptAsync(SqliteConnection connection, string key, string status, DateTime createdUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO request_receipts (library_instance_id, actor, operation_id, idempotency_key, " +
            "request_digest, status, attempt_json, result_json, created_utc, updated_utc) " +
            "VALUES ('inst', 'ui', 'games.update', $key, 'd', $status, NULL, NULL, $created, $created)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlySet<string>> IndexNamesAsync(string dataDir)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ScalarAsync(string dataDir, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void Cleanup(string dataDir)
    {
        try
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
