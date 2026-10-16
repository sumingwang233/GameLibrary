using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Domain.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Hosting;

internal sealed class GameEngineRecognition(Func<SqliteLibraryStore?> storeAccessor, RootRegistry roots, EventStream events)
{
    private readonly object _gate = new();
    private string? _scope;
    private string _afterGameId = "";

    public static string? Detect(string rootPath, string? entryPath)
    {
        try
        {
            var directory = Directory.Exists(rootPath) ? rootPath : Path.GetDirectoryName(entryPath ?? rootPath);
            if (directory is null || !Directory.Exists(directory)) return null;
            for (var path = directory; path is not null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var root = GamePath.TryCreate(directory);
            if (!root.IsValid) return null;
            var report = EngineDetectorSet.Default.DetectAll(new FileSystemDirectorySnapshot(root.Path!));
            // 多种高置信证据交给目录审核，不能任取第一个引擎。
            var found = report.Results.Where(result => result.Confidence == DetectionConfidence.High
                && (entryPath is null || result.EntryCandidates.Any(entry =>
                    Path.GetFullPath(Path.Combine(directory, entry.RelativePath)).Equals(entryPath, StringComparison.OrdinalIgnoreCase)))).ToArray();
            return found.Length == 1 ? JsonSerializer.SerializeToElement(found[0].Engine, ContractJson.Options).GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public bool Refresh(string gameId)
    {
        var store = storeAccessor();
        if (store?.TryGetGame(gameId) is not { Membership: "active" } game || !IsUnknown(game.Engine)
            || !roots.Contains(game.RootPath) || game.EntryPath is { } entry && !roots.Contains(entry)) return false;
        var engine = Detect(game.RootPath, game.EntryPath);
        if (engine is null || !store.SetUnknownGameEngine(gameId, engine, game.Revision, DateTime.UtcNow)) return false;
        events.Publish("game.updated", $"game:{gameId}", new { gameId, engine }, DateTime.UtcNow);
        events.Publish("tag.assigned", $"game:{gameId}", new { gameId, engine }, DateTime.UtcNow);
        return true;
    }

    public IReadOnlyList<string> Reconcile()
    {
        // ponytail: 每次最多检测 20 个未知游戏，每个库会话一轮；未来需要重试离线磁盘时再增加显式重识别操作。
        lock (_gate)
        {
            var store = storeAccessor();
            if (store is null) return [];
            var changed = new List<string>();
            var scope = store.Info.LibraryInstanceId + ":" + store.Info.DataEpoch;
            if (_scope != scope) { _scope = scope; _afterGameId = ""; }
            foreach (var game in store.ListUnknownEngineGames(_afterGameId))
            {
                if (Refresh(game.GameId)) changed.Add(game.GameId);
                _afterGameId = game.GameId;
            }
            return changed;
        }
    }

    private static bool IsUnknown(string? engine) => string.IsNullOrWhiteSpace(engine) || engine == "unknown";
}
