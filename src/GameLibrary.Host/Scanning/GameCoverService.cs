using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>封面随游戏携带；已有 cover 永不覆盖，库内仍保存独立副本。</summary>
public static class GameCoverService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private const long MaxBytes = 5 * 1024 * 1024;

    public static string? Synchronize(SqliteLibraryStore store, GameCard game)
    {
        if (game.Membership != "active" || DirectoryWalker.IsSystemDirectory(game.RootPath)) return null;
        var directory = game.Kind switch
        {
            "manualShortcut" => Path.GetDirectoryName(game.EntryPath),
            "manualFile" or "fileGame" => Path.GetDirectoryName(game.RootPath),
            _ => game.RootPath,
        };
        if (directory is null || !Directory.Exists(directory)) return null;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var files = Directory.EnumerateFiles(directory).ToArray();
            var existing = files.FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file).Equals("cover", StringComparison.OrdinalIgnoreCase));
            var asset = store.ListAssets(game.GameId).FirstOrDefault(item => item.IsCurrent);
            if (existing is not null)
            {
                // 已有用户封面优先；只有尚无库内封面的游戏才读取磁盘 cover。
                if (asset is not null || !Extensions.Contains(Path.GetExtension(existing).ToLowerInvariant())
                    || new FileInfo(existing).Length > MaxBytes
                    || (File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0) return null;
                var owned = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "assets", game.GameId);
                Directory.CreateDirectory(owned);
                var target = Path.Combine(owned, $"{Guid.NewGuid():N}{Path.GetExtension(existing)}");
                File.Copy(existing, target, overwrite: false);
                store.ImportAsset(game.GameId, target, DateTime.UtcNow);
            }
            else if (asset is not null && File.Exists(asset.FilePath))
            {
                var extension = Path.GetExtension(asset.FilePath).ToLowerInvariant();
                if (Extensions.Contains(extension) && new FileInfo(asset.FilePath).Length <= MaxBytes)
                {
                    // 先写临时文件再原子改名，避免中断留下半张 cover 并永久阻止补齐。
                    var temporary = Path.Combine(directory, $".gamelibrary-cover-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.Copy(asset.FilePath, temporary, overwrite: false);
                        File.Move(temporary, Path.Combine(directory, "cover" + extension), overwrite: false);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
            }
            return null;
        }
        catch (IOException ex) { return $"封面已保存在游戏库，但游戏目录封面同步失败：{ex.Message}"; }
        catch (UnauthorizedAccessException ex) { return $"无法写入或读取游戏目录封面：{ex.Message}"; }
    }
}
