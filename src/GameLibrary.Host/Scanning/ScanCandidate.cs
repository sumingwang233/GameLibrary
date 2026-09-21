using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// T05-B 扫描候选（宿主内存态；入库/审核状态机与持久化随 T11 进入）。
/// 已知引擎证据产生 GameRoot；未知引擎的直属 EXE/LNK 产生保守待审核候选；根级独立
/// EXE/SWF 产生 FileGame。发现可启动游戏后停止遍历其子树；≥2 个直属
/// GameRoot 的父目录补充诊断用 Container（不进入待添加列表），双 high 记 EngineConflict。
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

    /// <summary>内存观察态；持久层按扫描触发类型决定 observed 或 pendingReview。</summary>
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
    private readonly List<ScanCandidate> _candidates = [];

    /// <summary>本次作业发现的全部候选（落库与查询用）。</summary>
    public IReadOnlyList<ScanCandidate> Candidates => _candidates;

    public int CandidateCount => _candidates.Count;

    public bool ShouldDescend(string directoryPath) =>
        !_byPath.TryGetValue(directoryPath, out var candidate)
        || candidate.EntryCandidates.Count == 0
        || candidate.Kind is not (CandidateKind.GameRoot or CandidateKind.Unknown or CandidateKind.NestedCandidate);

    public ScanCandidateCollector(GamePath root, string jobId, CandidateRegistry registry)
    {
        _root = root;
        _jobId = jobId;
        _registry = registry;
    }

    /// <summary>复用 Walker 已完成的直属枚举做只读识别，避免每个检测器重新枚举目录。</summary>
    public void InspectDirectory(ScannedDirectory directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var validation = GamePath.TryCreate(directory.PhysicalPath);
        if (!validation.IsValid)
        {
            return;
        }

        var snapshot = new FileSystemDirectorySnapshot(validation.Path!, directory.Directories, directory.Files);
        var report = Detectors.DetectAll(snapshot);
        var confirmed = report.Results
            .Where(r => r.Confidence >= DetectionConfidence.Medium && r.LikelyRootRelativePaths.Count > 0)
            .ToArray();
        if (confirmed.Length > 0)
        {
            if (confirmed.Length == 1
                && confirmed[0].Engine == EngineId.Flash
                && confirmed[0].EntryCandidates.Count > 0)
            {
                foreach (var entry in confirmed[0].EntryCandidates)
                {
                    AddCandidate(BuildFileCandidate(validation.Path!, entry, confirmed, confirmed[0].Evidence));
                }

                return;
            }

            var kind = IsInsideConfirmedRoot(directory.PhysicalPath)
                ? CandidateKind.NestedCandidate
                : CandidateKind.GameRoot;
            var candidate = BuildCandidate(validation.Path!, kind, confirmed, report.Conflict is not null);
            AddCandidate(candidate);
            if (kind == CandidateKind.GameRoot)
            {
                var parent = Path.GetDirectoryName(directory.PhysicalPath) ?? "";
                _gameRootChildren[parent] = _gameRootChildren.GetValueOrDefault(parent) + 1;
            }

            return;
        }

        var generic = GenericGameCandidateDetector.Inspect(snapshot);
        if (!generic.IsCandidate || IsInsidePlayableRoot(directory.PhysicalPath))
        {
            return;
        }

        if (directory.Depth == 0)
        {
            foreach (var entry in generic.EntryCandidates)
            {
                AddCandidate(BuildFileCandidate(validation.Path!, entry, [], generic.Evidence));
            }

            return;
        }

        AddCandidate(BuildCandidate(
            validation.Path!,
            CandidateKind.Unknown,
            engines: [],
            engineConflict: false,
            evidenceOverride: generic.Evidence,
            entriesOverride: generic.EntryCandidates));
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
            AddCandidate(candidate);
        }
    }

    private ScanCandidate BuildCandidate(
        GamePath directory,
        CandidateKind kind,
        IReadOnlyList<DetectionResult> engines,
        bool engineConflict,
        int? directGameRootChildren = null,
        IReadOnlyList<DetectionEvidence>? evidenceOverride = null,
        IReadOnlyList<EntryCandidate>? entriesOverride = null)
    {
        var evidence = evidenceOverride ?? engines.SelectMany(r => r.Evidence).ToArray();
        var entries = entriesOverride ?? engines
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

    private ScanCandidate BuildFileCandidate(
        GamePath directory,
        EntryCandidate entry,
        IReadOnlyList<DetectionResult> engines,
        IReadOnlyList<DetectionEvidence> evidence)
    {
        var physicalPath = Path.Combine(
            directory.PhysicalPath,
            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var filePath = GamePath.Create(physicalPath);
        var entryEvidence = evidence
            .Where(item => string.Equals(item.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new ScanCandidate
        {
            CandidateId = $"cand-{Guid.NewGuid():N}",
            JobId = _jobId,
            ScanRoot = _root.PhysicalPath,
            PhysicalPath = filePath.PhysicalPath,
            RelativePath = RelativeToRoot(filePath.PhysicalPath),
            Kind = CandidateKind.FileGame,
            Engines = engines
                .Select(result => new CandidateEngine(result.Engine, result.DetectorVersion, result.Confidence))
                .OrderBy(result => result.Engine)
                .ToArray(),
            EngineConflict = false,
            Evidence = entryEvidence,
            EntryCandidates = [entry],
            Classification = Classification.Classify(AncestorSegments(directory)),
            ObservedUtc = DateTime.UtcNow,
        };
    }

    private void AddCandidate(ScanCandidate candidate)
    {
        _byPath[candidate.PhysicalPath] = candidate;
        _registry.Add(candidate);
        _candidates.Add(candidate);
    }

    private bool IsInsideConfirmedRoot(string directoryPhysicalPath) =>
        _byPath.Values.Any(c =>
            c.Kind == CandidateKind.GameRoot
            && directoryPhysicalPath.Length > c.PhysicalPath.Length
            && directoryPhysicalPath.StartsWith(c.PhysicalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private bool IsInsidePlayableRoot(string directoryPhysicalPath) =>
        _byPath.Values.Any(candidate =>
            (candidate.Kind is CandidateKind.GameRoot or CandidateKind.Unknown)
            && directoryPhysicalPath.Length > candidate.PhysicalPath.Length
            && directoryPhysicalPath.StartsWith(
                candidate.PhysicalPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

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
