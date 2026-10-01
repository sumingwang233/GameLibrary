using GameLibrary.Domain.Backups;

namespace GameLibrary.Application.Backups;

public interface IBackupCreateStorage
{
    Task CreateSnapshotAsync(CancellationToken cancellationToken);
    int CopyAssets();
    BackupCreationResult CommitManifest(int assetCount);
}

public static class BackupCreateService
{
    public static async Task<BackupCreationResult> CreateAsync(IBackupCreateStorage storage, CancellationToken cancellationToken)
    {
        await storage.CreateSnapshotAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var count = storage.CopyAssets();
        cancellationToken.ThrowIfCancellationRequested();
        return storage.CommitManifest(count);
    }
}
