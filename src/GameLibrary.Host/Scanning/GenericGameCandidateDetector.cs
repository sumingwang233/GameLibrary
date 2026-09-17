using GameLibrary.Domain.Detection;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// Conservative fallback discovery for games whose engine is not one of the versioned detectors.
/// It only inspects direct entries and produces review candidates; it never claims an engine.
/// </summary>
public sealed record GenericCandidateFinding(
    IReadOnlyList<DetectionEvidence> Evidence,
    IReadOnlyList<EntryCandidate> EntryCandidates)
{
    public bool IsCandidate => Evidence.Count > 0;
}

public static class GenericGameCandidateDetector
{
    private const int MaxDirectEntries = 256;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bepinex",
        "commonredist",
        "directx",
        "dotnet",
        "drivers",
        "installer",
        "installers",
        "monobleedingedge",
        "redist",
        "redistributable",
        "runtime",
        "runtimes",
        "sdk",
        "support",
        "tool",
        "tools",
    };

    private static readonly string[] ExcludedExecutableFragments =
    [
        "crashpad",
        "crashreport",
        "dxsetup",
        "notification_helper",
        "unitybugreporter",
        "vc_redist",
        "vcredist",
    ];

    public static GenericCandidateFinding Inspect(IDirectorySnapshot snapshot)
    {
        if (IsExcludedDirectory(snapshot.RootPhysicalPath))
        {
            return new GenericCandidateFinding([], []);
        }

        var files = snapshot.ListFiles("");
        var executables = files
            .Where(file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(IsPlausibleExecutable)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Take(MaxDirectEntries)
            .ToArray();
        var shortcuts = files
            .Where(file => file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Take(Math.Max(0, MaxDirectEntries - executables.Length))
            .ToArray();

        var evidence = executables
            .Select(file => new DetectionEvidence(
                "generic.executable",
                file,
                EvidenceObservation.Present,
                EvidencePolarity.Contextual,
                "发现未识别引擎的直属可执行文件；需要用户审核"))
            .Concat(shortcuts.Select(file => new DetectionEvidence(
                "generic.shortcut",
                file,
                EvidenceObservation.Present,
                EvidencePolarity.Contextual,
                "快捷方式仅作为发现线索；接受前仍需验证目标")))
            .ToArray();

        var entries = executables
            .Select(file => new EntryCandidate(
                file,
                20 + EntryScoring.GameExeNameBonusFor(file),
                ["未识别引擎的直属 EXE；需要用户审核"]))
            .Concat(shortcuts.Select(file => new EntryCandidate(
                file,
                0,
                ["快捷方式目标尚未验证；仅作为待审核线索"])))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new GenericCandidateFinding(evidence, entries);
    }

    private static bool IsPlausibleExecutable(string fileName) =>
        !EntryScoring.IsExcludedEntryName(fileName)
        && !ExcludedExecutableFragments.Any(fragment =>
            fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsExcludedDirectory(string physicalPath)
    {
        var name = Path.GetFileName(physicalPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var normalized = (name ?? "")
            .Trim('[', ']', ' ', '_', '-')
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        return ExcludedDirectoryNames.Contains(normalized);
    }
}
