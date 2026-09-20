using System.Globalization;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v20 迁移：game_fingerprints 独立表落地（纯 DDL）、v19 库升级保数据、
/// 硬删 games 行时指纹行级联清理（测试直连必须显式 PRAGMA foreign_keys=ON）。
/// </summary>
public sealed class SchemaV20Tests
{
    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options(DatabaseMigration[] migrations) =>
        new() { AppVersion = "0.1.0-dev", ApiVersion = "1", Migrations = migrations };

    private static string DbPath(string dataDir) => Path.Combine(dataDir, "library.db");

    [Fact]
    public async Task FreshLibrary_CreatesGameFingerprintsTable()
    {
        var dataDir = FreshDataDir("v20-fresh");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(result.IsOpened, result.Detail);
            Assert.True(result.Store!.Info.SchemaVersion >= 20, $"schema_version={result.Store.Info.SchemaVersion}");
            await result.Store.DisposeAsync();

            var tables = await ScalarAsync(dataDir,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'game_fingerprints'");
            Assert.Equal(1L, tables);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task UpgradeFromV19_KeepsGameData_FingerprintTableEmpty_FkCheckPasses()
    {
        var dataDir = FreshDataDir("v20-upgrade");
        try
        {
            // 建一个停在 v19 的库。
            var v19 = DatabaseMigrations.All.Take(19).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v19), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            Assert.Equal(19, seeded.Store!.Info.SchemaVersion);
            await seeded.Store.DisposeAsync();

            // 灌入既有 games 行（升级后必须原样保留）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO games (game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc) " +
                    "VALUES ('game-legacy1', '旧游戏', 'D:\\games\\legacy1', 'directory', 'kirikiri', NULL, 'active', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z')";
                await insert.ExecuteNonQueryAsync();
            }

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 20, $"schema_version={upgraded.Store.Info.SchemaVersion}");

            // 升级后指纹表存在但为空；games 数据原样。
            Assert.Equal(0, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM game_fingerprints"));
            Assert.Equal(1, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM games WHERE game_id = 'game-legacy1'"));

            // 外键核查：升级路径内置 PRAGMA foreign_key_check 已通过（打开成功即无违规）；
            // 直连复核一次（显式开外键才可验证级联语义）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync();
                await using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await check.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync(), "外键核查不应有违规行");
            }

            await upgraded.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task HardDeleteGame_CascadesFingerprintRow()
    {
        var dataDir = FreshDataDir("v20-cascade");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            var store = init.Store!;

            // 经真实存储路径建游戏 + 指纹行。
            var utcNow = DateTime.Parse("2026-09-20T00:00:00.0000000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            store.InsertGame(new GameCard
            {
                GameId = "game-cascade1",
                Title = "级联游戏",
                RootPath = @"D:\games\cascade1",
                Kind = "directory",
                Engine = "kirikiri",
                Membership = "active",
                AcceptedUtc = utcNow,
                UpdatedUtc = utcNow,
            });
            store.UpsertGameFingerprint("game-cascade1", new GameFingerprintData(
                1, """[{"relativeKey":"Game.exe","sizeBytes":3,"sha256":"abc"}]""", utcNow));
            Assert.NotNull(store.TryGetGameFingerprint("game-cascade1"));
            await store.DisposeAsync();

            // 直连硬删 games 行：级联清理指纹行。
            // 注意：SqliteLibraryStore 每连接 PRAGMA foreign_keys=ON，但测试直连不会继承——
            // 必须显式开启外键，否则级联不生效（SQLite 默认外键关闭）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync();

                await using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM games WHERE game_id = 'game-cascade1'";
                await delete.ExecuteNonQueryAsync();
            }

            Assert.Equal(0, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM game_fingerprints"));
        }
        finally
        {
            Cleanup(dataDir);
        }
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
