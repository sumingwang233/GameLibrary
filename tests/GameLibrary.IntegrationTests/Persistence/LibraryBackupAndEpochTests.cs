using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>备份 API 与 dataEpoch 更换（T10-C）。</summary>
public sealed class LibraryBackupAndEpochTests
{
    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options() =>
        new() { AppVersion = "0.1.0-dev", ApiVersion = "1" };

    [Fact]
    public async Task CreateBackup_ProducesReadableSnapshotWithIdentity()
    {
        var dataDir = FreshDataDir("backup-api");
        try
        {
            var store = (await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None)).Store!;
            var backupPath = Path.Combine(dataDir, "backups", "manual-test.db");

            await store.CreateBackupAsync(backupPath, CancellationToken.None);

            Assert.True(File.Exists(backupPath));
            await using var probe = new SqliteConnection($"Data Source={backupPath};Pooling=False");
            await probe.OpenAsync();
            await using var command = probe.CreateCommand();
            command.CommandText = "SELECT library_instance_id, data_epoch FROM schema_info WHERE id = 1";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(store.Info.LibraryInstanceId, reader.GetString(0));
            Assert.Equal(store.Info.DataEpoch, reader.GetString(1));
            await store.DisposeAsync();
        }
        finally
        {
            TryCleanup(dataDir);
        }
    }

    [Fact]
    public async Task CreateBackup_ToExistingTarget_IsRejected()
    {
        var dataDir = FreshDataDir("backup-exists");
        try
        {
            var store = (await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None)).Store!;
            var backupPath = Path.Combine(dataDir, "backups", "manual.db");
            await store.CreateBackupAsync(backupPath, CancellationToken.None);

            await Assert.ThrowsAsync<IOException>(
                () => store.CreateBackupAsync(backupPath, CancellationToken.None));
            await store.DisposeAsync();
        }
        finally
        {
            TryCleanup(dataDir);
        }
    }

    [Fact]
    public async Task RenewDataEpoch_PersistsNewEpochAcrossReopen()
    {
        var dataDir = FreshDataDir("epoch-renew");
        try
        {
            var store = (await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None)).Store!;
            var oldEpoch = store.Info.DataEpoch;

            var newEpoch = await store.RenewDataEpochAsync(CancellationToken.None);
            await store.DisposeAsync();

            Assert.NotEqual(oldEpoch, newEpoch);
            Assert.Equal(newEpoch, store.Info.DataEpoch);

            var reopened = (await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None)).Store!;
            Assert.Equal(newEpoch, reopened.Info.DataEpoch);
            await reopened.DisposeAsync();
        }
        finally
        {
            TryCleanup(dataDir);
        }
    }

    private static void TryCleanup(string dataDir)
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
