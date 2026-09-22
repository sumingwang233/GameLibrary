using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v22 迁移（feat-3）：tags 新增 category/sort_order/starred/display_name 四列，
/// 存量按 kind 回填（engine→engine、user→special）；tags 被 game_tags 外键引用，
/// 重建必须保住关联行（连接 PRAGMA foreign_keys=ON，直接 DROP 会级联清空）。
/// </summary>
public sealed class SchemaV22Tests
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
    public async Task FreshLibrary_TagsTableHasNewColumnsWithDefaults()
    {
        var dataDir = FreshDataDir("v22-fresh");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(result.IsOpened, result.Detail);
            Assert.True(result.Store!.Info.SchemaVersion >= 22, $"schema_version={result.Store.Info.SchemaVersion}");
            await result.Store.DisposeAsync();

            // 四列存在（默认值语义由升级测试断言：空库无行可查）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var info = connection.CreateCommand();
                info.CommandText = "SELECT name FROM pragma_table_info('tags')";
                var columns = new List<string>();
                await using (var reader = await info.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(0));
                    }
                }

                Assert.Contains("category", columns);
                Assert.Contains("sort_order", columns);
                Assert.Contains("starred", columns);
                Assert.Contains("display_name", columns);
            }
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task UpgradeFromV21_BackfillsCategory_AndPreservesGameTags()
    {
        var dataDir = FreshDataDir("v22-upgrade");
        try
        {
            // 建一个停在 v21 的库，灌入 games/tags/game_tags 行（升级后必须原样保留关联）。
            var v21 = DatabaseMigrations.All.Take(21).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v21), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            Assert.Equal(21, seeded.Store!.Info.SchemaVersion);
            await seeded.Store.DisposeAsync();

            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var batch = connection.CreateCommand();
                batch.CommandText = """
                    INSERT INTO games (game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc)
                    VALUES ('game-v22a', '游戏甲', 'D:\games\v22a', 'directory', 'kirikiri', NULL, 'active', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                           ('game-v22b', '游戏乙', 'D:\games\v22b', 'directory', 'kirikiri', NULL, 'active', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO tags (tag_id, kind, name, color, revision, created_utc, updated_utc)
                    VALUES ('tag-v22-engine', 'engine', 'kirikiri', NULL, 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                           ('tag-v22-user', 'user', '我的收藏', '#66C0F4', 3, '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z');
                    INSERT INTO game_tags (game_id, tag_id, created_utc)
                    VALUES ('game-v22a', 'tag-v22-engine', '2026-01-01T00:00:00.0000000Z'),
                           ('game-v22a', 'tag-v22-user', '2026-01-01T00:00:00.0000000Z'),
                           ('game-v22b', 'tag-v22-engine', '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO tag_overrides (game_id, tag_kind, tag_name, action, created_utc)
                    VALUES ('game-v22b', 'engine', 'kirikiri', 'suppress', '2026-01-01T00:00:00.0000000Z');
                    """;
                await batch.ExecuteNonQueryAsync();
            }

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 22, $"schema_version={upgraded.Store.Info.SchemaVersion}");

            // category 按 kind 回填：engine→engine、user→special；其余三列取默认。
            Assert.Equal("engine", await ScalarTextAsync(dataDir, "SELECT category FROM tags WHERE tag_id = 'tag-v22-engine'"));
            Assert.Equal("special", await ScalarTextAsync(dataDir, "SELECT category FROM tags WHERE tag_id = 'tag-v22-user'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT sort_order FROM tags WHERE tag_id = 'tag-v22-user'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT starred FROM tags WHERE tag_id = 'tag-v22-user'"));
            Assert.Equal(0L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM tags WHERE display_name IS NOT NULL"));

            // game_tags 关联行原样保留（外键级联未吞数据）；tag_overrides 未受影响。
            Assert.Equal(3L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM game_tags"));
            Assert.Equal(1L, await ScalarAsync(dataDir,
                "SELECT COUNT(*) FROM tag_overrides WHERE game_id = 'game-v22b' AND tag_name = 'kirikiri' AND action = 'suppress'"));

            // 随 DROP 消失的 idx_game_tags_tag 已重建。
            Assert.Equal(1L, await ScalarAsync(dataDir,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_game_tags_tag'"));

            // 原修订保留；升级后经真实存储路径可更新新字段。
            Assert.Equal(3L, await ScalarAsync(dataDir, "SELECT revision FROM tags WHERE tag_id = 'tag-v22-user'"));
            var newRevision = upgraded.Store.UpdateTag(
                "tag-v22-user", name: null, color: null, category: "gameplay", sortOrder: 5,
                starred: 4, displayName: "我的·收藏", clearDisplayName: false,
                expectedRevision: 3, DateTime.UtcNow);
            Assert.NotNull(newRevision);
            var updated = upgraded.Store.TryGetTagByName("user", "我的收藏");
            Assert.NotNull(updated);
            Assert.Equal("gameplay", updated!.Category);
            Assert.Equal(5, updated.SortOrder);
            Assert.Equal(4, updated.Starred);
            Assert.Equal("我的·收藏", updated.DisplayName);

            // 升级后扫描自动建 engine 标签 category='engine'。
            Assert.True(upgraded.Store.EnsureEngineTagAssigned("game-v22a", "rmvxace", DateTime.UtcNow));
            Assert.Equal("engine", upgraded.Store.TryGetTagByName("engine", "rmvxace")!.Category);

            // 外键核查：打开成功即内置核查通过；直连复核一次。
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

    private static async Task<long> ScalarAsync(string dataDir, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarTextAsync(string dataDir, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
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
