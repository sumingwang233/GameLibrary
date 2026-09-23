using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v21 迁移（bug-1）：library_views 重建放宽 sort CHECK 为与 games.list 一致的六值；
/// v20 库升级保数据；非法值仍被数据库 CHECK 拒绝。
/// </summary>
public sealed class SchemaV21Tests
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
    public async Task FreshLibrary_AcceptsExtendedSortValues()
    {
        var dataDir = FreshDataDir("v21-fresh");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(result.IsOpened, result.Detail);
            Assert.True(result.Store!.Info.SchemaVersion >= 21, $"schema_version={result.Store.Info.SchemaVersion}");

            // 六值经真实存储路径全部可写（CHECK 放宽）。
            var utcNow = DateTime.UtcNow;
            foreach (var sort in new[] { "title", "title-asc", "title-desc", "recent", "updated-desc", "accepted-desc" })
            {
                result.Store.InsertView(new LibraryView
                {
                    ViewId = $"view-v21-{sort}",
                    Name = $"视图 {sort}",
                    Sort = sort,
                    CreatedUtc = utcNow,
                    UpdatedUtc = utcNow,
                });
            }

            result.Store.InsertView(new LibraryView
            {
                ViewId = "view-v21-tag-filter",
                Name = "标签筛选",
                TagId = "tag-custom",
                Sort = "accepted-desc",
                CreatedUtc = utcNow,
                UpdatedUtc = utcNow,
            });
            Assert.Equal("tag-custom", result.Store.TryGetView("view-v21-tag-filter")!.TagId);

            await result.Store.DisposeAsync();

            // 白名单之外仍被数据库拒绝（CHECK 仍在，只是放宽到六值）。
            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO library_views (view_id, name, filter_json, sort, revision, created_utc, updated_utc) " +
                    "VALUES ('view-v21-bad', '坏排序', '{}', 'random', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z')";
                await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
            }
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task UpgradeFromV20_KeepsViewRows_AndRelaxesCheck()
    {
        var dataDir = FreshDataDir("v21-upgrade");
        try
        {
            // 建一个停在 v20 的库，灌入既有视图行（升级后必须原样保留）。
            var v20 = DatabaseMigrations.All.Take(20).ToArray();
            var seeded = await SqliteLibraryStore.InitializeAsync(dataDir, Options(v20), CancellationToken.None);
            Assert.True(seeded.IsOpened, seeded.Detail);
            await seeded.Store!.DisposeAsync();

            await using (var connection = new SqliteConnection($"Data Source={DbPath(dataDir)};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO library_views (view_id, name, filter_json, sort, revision, created_utc, updated_utc) " +
                    "VALUES ('view-legacy', '旧视图', '{\"search\":\"Test\",\"favoriteOnly\":true}', 'recent', 4, '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z')";
                await insert.ExecuteNonQueryAsync();
            }

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options([.. DatabaseMigrations.All]), CancellationToken.None);
            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.True(upgraded.Store!.Info.SchemaVersion >= 21, $"schema_version={upgraded.Store.Info.SchemaVersion}");

            // 数据原样保留（filter/revision 不丢）。
            var view = upgraded.Store.TryGetView("view-legacy");
            Assert.NotNull(view);
            Assert.Equal("recent", view!.Sort);
            Assert.Equal(4, view.Revision);
            Assert.Equal("Test", view.Search);
            Assert.True(view.FavoriteOnly);

            // 升级后可写扩展排序值。
            var utcNow = DateTime.UtcNow;
            upgraded.Store.InsertView(new LibraryView
            {
                ViewId = "view-v21-accepted",
                Name = "收藏夹视图",
                Sort = "accepted-desc",
                CreatedUtc = utcNow,
                UpdatedUtc = utcNow,
            });
            Assert.Equal("accepted-desc", upgraded.Store.TryGetView("view-v21-accepted")!.Sort);

            await upgraded.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
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
