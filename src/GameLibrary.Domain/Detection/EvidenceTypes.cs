namespace GameLibrary.Domain.Detection;

/// <summary>首批五类引擎/格式（策划案 5.4 v1）；固定英文枚举值。</summary>
public enum EngineId
{
    Unity,

    RpgMakerMvMz,

    Renpy,

    Kirikiri,

    Flash,
}

/// <summary>证据观察态：不可读不等同缺失（补充规格 2.3）。</summary>
public enum EvidenceObservation
{
    Present,

    Absent,

    Unreadable,
}

/// <summary>证据极性：目录名等仅 contextual，不能覆盖文件证据。</summary>
public enum EvidencePolarity
{
    Positive,

    Negative,

    Contextual,
}

/// <summary>置信度：标识+配对组合且无强反证才 high；单个标识 medium/low。</summary>
public enum DetectionConfidence
{
    Low,

    Medium,

    High,
}

/// <summary>一条证据：规则、相对路径、观察态与极性。</summary>
public sealed record DetectionEvidence(
    string RuleId,
    string RelativePath,
    EvidenceObservation Observation,
    EvidencePolarity Polarity,
    string? Detail = null);

/// <summary>入口候选：相对路径、角色、规则内推荐分（策划案 5.5；是排序线索不是概率）。</summary>
public sealed record EntryCandidate(
    string RelativePath,
    int Score,
    IReadOnlyList<string> Reasons)
{
    /// <summary>推荐预选门槛：≥80 且比第二候选高 ≥25（策划案 5.5）。</summary>
    public const int PrescoreThreshold = 80;

    public const int PrescoreMargin = 25;
}

/// <summary>单个检测器对快照根的判定；LikelyRoots 为相对快照根的路径（"" 表示根本身）。</summary>
public sealed record DetectionResult
{
    public required EngineId Engine { get; init; }

    public required int DetectorVersion { get; init; }

    public required DetectionConfidence Confidence { get; init; }

    public required IReadOnlyList<DetectionEvidence> Evidence { get; init; }

    public required IReadOnlyList<string> LikelyRootRelativePaths { get; init; }

    public required IReadOnlyList<EntryCandidate> EntryCandidates { get; init; }
}

/// <summary>同根多个 high 判定：不按注册顺序取先到者，交边界解析（T04）。</summary>
public sealed record EngineConflict(IReadOnlyList<DetectionResult> ConflictingResults);

/// <summary>检测器集合的聚合输出：确定性排序（按 Engine 枚举值），与检测器注册顺序无关。</summary>
public sealed record DetectionReport(
    IReadOnlyList<DetectionResult> Results,
    EngineConflict? Conflict)
{
    public static readonly DetectionReport Empty = new([], null);
}
