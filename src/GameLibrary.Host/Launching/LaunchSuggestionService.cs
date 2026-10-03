using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Hosting;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Launching;

/// <summary>单后台队列回填；每个游戏持短会话租约，切库后重新读取当前库。</summary>
internal sealed class LaunchSuggestionService(HostRuntimeState state) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _worker = new(1);
    private readonly Dictionary<string, (string Path, int Revision)> _seen = new(StringComparer.Ordinal);
    private int _generation = -1;
    private Task? _task;

    public void Start() => _task = Task.Run(async () =>
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var version = state.Events.Version;
                var completed = false;
                try { completed = await DiscoverAsync(null, _stop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.Coordinator?.OnBackgroundError?.Invoke(ex);
                }
                await state.Events.WaitForChangeAsync(version, completed ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(1), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    });

    public async Task<bool> DiscoverAsync(string? gameId, CancellationToken token)
    {
        await _worker.WaitAsync(token);
        try
        {
            string[] ids;
            GameLibrary.Domain.Catalog.GameCard[] games;
            using (var lease = state.Sessions.TryEnter(background: true))
            {
                if (lease is null) return false;
                if (_generation != state.ConnectionGeneration) { _seen.Clear(); _generation = state.ConnectionGeneration; }
                games = state.Library.Store?.ListGames().Where(g => g.Membership == "active").ToArray() ?? [];
                ids = gameId is null ? games.Select(g => g.GameId).ToArray() : [gameId];
            }
            var snapshotById = games.ToDictionary(game => game.GameId, StringComparer.Ordinal);
            var knownEntries = games.SelectMany(game => new[]
                {
                    (Path: game.EntryPath, game.GameId),
                    (Path: game.Kind is "manualFile" or "fileGame" ? game.RootPath : null, game.GameId),
                }).Where(entry => entry.Path is not null)
                .ToLookup(entry => entry.Path!, entry => entry.GameId, StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                if (gameId is null && snapshotById.TryGetValue(id, out var snapshotGame)
                    && _seen.TryGetValue(id, out var previous) && previous == (snapshotGame.RootPath, snapshotGame.Revision)) continue;
                using var lease = state.Sessions.TryEnter(background: true);
                if (lease is null || _generation != state.ConnectionGeneration) return false;
                var game = state.Library.Store?.TryGetGame(id);
                if (game is not { Membership: "active" }) continue;
                var directory = game.Kind is "manualFile" or "fileGame" ? Path.GetDirectoryName(game.RootPath)
                    : game.Kind == "manualShortcut" ? Path.GetDirectoryName(game.EntryPath) : game.RootPath;
                if (directory is null || !Directory.Exists(directory) || !state.Roots.Contains(directory)) continue;
                try
                {
                    if (HasReparsePoint(directory)) continue;
                    var files = Directory.EnumerateFiles(directory).Take(257).ToArray();
                    if (files.Length > 256) continue;
                    var directories = Directory.EnumerateDirectories(directory).Take(257).ToArray();
                    if (directories.Length > 256 || files.Any(HasReparsePoint) || directories.Any(HasReparsePoint)) continue;
                    var path = GamePath.TryCreate(directory);
                    if (!path.IsValid) continue;
                    // Shared directories may hold separately registered file games. Their known
                    // entries belong to those games and must not influence this game's single-entry score.
                    var ownFiles = files.Where(file => !knownEntries[file].Any(otherId => otherId != id)).ToArray();
                    var snapshot = new FileSystemDirectorySnapshot(path.Path!, directories, ownFiles);
                    state.Launches.AddSuggestions(id, directory, LaunchSuggestionDetector.Detect(snapshot));
                    _seen[id] = (game.RootPath, game.Revision);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                // Yield between entries so a large library does not monopolize request processing.
                await Task.Yield();
            }
            return true;
        }
        finally { _worker.Release(); }
    }

    private static bool HasReparsePoint(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    public void Dispose() => _stop.Cancel();
    public Task Stopped => _task ?? Task.CompletedTask;
}
