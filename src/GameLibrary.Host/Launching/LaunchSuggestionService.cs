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
                try { await DiscoverAsync(null, _stop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.Coordinator?.OnBackgroundError?.Invoke(ex);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    });

    public async Task DiscoverAsync(string? gameId, CancellationToken token)
    {
        await _worker.WaitAsync(token);
        try
        {
            string[] ids;
            GameLibrary.Domain.Catalog.GameCard[] games;
            using (var lease = state.Sessions.TryEnter(background: true))
            {
                if (lease is null) return;
                if (_generation != state.ConnectionGeneration) { _seen.Clear(); _generation = state.ConnectionGeneration; }
                games = state.Library.Store?.ListGames().Where(g => g.Membership == "active").ToArray() ?? [];
                ids = gameId is null ? games.Select(g => g.GameId).ToArray() : [gameId];
            }
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                using var lease = state.Sessions.TryEnter(background: true);
                if (lease is null || _generation != state.ConnectionGeneration) return;
                var game = state.Library.Store?.TryGetGame(id);
                if (game is not { Membership: "active" }) continue;
                if (gameId is null && _seen.TryGetValue(id, out var previous) && previous == (game.RootPath, game.Revision)) continue;
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
                    var ownFiles = files.Where(file => !games.Any(other => other.GameId != id
                        && (string.Equals(other.EntryPath, file, StringComparison.OrdinalIgnoreCase)
                            || (other.Kind is "manualFile" or "fileGame" && string.Equals(other.RootPath, file, StringComparison.OrdinalIgnoreCase))))).ToArray();
                    var snapshot = new FileSystemDirectorySnapshot(path.Path!, directories, ownFiles);
                    state.Launches.AddSuggestions(id, directory, LaunchSuggestionDetector.Detect(snapshot));
                    _seen[id] = (game.RootPath, game.Revision);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                // Yield between entries so a large library does not monopolize request processing.
                await Task.Yield();
            }
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
