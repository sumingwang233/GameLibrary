namespace GameLibrary.Domain.States;

/// <summary>候选物理类型（ADR-0005，与审核状态/可用性正交）。</summary>
public enum CandidateKind
{
    Unknown,

    /// <summary>容器/合集目录，尚无安装根证据。</summary>
    Container,

    /// <summary>已确认的安装根。</summary>
    GameRoot,

    /// <summary>独立文件型游戏（根级 EXE/SWF）。</summary>
    FileGame,

    /// <summary>嵌套在其他结构内的独立安装候选（Games/Game1 等），交用户选择。</summary>
    NestedCandidate,

    /// <summary>工具安装（翻译器、播放器等）。</summary>
    Tool,

    /// <summary>上下文相关资源目录（引擎已确认的 *_Data、存档、缓存等）。</summary>
    SupportDirectory,
}

/// <summary>候选审核状态（补充规格 1.4）。Accepted 是终态：离线等后续可用性由 GameAvailability 承担。</summary>
public enum CandidateReviewState
{
    Observed,

    Stabilizing,

    PendingReview,

    Accepted,

    Deferred,

    Ignored,

    /// <summary>审核前文件消失；文件回来后回到 PendingReview。</summary>
    Unavailable,
}

public enum GameAvailability
{
    Unknown,

    Available,

    /// <summary>第一次在成功完整分支检查中缺失。</summary>
    SuspectedMissing,

    /// <summary>卷在线且两次间隔 ≥60 秒的成功完整核对仍缺失。</summary>
    Missing,

    /// <summary>卷离线；保存缺失证据但不累计缺失次数。</summary>
    Offline,

    AccessError,

    RootUnbound,
}

public enum GameMembership
{
    Active,

    /// <summary>库内移除（收据/墓碑 + 忽略记录）；不等于磁盘删除。</summary>
    Removed,
}

/// <summary>状态机允许的转移（可测试规则；非法转移直接拒绝，不静默纠偏）。</summary>
public static class StateMachines
{
    private static readonly Dictionary<CandidateReviewState, CandidateReviewState[]> ReviewTransitions = new()
    {
        [CandidateReviewState.Observed] = [CandidateReviewState.Stabilizing, CandidateReviewState.PendingReview],
        [CandidateReviewState.Stabilizing] = [CandidateReviewState.PendingReview, CandidateReviewState.Observed],
        [CandidateReviewState.PendingReview] =
        [
            CandidateReviewState.Accepted, CandidateReviewState.Deferred, CandidateReviewState.Ignored,
            CandidateReviewState.Unavailable,
        ],
        [CandidateReviewState.Unavailable] = [CandidateReviewState.PendingReview, CandidateReviewState.Ignored],
        [CandidateReviewState.Deferred] = [CandidateReviewState.PendingReview],
        // 只有撤销匹配的忽略规则后才重新建议（回到观察），不周期重弹。
        [CandidateReviewState.Ignored] = [CandidateReviewState.Observed],
        // Accepted 为终态：accepted 候选不因路径离线变回 new。
        [CandidateReviewState.Accepted] = [],
    };

    private static readonly Dictionary<GameAvailability, GameAvailability[]> AvailabilityTransitions = new()
    {
        [GameAvailability.Unknown] = [GameAvailability.Available, GameAvailability.SuspectedMissing, GameAvailability.Offline, GameAvailability.AccessError, GameAvailability.RootUnbound],
        [GameAvailability.Available] = [GameAvailability.SuspectedMissing, GameAvailability.Offline, GameAvailability.AccessError, GameAvailability.RootUnbound],
        [GameAvailability.SuspectedMissing] = [GameAvailability.Missing, GameAvailability.Available, GameAvailability.Offline, GameAvailability.AccessError, GameAvailability.RootUnbound],
        [GameAvailability.Missing] = [GameAvailability.Available, GameAvailability.Offline, GameAvailability.RootUnbound],
        // 离线恢复后重新核对：成功→Available，缺失→重新计数 SuspectedMissing；不继承离线期间失败次数。
        [GameAvailability.Offline] = [GameAvailability.Available, GameAvailability.SuspectedMissing, GameAvailability.AccessError, GameAvailability.RootUnbound],
        [GameAvailability.AccessError] = [GameAvailability.Available, GameAvailability.SuspectedMissing, GameAvailability.Offline, GameAvailability.RootUnbound],
        [GameAvailability.RootUnbound] = [GameAvailability.Available, GameAvailability.SuspectedMissing],
    };

    public static bool CanTransition(CandidateReviewState from, CandidateReviewState to) =>
        ReviewTransitions[from].Contains(to);

    public static bool CanTransition(GameAvailability from, GameAvailability to) =>
        AvailabilityTransitions[from].Contains(to);
}

/// <summary>
/// 可用性判定（补充规格 1.4 / ID-05）：只有卷在线且两次成功完整核对间隔 ≥60 秒仍缺失才 Missing；
/// 离线/访问错误/取消/扫描未完成都不参与缺失计数。
/// </summary>
public sealed record AvailabilityEvaluation(
    GameAvailability NewState,
    DateTime? MissingSinceUtc)
{
    public const int MissingConfirmationSeconds = 60;
}

public static class GameAvailabilityTracker
{
    /// <summary>记录一次成功完整核对的观察结果。present 时清零缺失起点。</summary>
    public static AvailabilityEvaluation RecordFullCheck(
        GameAvailability current,
        bool present,
        bool rootOnline,
        DateTime? missingSinceUtc,
        DateTime utcNow)
    {
        if (!rootOnline)
        {
            return new AvailabilityEvaluation(GameAvailability.Offline, missingSinceUtc);
        }

        if (present)
        {
            return new AvailabilityEvaluation(GameAvailability.Available, null);
        }

        // 卷在线但缺失：Unknown/Available/离线恢复后 → 第一次缺失；
        // 已在 SuspectedMissing → 检查确认间隔。
        if (current != GameAvailability.SuspectedMissing || missingSinceUtc is null)
        {
            return new AvailabilityEvaluation(GameAvailability.SuspectedMissing, utcNow);
        }

        var elapsed = utcNow - missingSinceUtc.Value;
        return elapsed.TotalSeconds >= AvailabilityEvaluation.MissingConfirmationSeconds
            ? new AvailabilityEvaluation(GameAvailability.Missing, missingSinceUtc)
            : new AvailabilityEvaluation(GameAvailability.SuspectedMissing, missingSinceUtc);
    }
}
