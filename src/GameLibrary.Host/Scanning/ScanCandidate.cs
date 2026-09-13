using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// T05-B 扫描候选（宿主内存态；入库/审核状态机与持久化随 T11 进入）。
/// 一个有 ≥Medium 引擎证据的目录产生一个候选；Kind 依 5.3 编排：确认根内的独立证据为
/// NestedCandidate，≥2 个直属 GameRoot 的父目录补充 Container，双 high 记 EngineConflict 不取先注册。
/// </summary>
public sealed record ScanCandidate
{
    public required string CandidateId { get; init; }

    public required string JobId { get; init; }

    public required string ScanRoot { get; init; }

    public required string PhysicalPath { get; init; }

    /// <summary>相对扫描根、'/' 分隔；扫描根本身为 ""。</summary>
    public required string RelativePath { get; init; }

    public required CandidateKind Kind { get; init; }

    public required IReadOnlyList<CandidateEngine> Engines { get; init; }

    public bool EngineConflict { get; init; }

    /// <summary>直接隶属本容器候选的 GameRoot 子候选数（仅 Container 有值）。</summary>
    public int? DirectGameRootChildren { get; init; }

    public DetectionConfidence? Confidence => Engines.Count == 0
        ? null
        : Engines.Max(e => e.Confidence);

    public required IReadOnlyList<DetectionEvidence> Evidence { get; init; }

    public required IReadOnlyList<EntryCandidate> EntryCandidates { get; init; }

    public required FolderClassification Classification { get; init; }

    /// <summary>扫描新发现的候选一律从 Observed 开始；accept/defer/ignore 随 T11。</summary>
    public CandidateReviewState ReviewState => CandidateReviewState.Observed;

    public required DateTime ObservedUtc { get; init; }

    public object ToListItem() => new
    {
        candidateId = CandidateId,
        jobId = JobId,
        kind = Kind,
        relativePath = RelativePath,
        engines = Engines.Select(e => new { engine = e.Engine, confidence = e.Confidence }).ToArray(),
        engineConflict = EngineConflict,
        confidence = Confidence,
        reviewState = ReviewState,
        observedUtc = ObservedUtc.ToString("O"),
    };

    public object ToDetail() => new
    {
        candidateId = CandidateId,
        jobId = JobId,
        scanRoot = ScanRoot,
        physicalPath = PhysicalPath,
        relativePath = RelativePath,
        kind = Kind,
        engines = Engines.Select(e => new
        {
            engine = e.Engine,
            detectorVersion = e.DetectorVersion,
            confidence = e.Confidence,
        }).ToArray(),
        engineConflict = EngineConflict,
        directGameRootChildren = DirectGameRootChildren,
        confidence = Confidence,
        evidence = Evidence.Select(e => new
        {
            ruleId = e.RuleId,
            relativePath = e.RelativePath,
            observation = e.Observation,
            polarity = e.Polarity,
            detail = e.Detail,
        }).ToArray(),
        entryCandidates = EntryCandidates.Select(e => new
        {
            relativePath = e.RelativePath,
            score = e.Score,
            reasons = e.Reasons,
        }).ToArray(),
        classification = new
        {
            tags = Classification.Tags.Select(t => new
            {
                kind = t.Kind,
                canonicalValue = t.CanonicalValue,
                sourceSegment = t.SourceSegment,
            }).ToArray(),
            requiredByToolNeed = Classification.RequiredByToolNeed,
            toolNeedSourceSegment = Classification.ToolNeedSourceSegment,
            rulesVersion = Classification.RulesVersion,
        },
        reviewState = ReviewState,
        observedUtc = ObservedUtc.ToString("O"),
    };
}

/// <summary>候选上的单引擎判定（含检测器规则版本）。</summary>
public sealed record CandidateEngine(EngineId Engine, int DetectorVersion, DetectionConfidence Confidence);

/// <summary>宿主内候选注册表：扫描作业写入，candidates.list/get 查询（宿主生命周期内有效）。</summary>
public sealed class CandidateRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScanCandidate> _candidates = new(StringComparer.Ordinal);

    public void Add(ScanCandidate candidate) => _candidates[candidate.CandidateId] = candidate;

    public ScanCandidate? Get(string candidateId) =>
        _candidates.TryGetValue(candidateId, out var candidate) ? candidate : null;

    public IReadOnlyList<ScanCandidate> List(string? jobId = null) =>
        _candidates.Values
            .Where(c => jobId is null || string.Equals(c.JobId, jobId, StringComparison.Ordinal))
            .OrderBy(c => c.ObservedUtc)
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>单个扫描作业的候选编排器：目录级检测 + 容器归并（策划案 5.3）。</summary>
public sealed class ScanCandidateCollector
{
    private static readonly EngineDetectorSet Detectors = new(
    [
        new Domain.Detection.Detectors.UnityDetector(),
        new Domain.Detection.Detectors.RpgMakerMvMzDetector(),
        new Domain.Detection.Detectors.RenpyDetector(),
        new Domain.Detection.Detectors.KirikiriDetector(),
        new Domain.Detection.Detectors.FlashDetector(),
    ]);

