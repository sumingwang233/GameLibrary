namespace GameLibrary.Domain.Detection;

/// <summary>
/// 入口评分共享规则（策划案 5.5）：同名配对 +50、引擎结构 +30、Game.exe 名称 +5；
/// 卸载器/安装器/崩溃处理器等永远不成为候选。分数只是规则内推荐线索。
/// </summary>
public static class EntryScoring
{
    /// <summary>与引擎数据同名（Foo.exe + Foo_Data/）。</summary>
    public const int PairedDataBonus = 50;

    /// <summary>有对应引擎结构。</summary>
    public const int EngineStructureBonus = 30;

    /// <summary>文件名恰好是 Game.exe。</summary>
    public const int GameExeNameBonus = 5;

    private static readonly string[] ExcludedFragments =
    [
        "uninstall", "unins", "setup", "install", "crashhandler", "configurator", "redist",
    ];

    public static bool IsExcludedEntryName(string fileName) =>
        ExcludedFragments.Any(fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static int GameExeNameBonusFor(string fileName) =>
        fileName.Equals("Game.exe", StringComparison.OrdinalIgnoreCase) ? GameExeNameBonus : 0;

    /// <summary>是否满足预选条件：≥80 且比第二名高 ≥25。</summary>
    public static bool MeetsPrescore(EntryCandidate top, EntryCandidate? second) =>
        top.Score >= EntryCandidate.PrescoreThreshold
        && (second is null || top.Score - second.Score >= EntryCandidate.PrescoreMargin);
}

/// <summary>检测器集合：先按 Engine 枚举值规范化顺序再运行，输出与注册顺序无关。</summary>
public sealed class EngineDetectorSet
{
    private readonly IReadOnlyList<IEngineDetector> _detectors;

    public EngineDetectorSet(IEnumerable<IEngineDetector> detectors)
    {
        _detectors = detectors.OrderBy(d => (int)d.Engine).ToArray();
    }

    public IReadOnlyList<IEngineDetector> Detectors => _detectors;

    public DetectionReport DetectAll(IDirectorySnapshot snapshot)
    {
        var results = _detectors
            .Select(detector => detector.Detect(snapshot))
            .OfType<DetectionResult>()
            .OrderBy(result => (int)result.Engine)
            .ToArray();

        var highConflicts = results.Where(r => r.Confidence == DetectionConfidence.High).ToArray();
        EngineConflict? conflict = highConflicts.Length >= 2 ? new EngineConflict(highConflicts) : null;

        return new DetectionReport(results, conflict);
    }
}
