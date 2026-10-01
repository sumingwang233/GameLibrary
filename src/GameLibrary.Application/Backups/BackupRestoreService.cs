using GameLibrary.Domain.Backups;

namespace GameLibrary.Application.Backups;

public interface IRestoreStorage
{
    Task PrepareAsync();
    Task SwapAsync();
    Task<RestoreResult> ValidateAsync();
    void Commit(RestoreResult result);
    Task RollbackAsync();
}

public static class BackupRestoreService
{
    public static async Task<RestoreResult> RestoreAsync(IRestoreStorage storage)
    {
        try
        {
            await storage.PrepareAsync();
            await storage.SwapAsync();
            var result = await storage.ValidateAsync();
            storage.Commit(result);
            return result;
        }
        catch
        {
            await storage.RollbackAsync();
            throw;
        }
    }
}
