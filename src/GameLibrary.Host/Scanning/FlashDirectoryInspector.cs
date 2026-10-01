using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>复用 Walker 的系统目录/链接边界，只读收集格式证据；不运行播放器。</summary>
public static class FlashDirectoryInspector
{
    public static (DetectionResult? Result, bool Complete) Inspect(GamePath root, CancellationToken ct,
        bool includeDescendants = true)
    {
        try
        {
            if ((File.GetAttributes(root.PhysicalPath) & FileAttributes.ReparsePoint) != 0) return (null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, false); }
        var evidence = new List<DetectionEvidence>();
        var entries = new List<EntryCandidate>();
        var unreadable = false;
        var walker = new DirectoryWalker(root, new ScanRuleSet([]));
        var coverage = walker.Walk(_ => { }, null, ct, directory =>
        {
            if (GenericGameCandidateDetector.IsExcludedDirectory(directory.PhysicalPath)) return;
            var files = directory.Files.Where(path =>
            {
                try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable = true; return false; }
            }).ToArray();
            var snapshot = new FileSystemDirectorySnapshot(GamePath.Create(directory.PhysicalPath),
                directory.Directories, files);
            var result = new FlashDetector().Detect(snapshot);
            var prefix = Path.GetRelativePath(root.PhysicalPath, directory.PhysicalPath).Replace('\\', '/');
            string Relative(string path) => prefix == "." ? path : prefix + "/" + path;
            if (result is not null)
            {
                evidence.AddRange(result.Evidence.Select(item => item with { RelativePath = Relative(item.RelativePath) }));
                entries.AddRange(result.EntryCandidates.Select(item => item with { RelativePath = Relative(item.RelativePath) }));
            }
            if (prefix == ".")
                entries.AddRange(GenericGameCandidateDetector.Inspect(snapshot).EntryCandidates
                    .Where(item => LaunchSuggestionDetector.IsGameEntryName(item.RelativePath)));
        }, path => includeDescendants && !GenericGameCandidateDetector.IsExcludedDirectory(path));
        if (!evidence.Any(item => item.Polarity == EvidencePolarity.Positive)) return (null, false);
        return (new DetectionResult
        {
            Engine = EngineId.Flash,
            DetectorVersion = 2,
            Confidence = DetectionConfidence.High,
            Evidence = evidence,
            LikelyRootRelativePaths = [""],
            EntryCandidates = entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(),
        }, coverage.Completion == ScanCompletion.Complete && !unreadable
            && !evidence.Any(item => item.Observation == EvidenceObservation.Unreadable));
    }

    public static FlashDirectoryGroup Group(string directory, DetectionResult result, bool complete,
        IReadOnlyList<FlashDirectoryRule> rules)
    {
        var inventory = result.EntryCandidates.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray();
        var confirmed = rules.FirstOrDefault(rule =>
            string.Equals(rule.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase)
            && rule.Inventory.Order(StringComparer.Ordinal).SequenceEqual(inventory, StringComparer.Ordinal));
        var reasons = new List<string> { "有效 SWF 文件不等于独立完整游戏，请确认目录用途和入口" };
        if (inventory.Length > 1) reasons.Add("多个入口可能是项目资源，也可能是独立游戏合集");
        if (inventory.Any(path => Path.GetFileNameWithoutExtension(path).StartsWith("flashplayer", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("通用 Flash 播放器不是游戏身份依据");
        if (!complete) reasons.Add("目录读取不完整，请解决不可读分支后重新扫描");
        return new(directory, confirmed?.Kind ?? "unknown", confirmed is null || !complete,
            confirmed?.EntryPaths ?? [], inventory, complete, reasons);
    }
}
