using GameLibrary.Domain.Catalog;

namespace GameLibrary.Application.Catalog;

public interface IGameCatalogStore
{
    T InTransaction<T>(Func<T> action);
    GameCard? TryGetGameByRootPath(string rootPath);
    void InsertGame(GameCard game);
    int? SetGameField(string gameId, string fieldKey, string? value, string source, int expectedRevision, DateTime utcNow);
    int? RelinkGame(string gameId, string newRootPath, int expectedRevision, DateTime utcNow);
    void UpsertGameFingerprint(string gameId, GameFingerprintData fingerprint);
}

public sealed class GameCatalogService(IGameCatalogStore store, ICatalogFiles files)
{
    public GameCard Create(GameCard game, bool titleOverride)
    {
        var fingerprint = files.Fingerprint(game.RootPath, game.EntryPath, game.Engine, game.UpdatedUtc);
        return store.InTransaction(() =>
        {
            if (store.TryGetGameByRootPath(game.RootPath) is { Membership: "active" })
                throw new InvalidOperationException("该游戏位置已入库");
            store.InsertGame(game);
            if (titleOverride)
                game = game with { Revision = store.SetGameField(game.GameId, "title", game.Title, "user", 1, game.UpdatedUtc)!.Value };
            if (fingerprint is not null) store.UpsertGameFingerprint(game.GameId, fingerprint);
            return game;
        });
    }

    public int? Relink(GameCard game, string rootPath, int revision)
    {
        var now = DateTime.UtcNow;
        var entry = RebaseEntry(game.RootPath, game.EntryPath, rootPath);
        var fingerprint = files.Fingerprint(rootPath, entry, game.Engine, now);
        return store.InTransaction(() =>
        {
            if (store.TryGetGameByRootPath(rootPath) is { } other && other.GameId != game.GameId)
                throw new InvalidOperationException("新路径已绑定到其他游戏");
            var updated = store.RelinkGame(game.GameId, rootPath, revision, now);
            if (updated is not null && fingerprint is not null) store.UpsertGameFingerprint(game.GameId, fingerprint);
            return updated;
        });
    }

    private static string? RebaseEntry(string oldRootPath, string? entryPath, string newRootPath)
    {
        if (entryPath is null) return null;
        var oldRoot = Path.GetFullPath(oldRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entry = Path.GetFullPath(entryPath);
        return entry.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(newRootPath, Path.GetRelativePath(oldRoot, entry))) : entryPath;
    }
}
