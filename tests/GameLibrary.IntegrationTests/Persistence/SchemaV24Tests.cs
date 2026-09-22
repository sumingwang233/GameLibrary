using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v24 迁移（v1.5.2 星级评分）：tags.starred CHECK 从 IN (0,1) 放宽为 IN (0..5)，
/// 全部列值原样保留（与 v22 不同，本次不重置任何字段）；game_tags 外键引用 tags，
/// 重建必须保住关联行。starred=5 可写、6 被 CHECK 拒绝、存量 1 自然成为 1 星。
/// </summary>
public sealed class SchemaV24Tests
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
    public async Task UpgradeFromV23_PreservesAllTagValues_AndAllowsRatingUpTo5()
    {
        var dataDir = FreshDataDir("v24-upgrade");
        try
        {
            // 建一个停在 v23 的库，灌入带全字段值的标签行与关联行（升级后必须原样保留）。
            var v23 = DatabaseMigrations.All.Take(23).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v23), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            Assert.Equal(23, seeded.Store!.Info.SchemaVersion);
            await seeded.Store.DisposeAsync();

            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var batch = connection.CreateCommand();
                batch.CommandText = """
                    INSERT INTO games (game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc)
                    VALUES ('game-v24a', '游戏甲', 'D:\games\v24a', 'directory', 'kirikiri', NULL, 'active', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO tags (tag_id, kind, name, color, category, sort_order, starred, display_name, revision, created_utc, updated_utc)
                    VALUES ('tag-v24-user', 'user', '我的收藏', '#66C0F4', 'gameplay', 7, 1, '收藏·甲', 4, '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z');
                    INSERT INTO game_tags (game_id, tag_id, created_utc)
                    VALUES ('game-v24a', 'tag-v24-user', '2026-01-01T00:00:00.0000000Z');
                    """;
                await batch.ExecuteNonQueryAsync();
            }

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 24, $"schema_version={upgraded.Store.Info.SchemaVersion}");

            // 全部列值原样保留（含存量 starred=1 → 自然成为 1 星）。
            Assert.Equal(7L, await ScalarAsync(dataDir, "SELECT sort_order FROM tags WHERE tag_id = 'tag-v24-user'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT starred FROM tags WHERE tag_id = 'tag-v24-user'"));
            Assert.Equal(4L, await ScalarAsync(dataDir, "SELECT revision FROM tags WHERE tag_id = 'tag-v24-user'"));
            Assert.Equal(1L, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM game_tags WHERE tag_id = 'tag-v24-user'"));

            // 存储路径更新星级 0–5 均可写；保留其余字段。
            var newRevision = upgraded.Store.UpdateTag(
                "tag-v24-user", name: null, color: null, category: null, sortOrder: null,
                starred: 5, displayName: null, clearDisplayName: false,
                expectedRevision: 4, DateTime.UtcNow);
            Assert.NotNull(newRevision);
            var updated = upgraded.Store.TryGetTagByName("user", "我的收藏");
            Assert.NotNull(updated);
            Assert.Equal(5, updated!.Starred);
            Assert.Equal("gameplay", updated.Category);
            Assert.Equal(7, updated.SortOrder);
            Assert.Equal("收藏·甲", updated.DisplayName);

            // 越界值 6 被 CHECK 拒绝（直连验证约束本体）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var bad = connection.CreateCommand();
                bad.CommandText = "UPDATE tags SET starred = 6 WHERE tag_id = 'tag-v24-user'";
                await Assert.ThrowsAsync<SqliteException>(() => bad.ExecuteNonQueryAsync());
            }

            // 外键核查：连接 PRAGMA foreign_keys=ON 下无违规行。
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

    private static void Cleanup(string dataDir)
    {
        try { Directory.Delete(dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
