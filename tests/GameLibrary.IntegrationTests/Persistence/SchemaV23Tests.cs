using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v23 迁移（bug-5）：library_roots 增加 kind 列（library|manual，存量 'library'）；
/// 治理存量冗余——删除“是另一 library 根的严格子路径”的 library 根
/// （大小写与分隔符归一后比较；substr 精确前缀，路径含 %/_ 通配符也不误判）；
/// 被删根下的游戏不受影响。待删集先物化快照，链式子根全部命中。
/// </summary>
public sealed class SchemaV23Tests
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
    public async Task FreshLibrary_RootsTableHasKindColumnWithLibraryDefault()
    {
        var dataDir = FreshDataDir("v23-fresh");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(result.IsOpened, result.Detail);
            Assert.True(result.Store!.Info.SchemaVersion >= 23, $"schema_version={result.Store.Info.SchemaVersion}");
            await result.Store.DisposeAsync();

            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var info = connection.CreateCommand();
                info.CommandText = "SELECT name FROM pragma_table_info('library_roots')";
                var columns = new List<string>();
                await using (var reader = await info.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(0));
                    }
                }

                Assert.Contains("kind", columns);
            }

            // ReadRoots 走真实存储路径：空表无行，写一行再读回验证 kind 序列化。
            var reopened = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(reopened.IsOpened, reopened.Detail);
            reopened.Store!.UpsertRoot(
                new PersistedRoot("root-v23-probe", @"C:\v23probe", 1, DateTime.UtcNow, "manual"), DateTime.UtcNow);
            Assert.Equal("manual", reopened.Store.ReadRoots().Single(r => r.RootId == "root-v23-probe").Kind);
            await reopened.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task UpgradeFromV22_PrunesStrictSubpathLibraryRootsAndKeepsGames()
    {
        var dataDir = FreshDataDir("v23-upgrade");
        try
        {
            // 建一个停在 v22 的库，灌入冗余根簇与根下游戏。
            var v22 = DatabaseMigrations.All.Take(22).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v22), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            Assert.Equal(22, seeded.Store!.Info.SchemaVersion);
            await seeded.Store.DisposeAsync();

            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var batch = connection.CreateCommand();
                batch.CommandText = """
                    INSERT INTO library_roots (root_id, physical_path, revision, created_utc) VALUES
                        ('root-drive',  'F:\',                    1, '2026-01-01T00:00:00.0000000Z'),
                        ('root-child',  'F:\Games',               1, '2026-01-02T00:00:00.0000000Z'),
                        ('root-deep',   'f:\games\Deep',          1, '2026-01-03T00:00:00.0000000Z'),
                        ('root-solo',   'D:\Lib',                 1, '2026-01-04T00:00:00.0000000Z'),
                        ('root-wild',   'D:\My%Games',            1, '2026-01-05T00:00:00.0000000Z'),
                        ('root-wildch', 'D:\My%Games\Child',      1, '2026-01-06T00:00:00.0000000Z'),
                        ('root-sameA',  'E:\Same',                1, '2026-01-07T00:00:00.0000000Z'),
                        ('root-sameB',  'e:\same/',               1, '2026-01-08T00:00:00.0000000Z'),
                        ('root-sibling','D:\Library',             1, '2026-01-09T00:00:00.0000000Z');
                    INSERT INTO games (game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc)
                    VALUES ('game-v23a', '冗余根下的游戏', 'F:\Games\SomeGame', 'manualDirectory', NULL, NULL, 'active', 1,
                            '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                           ('game-v23b', '盘根下的游戏', 'F:\OtherGame', 'directory', NULL, NULL, 'active', 1,
                            '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    """;
                await batch.ExecuteNonQueryAsync();
            }

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 23, $"schema_version={upgraded.Store.Info.SchemaVersion}");
            await upgraded.Store.DisposeAsync();

            // 严格子路径被删：F:\Games（⊂F:\）、f:\games\Deep（大小写归一后 ⊂F:\Games，链式命中）、
            // D:\My%Games\Child（⊂D:\My%Games，% 不是 LIKE 通配符——substr 精确前缀）。
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-child'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-deep'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-wildch'"));

            // 保留：盘根/独立根/通配符路径本体/互为同路径的两行（相等不是严格子路径，不删）/
            // 同级不同目录（Library 与 Lib 前缀不同：'d:\library' 不以 'd:\lib\' 开头）。
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-drive'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-solo'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-wild'"));
            Assert.Equal(2L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id IN ('root-sameA','root-sameB')"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE root_id = 'root-sibling'"));

            // 存量 kind 全部回填 'library'；被删根下的游戏不受影响（父根覆盖其路径）。
            Assert.Equal(6L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE kind = 'library'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM library_roots WHERE kind <> 'library'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM games WHERE game_id = 'game-v23a' AND membership = 'active'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM games WHERE game_id = 'game-v23b' AND membership = 'active'"));

            // 治理临时表已清理，不留迁移残渣。
            Assert.Equal(0L, await ScalarAsync(dataDir,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'library_roots_v23%'"));
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
