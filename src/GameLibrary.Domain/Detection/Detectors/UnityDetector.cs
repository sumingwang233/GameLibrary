namespace GameLibrary.Domain.Detection.Detectors;

/// <summary>
/// Unity 检测（策划案 5.4）：UnityPlayer.dll、成对 Foo.exe+Foo_Data/、globalgamemanagers
/// 为正向证据；GameAssembly.dll 记 IL2CPP 上下文证据；CrashHandler 仅上下文且永不成为入口。
/// </summary>
public sealed class UnityDetector : IEngineDetector
{
    private const int Version = 1;

    public EngineId Engine => EngineId.Unity;

    public int DetectorVersion => Version;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var evidence = new List<DetectionEvidence>();
        var rootDirs = snapshot.ListDirectories("");
        var rootFiles = snapshot.ListFiles("");

        var hasUnityPlayer = snapshot.FileExists("UnityPlayer.dll");
        if (hasUnityPlayer)
        {
            evidence.Add(new DetectionEvidence("unity.player-dll", "UnityPlayer.dll", EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        var dataDir = rootDirs.FirstOrDefault(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase));
        if (dataDir is not null)
        {
            evidence.Add(new DetectionEvidence("unity.data-dir", dataDir, EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        if (dataDir is not null)
        {
            var ggmPath = Join(dataDir, "globalgamemanagers");
            var ggm = snapshot.FileExists(ggmPath);
            evidence.Add(new DetectionEvidence(
                "unity.globalgamemanagers",
                ggmPath,
                ggm ? EvidenceObservation.Present : EvidenceObservation.Absent,
                ggm ? EvidencePolarity.Positive : EvidencePolarity.Negative));
        }

        if (snapshot.FileExists("GameAssembly.dll"))
        {
            evidence.Add(new DetectionEvidence("unity.il2cpp", "GameAssembly.dll", EvidenceObservation.Present, EvidencePolarity.Contextual, "IL2CPP 补充证据"));
        }

        foreach (var file in rootFiles)
        {
            if (file.Contains("crashhandler", StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(new DetectionEvidence("unity.crashhandler", file, EvidenceObservation.Present, EvidencePolarity.Contextual, "崩溃处理器不是入口"));
            }
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        var candidates = new List<EntryCandidate>();
        var exes = rootFiles.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var exe in exes)
        {
            if (EntryScoring.IsExcludedEntryName(exe))
            {
                continue;
            }

            var reasons = new List<string>();
            var score = 0;
            var baseName = exe[..^".exe".Length];
            if (dataDir is not null
                && string.Equals(dataDir[..^"_Data".Length], baseName, StringComparison.OrdinalIgnoreCase))
            {
                score += EntryScoring.PairedDataBonus;
                reasons.Add("与 Foo_Data 同名配对 +50");
            }

            if (hasUnityPlayer || (dataDir is not null && snapshot.FileExists(Join(dataDir, "globalgamemanagers"))))
            {
                score += EntryScoring.EngineStructureBonus;
                reasons.Add("Unity 结构证据 +30");
            }

            score += EntryScoring.GameExeNameBonusFor(exe);
            if (EntryScoring.GameExeNameBonusFor(exe) > 0)
            {
                reasons.Add("Game.exe 名称 +5");
            }

            candidates.Add(new EntryCandidate(exe, score, reasons));
        }

        var paired = dataDir is not null
            && exes.Any(exe => string.Equals(
                dataDir[..^"_Data".Length],
                exe[..^".exe".Length],
                StringComparison.OrdinalIgnoreCase));
        var structure = hasUnityPlayer
            || (dataDir is not null && snapshot.FileExists(Join(dataDir, "globalgamemanagers")));

        var confidence = paired && structure
            ? DetectionConfidence.High
            : structure || paired
                ? DetectionConfidence.Medium
                : DetectionConfidence.Low;

        return new DetectionResult
        {
            Engine = EngineId.Unity,
            DetectorVersion = Version,
            Confidence = confidence,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.Ordinal).ToArray(),
        };
    }

    private static string Join(string a, string b) => $"{a}/{b}";
}
