using GameLibrary.Application.Backups;
using GameLibrary.Domain.Backups;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Infrastructure.Backups;

public sealed class LibraryBackupStorage(SqliteLibraryStore store, string dataDirectory, string backupId) : IBackupCreateStorage
{
    private readonly string _directory = BackupArchive.BackupDirectory(Path.Combine(dataDirectory, "backups"), backupId);

    public async Task CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        await store.CreateBackupAsync(Path.Combine(_directory, BackupArchive.DatabaseFileName), cancellationToken);
    }

    public int CopyAssets()
    {
        var source = Path.Combine(dataDirectory, "assets");
        return Directory.Exists(source) ? BackupArchive.CopyDirectory(source, Path.Combine(_directory, "assets")) : 0;
    }

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
