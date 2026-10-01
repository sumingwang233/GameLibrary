namespace GameLibrary.Domain.Catalog;

/// <summary>落库候选（T11）：payload 保留完整检测证据；审核状态与 Revision 在库内演进。</summary>
public sealed record PersistedCandidate
{
    public required string CandidateId { get; init; }

    public string? JobId { get; init; }

    public required string Kind { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>规范化物理路径；唯一键（重扫刷新同一候选，不重复建卡）。</summary>
    public required string PhysicalPath { get; init; }

    public required string PayloadJson { get; init; }

    public required string ReviewState { get; init; }

    public int Revision { get; init; } = 1;

    public string? GameId { get; init; }

    public required DateTime ObservedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

/// <summary>游戏卡片（T11 最小集）：accept 创建；资料/封面/标签编辑随 T14。</summary>
public sealed record GameCard
{
    public required string GameId { get; init; }

    public required string Title { get; init; }

    public required string RootPath { get; init; }

    public required string Kind { get; init; }

    public string? Engine { get; init; }

    public string? EntryPath { get; init; }

    public required string Membership { get; init; }

    public bool Favorite { get; init; }

    /// <summary>祖先 [toolNeed] 继承的 Required（accept 时落库；T17 对账后更新）。</summary>
    public bool TranslationInherited { get; init; }

    /// <summary>用户覆盖：Auto/Required/NotRequired 或 null（=Auto 未覆盖）。继承值与覆盖值分离持久化。</summary>
    public string? TranslationOverride { get; init; }

    /// <summary>路径可用性（T17）：unknown/available/suspectedMissing/missing/offline/accessError/rootUnbound。扫描器维护，不占 Revision。</summary>
    public string Availability { get; init; } = "unknown";

    /// <summary>第一次确认缺失的时间（suspectedMissing/missing 期间非空）；ID-05 的 60 秒间隔判定依据。</summary>
    public DateTime? MissingSinceUtc { get; init; }

    public int Revision { get; init; } = 1;

    public required DateTime AcceptedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

/// <summary>内置视图（T15）：视图自定义随 T15-C。</summary>
public static class BuiltInViews
{
    public static readonly IReadOnlyList<(string ViewId, string Name)> All =
    [
        ("all", "全部游戏"),
        ("favorites", "收藏"),
        ("pending", "待审核候选"),
    ];
}

/// <summary>忽略规则（T11）：scope 默认 ExactPath；撤销匹配规则是恢复候选提示的唯一途径。</summary>
public sealed record IgnoreRule
{
    public required string IgnoreId { get; init; }

    public required string Scope { get; init; }

    public string? Path { get; init; }

    public string? GameId { get; init; }

    public string? Reason { get; init; }

    public int Revision { get; init; } = 1;

    public required DateTime CreatedUtc { get; init; }
}

/// <summary>列表页充实数据（games.list 批量取回，等价于逐游戏 EffectiveField×2 + ListAssets + ListGameTags）。</summary>
public sealed record GameCardEnrichment
{
    public GameTitleTranslation? TitleTranslation { get; init; }

    public required string? Title { get; init; }

    public required string TitleSource { get; init; }

    public required string Summary { get; init; }

    public required string SummarySource { get; init; }

    public required string? CoverAssetId { get; init; }

    public required IReadOnlyList<(string Kind, string Name)> Tags { get; init; }
}

public sealed record GameTitleTranslation(string TranslatedTitle, string SourceTitle, string Provider,
    string TranslatedUtc, bool ManuallyEdited, string DisplayMode);

/// <summary>
/// 游玩统计（feat-1）：由 launch_attempts 聚合。PlaytimeMinutes 为
/// sum(finished-started) 换算的分钟数（整分钟向下取整，负值钳 0）；
/// LastPlayedUtc 为最近一次有起止时间的 attempt 的 finished_utc（从未玩过为 null）。
/// </summary>
public sealed record PlaytimeStats(long PlaytimeMinutes, DateTime? LastPlayedUtc);

/// <summary>accept 结果：accepted=本次转移成功；alreadyAccepted=幂等重放（不写任何表）；conflict=状态或 Revision 不符。</summary>
public sealed record AcceptCandidateOutcome
{
    public required string Status { get; init; }

    public required PersistedCandidate Candidate { get; init; }

    public required string GameId { get; init; }
}

/// <summary>待落库的游戏指纹（game_id 由 Store 在事务内以最终 GameId 填充；entries_json 由 Host 序列化）。</summary>
public sealed record GameFingerprintData(int StrategyVersion, string EntriesJson, DateTime ComputedUtc);

/// <summary>game_fingerprints 行；Title 仅 ListActiveGameFingerprints 的 JOIN 查询填充。</summary>
public sealed record GameFingerprintRow
{
    public required string GameId { get; init; }

    public required int StrategyVersion { get; init; }

    public required string EntriesJson { get; init; }

    public required DateTime ComputedUtc { get; init; }

    public string? Title { get; init; }
}

/// <summary>ignore 结果：ignored=本次成功；conflict=状态或 Revision 不符（不落任何写）。</summary>
public sealed record IgnoreCandidateOutcome
{
    public required string Status { get; init; }

    public required PersistedCandidate Candidate { get; init; }

    public required string IgnoreId { get; init; }
}
