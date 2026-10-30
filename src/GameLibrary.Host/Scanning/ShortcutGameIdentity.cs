using GameLibrary.Domain.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Domain.Paths;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>只比较快捷方式与已验证入口；标题、目录前缀不能证明是同一游戏。</summary>
internal sealed class ShortcutGameIdentity
{
    private readonly IReadOnlyList<GameCard> _games;
    private readonly ILookup<string, GameCard> _entries;

    public ShortcutGameIdentity(IReadOnlyList<GameCard> games)
    {
        _games = games.Where(game => game.Membership == "active").ToArray();
        _entries = _games.Where(game => game.EntryPath is not null)
            .Select(game => (Game: game, Key: EntryKey(game.EntryPath!))).Where(item => item.Key is not null)
            .ToLookup(item => item.Key!, item => item.Game, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GameCard> FindMatches(IReadOnlyList<string> entries, bool importingShortcut = false)
    {
        var keys = entries.Select(EntryKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return [];
        return keys.SelectMany(key => _entries[key]).Where(game => importingShortcut || game.Kind == "manualShortcut")
            .Concat(importingShortcut ? _games.Where(game => game.EntryPath is null && keys.Any(key =>
                        string.Equals(Path.GetDirectoryName(key), game.RootPath, StringComparison.OrdinalIgnoreCase)
                        && IsUniqueDirectoryEntry(game.RootPath, key))) : [])
            .DistinctBy(game => game.GameId)
            .ToArray();
    }

    private static bool IsUniqueDirectoryEntry(string directory, string entry)
    {
        if (!IsSafeFile(entry)) return false;
        var snapshot = new FileSystemDirectorySnapshot(GamePath.Create(directory));
        var entries = GenericGameCandidateDetector.Inspect(snapshot).EntryCandidates;
        return entries.Count == 1 && string.Equals(Path.Combine(directory, entries[0].RelativePath), entry,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? EntryKey(string path)
    {
        var validation = GamePath.TryCreate(path);
        if (!validation.IsValid || !IsSafeFile(validation.Path!.PhysicalPath)) return null;
        var entry = validation.Path!.PhysicalPath;
        return UnrealWrapper(entry) ?? entry;
    }

    private static bool IsSafeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string? UnrealWrapper(string entry)
    {
        var platform = Path.GetDirectoryName(entry);
        if (platform is null || !Path.GetFileName(platform).Equals("Win64", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(platform).Equals("Win32", StringComparison.OrdinalIgnoreCase)) return null;
        var binaries = Path.GetDirectoryName(platform);
        if (!string.Equals(Path.GetFileName(binaries), "Binaries", StringComparison.OrdinalIgnoreCase)) return null;
        var project = Path.GetDirectoryName(binaries!);
        var root = project is null ? null : Path.GetDirectoryName(project);
        if (root is null || GamePath.Create(root).IsDriveRoot) return null;
        var projectName = Path.GetFileName(project!);
        if (!Path.GetFileName(entry).Equals(projectName + "-" + Path.GetFileName(platform) + "-Shipping.exe",
            StringComparison.OrdinalIgnoreCase)) return null;
        var wrapper = Path.Combine(root, projectName + ".exe");
        if (!IsSafeFile(wrapper)) return null;
        try
        {
            var snapshot = new FileSystemDirectorySnapshot(GamePath.Create(root));
            // 检测器最多检查 32 个直属目录；截断的观察不能用来证明项目唯一。
            if (snapshot.ListDirectories("").Count > 32) return null;
            var report = new AdditionalEngineDetector(EngineId.Unreal)
                .Detect(snapshot);
            return report is { EntryCandidates.Count: 1 }
                && report.Evidence.Any(e => e.RuleId == "unreal.packaged-project"
                    && string.Equals(e.RelativePath, projectName + "/Content/Paks", StringComparison.OrdinalIgnoreCase))
                && string.Equals(report.EntryCandidates[0].RelativePath, Path.GetFileName(wrapper), StringComparison.OrdinalIgnoreCase)
                    ? wrapper : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