    private static readonly ClassificationRules Classification = new();

    private readonly GamePath _root;
    private readonly string _jobId;
    private readonly CandidateRegistry _registry;
    private readonly Dictionary<string, ScanCandidate> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gameRootChildren = new(StringComparer.OrdinalIgnoreCase);

    public ScanCandidateCollector(GamePath root, string jobId, CandidateRegistry registry)
    {
        _root = root;
        _jobId = jobId;
        _registry = registry;
    }

    /// <summary>对枚举到的目录做只读识别；≥Medium 证据产生候选。</summary>
    public void InspectDirectory(string directoryPhysicalPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var validation = GamePath.TryCreate(directoryPhysicalPath);
        if (!validation.IsValid)
        {
            return;
        }

        var report = Detectors.DetectAll(new FileSystemDirectorySnapshot(validation.Path!));
        var confirmed = report.Results
            .Where(r => r.Confidence >= DetectionConfidence.Medium && r.LikelyRootRelativePaths.Count > 0)
            .ToArray();
        if (confirmed.Length == 0)
        {
            return;
        }

        var kind = IsInsideConfirmedRoot(directoryPhysicalPath)
            ? CandidateKind.NestedCandidate
            : CandidateKind.GameRoot;
        var candidate = BuildCandidate(validation.Path!, kind, confirmed, report.Conflict is not null);
        _byPath[candidate.PhysicalPath] = candidate;
        _registry.Add(candidate);
        if (kind == CandidateKind.GameRoot)
        {
            var parent = Path.GetDirectoryName(directoryPhysicalPath) ?? "";
            _gameRootChildren[parent] = _gameRootChildren.GetValueOrDefault(parent) + 1;
        }
    }

    /// <summary>遍历结束后归并容器：≥2 个直属 GameRoot 且自身无引擎证据的父目录。</summary>
    public void CompleteContainers()
    {
        foreach (var (parent, count) in _gameRootChildren)
        {
            if (count < 2 || _byPath.ContainsKey(parent))
            {
                continue;
            }

            var validation = GamePath.TryCreate(parent);
            if (!validation.IsValid)
            {
                continue;
            }

            var candidate = BuildCandidate(
                validation.Path!,
                CandidateKind.Container,
                engines: [],
                engineConflict: false,
                directGameRootChildren: count);
            _byPath[candidate.PhysicalPath] = candidate;
            _registry.Add(candidate);
        }
    }

    private ScanCandidate BuildCandidate(
        GamePath directory,
        CandidateKind kind,
        IReadOnlyList<DetectionResult> engines,
        bool engineConflict,
        int? directGameRootChildren = null)
    {
        var evidence = engines.SelectMany(r => r.Evidence).ToArray();
        var entries = engines
            .SelectMany(r => r.EntryCandidates)
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var ancestorSegments = AncestorSegments(directory);

        return new ScanCandidate
        {
            CandidateId = $"cand-{Guid.NewGuid():N}",
            JobId = _jobId,
            ScanRoot = _root.PhysicalPath,
            PhysicalPath = directory.PhysicalPath,
            RelativePath = RelativeToRoot(directory.PhysicalPath),
            Kind = kind,
            Engines = engines
                .Select(r => new CandidateEngine(r.Engine, r.DetectorVersion, r.Confidence))
                .OrderBy(e => e.Engine)
                .ToArray(),
            EngineConflict = engineConflict,
            DirectGameRootChildren = directGameRootChildren,
            Evidence = evidence,
            EntryCandidates = entries,
            Classification = Classification.Classify(ancestorSegments),
            ObservedUtc = DateTime.UtcNow,
        };
    }

    private bool IsInsideConfirmedRoot(string directoryPhysicalPath) =>
        _byPath.Values.Any(c =>
            c.Kind == CandidateKind.GameRoot
            && directoryPhysicalPath.Length > c.PhysicalPath.Length
            && directoryPhysicalPath.StartsWith(c.PhysicalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private string RelativeToRoot(string physicalPath)
    {
        var root = _root.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
        var path = physicalPath.TrimEnd(Path.DirectorySeparatorChar);
        if (path.Length <= root.Length)
        {
            return "";
        }

        return path[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private IReadOnlyList<string> AncestorSegments(GamePath directory)
    {
        var relative = RelativeToRoot(directory.PhysicalPath);
        if (relative.Length == 0)
        {
            return [];
        }

        var segments = relative.Split('/');
        return segments[..^1];
    }
}
