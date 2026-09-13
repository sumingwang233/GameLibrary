namespace GameLibrary.Domain.Detection.Detectors;

/// <summary>
/// RPG Maker MV/MZ 检测（策划案 5.4）：rpg_core.js（MV）/ rmmz_core.js（MZ）+
/// data/System.json；System.json 不可读是 incomplete（置信度封顶 medium），不是反证。
/// </summary>
public sealed class RpgMakerMvMzDetector : IEngineDetector
{
    private const int Version = 1;

    public EngineId Engine => EngineId.RpgMakerMvMz;

    public int DetectorVersion => Version;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var evidence = new List<DetectionEvidence>();

        var coreCandidates = new[]
        {
            "www/js/rpg_core.js",
            "js/rpg_core.js",
            "www/js/rmmz_core.js",
            "js/rmmz_core.js",
        };
        var coreFound = coreCandidates.FirstOrDefault(snapshot.FileExists);
        if (coreFound is not null)
        {
            evidence.Add(new DetectionEvidence("rpgmv.core-js", coreFound, EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        var systemCandidates = new[] { "www/data/System.json", "data/System.json" };
        EvidenceObservation? systemObservation = null;
        var systemPath = "";
        foreach (var candidate in systemCandidates)
        {
            if (snapshot.FileExists(candidate))
            {
                systemPath = candidate;
                // 存在性即可作证据；标题等字段读取属元数据阶段（T14）。
                systemObservation = EvidenceObservation.Present;
                break;
            }
        }

        if (systemObservation is not null)
        {
            evidence.Add(new DetectionEvidence("rpgmv.system-json", systemPath, systemObservation.Value, EvidencePolarity.Positive));
        }

        if (snapshot.FileExists("package.json") || snapshot.FileExists("www/package.json"))
        {
            evidence.Add(new DetectionEvidence("rpgmv.nwjs-package", "package.json", EvidenceObservation.Present, EvidencePolarity.Contextual, "NW.js 壳线索；不代表可执行其 scripts"));
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        var confidence = (coreFound, systemObservation) switch
        {
            { coreFound: not null, systemObservation: EvidenceObservation.Present } => DetectionConfidence.High,
            { coreFound: not null } => DetectionConfidence.Medium,
            _ => DetectionConfidence.Low,
        };

        var candidates = new List<EntryCandidate>();
        foreach (var exe in snapshot.ListFiles("").Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (EntryScoring.IsExcludedEntryName(exe))
            {
                continue;
            }

            var score = EntryScoring.EngineStructureBonus + EntryScoring.GameExeNameBonusFor(exe);
            var reasons = new List<string> { "RPG Maker 结构证据 +30" };
            if (EntryScoring.GameExeNameBonusFor(exe) > 0)
            {
                reasons.Add("Game.exe 名称 +5");
            }

            candidates.Add(new EntryCandidate(exe, score, reasons));
        }

        return new DetectionResult
        {
            Engine = EngineId.RpgMakerMvMz,
            DetectorVersion = Version,
            Confidence = confidence,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.Ordinal).ToArray(),
        };
    }
}

/// <summary>Ren'Py 检测：renpy/ + game/ 配对；game/ 下 .rpy 明文或 .rpa 归档即可（不要求 .rpa）。</summary>
public sealed class RenpyDetector : IEngineDetector
{
    private const int Version = 1;

    public EngineId Engine => EngineId.Renpy;

    public int DetectorVersion => Version;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var evidence = new List<DetectionEvidence>();

        var hasRenpyDir = snapshot.DirectoryExists("renpy");
        var hasGameDir = snapshot.DirectoryExists("game");
        if (hasRenpyDir)
        {
            evidence.Add(new DetectionEvidence("renpy.dir", "renpy", EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        if (hasGameDir)
        {
            evidence.Add(new DetectionEvidence("renpy.game-dir", "game", EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        var gameFiles = hasGameDir ? snapshot.ListFiles("game") : [];
        var scriptFile = gameFiles.FirstOrDefault(f =>
            f.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rpa", StringComparison.OrdinalIgnoreCase));
        if (scriptFile is not null)
        {
            evidence.Add(new DetectionEvidence("renpy.script", $"game/{scriptFile}", EvidenceObservation.Present, EvidencePolarity.Positive));
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        var confidence = hasRenpyDir && hasGameDir && scriptFile is not null
            ? DetectionConfidence.High
            : hasRenpyDir && hasGameDir
                ? DetectionConfidence.Medium
                : DetectionConfidence.Low;

        // 只选择顶层发行入口；renpy/ 内部 exe 不是游戏入口。
        var candidates = snapshot.ListFiles("")
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !EntryScoring.IsExcludedEntryName(f))
            .Select(exe => new EntryCandidate(
                exe,
                EntryScoring.EngineStructureBonus + EntryScoring.GameExeNameBonusFor(exe),
                ["Ren'Py 结构证据 +30"]))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new DetectionResult
        {
            Engine = EngineId.Renpy,
            DetectorVersion = Version,
            Confidence = confidence,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = candidates,
        };
    }
}

/// <summary>Kirikiri 检测：根级 .xp3 只支持引擎判断；+EXE 配对才 high（不推断 krkr2/z 注入 DLL）。</summary>
public sealed class KirikiriDetector : IEngineDetector
{
    private const int Version = 1;

    public EngineId Engine => EngineId.Kirikiri;

    public int DetectorVersion => Version;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var xp3Files = snapshot.ListFiles("")
            .Where(f => f.EndsWith(".xp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (xp3Files.Count == 0)
        {
            return null;
        }

        var evidence = xp3Files
            .Select(f => new DetectionEvidence("krkr.xp3", f, EvidenceObservation.Present, EvidencePolarity.Positive))
            .ToList();

        var exes = snapshot.ListFiles("")
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !EntryScoring.IsExcludedEntryName(f))
            .ToList();
        var hasExe = exes.Count > 0;

        var candidates = exes
            .Select(exe => new EntryCandidate(
                exe,
                EntryScoring.EngineStructureBonus + EntryScoring.GameExeNameBonusFor(exe),
                ["XP3 配对 +30"]))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new DetectionResult
        {
            Engine = EngineId.Kirikiri,
            DetectorVersion = Version,
            Confidence = hasExe ? DetectionConfidence.High : DetectionConfidence.Medium,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = candidates,
        };
    }
}

/// <summary>Flash 检测：SWF 头 FWS/CWS/ZWS（读取前 3 字节）；损坏头为负向证据，不是入口。</summary>
public sealed class FlashDetector : IEngineDetector
{
    private const int Version = 1;

    public EngineId Engine => EngineId.Flash;

    public int DetectorVersion => Version;

    public DetectionResult? Detect(IDirectorySnapshot snapshot)
    {
        var swfFiles = snapshot.ListFiles("")
            .Where(f => f.EndsWith(".swf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (swfFiles.Count == 0)
        {
            return null;
        }

        var evidence = new List<DetectionEvidence>();
        var candidates = new List<EntryCandidate>();
        var validCount = 0;

        foreach (var swf in swfFiles)
        {
            var probe = snapshot.TryReadFirstBytes(swf, 3);
            var signature = probe.Kind == FileProbeKind.Ok && probe.Bytes!.Length >= 3
                ? System.Text.Encoding.ASCII.GetString(probe.Bytes, 0, 3)
                : null;

            if (signature is "FWS" or "CWS" or "ZWS")
            {
                validCount++;
                evidence.Add(new DetectionEvidence("flash.swf-header", swf, EvidenceObservation.Present, EvidencePolarity.Positive, $"头 {signature}"));
                // 每个 SWF 是独立的文件型游戏入口（策划案 5.3）。
                candidates.Add(new EntryCandidate(swf, EntryScoring.EngineStructureBonus, ["有效 SWF 头 +30"]));
            }
            else if (probe.Kind == FileProbeKind.Unreadable)
            {
                evidence.Add(new DetectionEvidence("flash.swf-header", swf, EvidenceObservation.Unreadable, EvidencePolarity.Contextual));
            }
            else
            {
                evidence.Add(new DetectionEvidence(
                    "flash.swf-header",
                    swf,
                    EvidenceObservation.Present,
                    EvidencePolarity.Negative,
                    signature is null ? "空文件" : $"非法头 {signature}"));
            }
        }

        return new DetectionResult
        {
            Engine = EngineId.Flash,
            DetectorVersion = Version,
            Confidence = validCount > 0 ? DetectionConfidence.High : DetectionConfidence.Low,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.Ordinal).ToArray(),
        };
    }
}
