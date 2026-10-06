using GameLibrary.Application.Backups;
using GameLibrary.Domain.Backups;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Infrastructure.Backups;

public sealed class LibraryBackupStorage(SqliteLibraryStore store, string dataDirectory, string backupId) : IBackupCreateStorage
{
    private readonly string _directory = BackupArchive.BackupDirectory(Path.Combine(dataDirectory, "backups"), backupId);
    private int _assetCount;

    public Task CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        store.WithWriteLock(() =>
        {
            Directory.CreateDirectory(_directory);
            store.CreateBackupAsync(Path.Combine(_directory, BackupArchive.DatabaseFileName), cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(dataDirectory, "assets");
            _assetCount = Directory.Exists(source) ? BackupArchive.CopyDirectory(source, Path.Combine(_directory, "assets")) : 0;
            return _assetCount;
        });
        return Task.CompletedTask;
    }

    public int CopyAssets() => _assetCount;

    public BackupCreationResult CommitManifest(int assetCount)
    {
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            LibraryInstanceId = store.Info.LibraryInstanceId,
            SourceDataEpoch = store.Info.DataEpoch,
            AppVersion = store.Info.AppVersion,
            SchemaVersion = store.Info.SchemaVersion,
            CreatedUtc = DateTime.UtcNow,
            Files = BackupArchive.EnumerateFiles(_directory),
        };
        BackupArchive.WriteManifest(_directory, manifest);
        return new(backupId, assetCount, manifest.Files.Count);
    }
}
