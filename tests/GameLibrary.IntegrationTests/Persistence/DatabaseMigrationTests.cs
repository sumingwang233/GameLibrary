using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>迁移框架（T10-B）：单事务逐步升级、迁移前快照、失败不半升级、连续版本校验。</summary>
public sealed class DatabaseMigrationTests
{
    private static readonly DatabaseMigration V1 = DatabaseMigrations.All[0];

    private static readonly DatabaseMigration V2 = new(
        2,
        "CREATE TABLE events_test (id INTEGER PRIMARY KEY, payload TEXT NOT NULL)");

    private static readonly DatabaseMigration V2Broken = new(
        2,
        "THIS IS NOT VALID SQL AT ALL");

    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options(params DatabaseMigration[] migrations) =>
        new() { AppVersion = "0.1.0-dev", ApiVersion = "1", Migrations = migrations };

    [Fact]
    public async Task Open_OlderLibrary_MigratesStepwiseAndKeepsIdentity()
    {
        var dataDir = FreshDataDir("mig-upgrade");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(V1), CancellationToken.None);
            var initInfo = init.Store!.Info;
            await init.Store.DisposeAsync();

            var upgraded = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(V1, V2), CancellationToken.None);

            Assert.True(upgraded.IsOpened, upgraded.Detail);
            Assert.Equal(2, upgraded.Store!.Info.SchemaVersion);
            Assert.Equal(initInfo.LibraryInstanceId, upgraded.Store.Info.LibraryInstanceId);
            Assert.Equal(0, await ScalarAsync(dataDir, "SELECT COUNT(*) FROM events_test"));
            Assert.Equal(
                2,
                await ScalarAsync(dataDir, "SELECT schema_version FROM schema_info"));

            var snapshot = Directory.GetFiles(Path.Combine(dataDir, "backups"), "pre-migration-v1-to-v2-*.db");
            Assert.Single(snapshot);
            await upgraded.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Open_BrokenMigration_KeepsOldVersionAndLeavesSnapshot()
    {
        var dataDir = FreshDataDir("mig-broken");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(V1), CancellationToken.None);
            await init.Store!.DisposeAsync();

            var result = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options(V1, V2Broken), CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.MigrationFailed, result.Status);
            Assert.Contains("快照", result.Detail);
            Assert.Equal(
                1,
                await ScalarAsync(dataDir, "SELECT schema_version FROM schema_info"));

            var snapshot = Directory.GetFiles(Path.Combine(dataDir, "backups"), "pre-migration-*.db");
            Assert.Single(snapshot);

            // 旧结构仍完整可读：随后用仅 v1 的旧程序语义可以继续打开。
            var reopen = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(V1), CancellationToken.None);
            Assert.True(reopen.IsOpened, reopen.Detail);
            await reopen.Store!.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Initialize_WithVersionGap_IsRejected()
    {
        var dataDir = FreshDataDir("mig-gap");
        try
        {
            var gap = new[] { V1, new DatabaseMigration(3, "CREATE TABLE x (id INTEGER)") };

            await Assert.ThrowsAsync<ArgumentException>(
                () => SqliteLibraryStore.InitializeAsync(dataDir, Options(gap), CancellationToken.None));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Snapshot_IsReadableAndContainsIdentity()
    {
        var dataDir = FreshDataDir("mig-snapshot");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(V1), CancellationToken.None);
            var instanceId = init.Store!.Info.LibraryInstanceId;
            await init.Store.DisposeAsync();

            var upgraded = await SqliteLibraryStore.TryOpenAsync(
                dataDir, Options(V1, V2), CancellationToken.None);
            Assert.True(upgraded.IsOpened);
            await upgraded.Store!.DisposeAsync();

            var snapshotPath = Directory.GetFiles(Path.Combine(dataDir, "backups"), "pre-migration-*.db")[0];
            await using var probe = new SqliteConnection($"Data Source={snapshotPath};Pooling=False");
            await probe.OpenAsync();
            await using var command = probe.CreateCommand();
            command.CommandText = "SELECT library_instance_id, schema_version FROM schema_info WHERE id = 1";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(instanceId, reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    private static async Task<long> ScalarAsync(string dataDir, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDir, "library.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void Cleanup(string dataDir)
    {
        // 测试产物在 gitignored artifacts/test-runs 唯一 guid 目录下；
        // 清理尽力而为，句柄被测试进程外延迟释放时不判测试失败。
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
