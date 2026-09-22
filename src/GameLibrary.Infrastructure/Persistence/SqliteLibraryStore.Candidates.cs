namespace GameLibrary.Infrastructure.Persistence;

// 候选审核域转发（第 10 片拆分）：幂等收据（契约 7.1）+ 候选状态机（T11）+ 通知批（T18），
// 全部经主文件的私有 Execute 锁助手串行访问当前库连接；事务留在静态 Store 类。

public sealed partial class SqliteLibraryStore
{
    /// <summary>幂等收据查询（契约 7.1）；收据属于当前库实例。</summary>
    public RequestReceipt? TryGetReceipt(string actor, string operationId, string idempotencyKey)
        => Execute((c, info) => RequestReceiptStore.TryGet(c, info.LibraryInstanceId, actor, operationId, idempotencyKey));

    public void InsertPreparedReceipt(RequestReceipt receipt)
        => Execute((c, _) => RequestReceiptStore.InsertPrepared(c, receipt));

    public void UpdateReceiptAttempt(RequestReceipt receipt, string attemptJson)
        => Execute((c, _) => RequestReceiptStore.UpdateAttempt(c, receipt, attemptJson));

    public void CompleteReceipt(RequestReceipt receipt, string resultJson)
        => Execute((c, _) => RequestReceiptStore.CompleteWithPrune(c, receipt, resultJson, DateTime.UtcNow));

    public bool UpsertCandidate(PersistedCandidate candidate)
        => Execute((c, _) => LibraryCatalogStore.UpsertCandidate(c, candidate));

    /// <summary>
    /// 候选注册根门控：路径必须落在某个 library 根内，且不在任何 manual 根之下
    /// （v23 bug-5：manual 根下的游戏由用户手动管理，不参与候选发现——即使
    /// manual 根嵌套在 library 根内，扫描也不得把已手动添加的位置再报一遍）。
    /// </summary>
    public (bool Stored, bool Existed) UpsertRegisteredCandidate(PersistedCandidate candidate)
        => Execute((c, _) =>
        {
            var roots = RuntimeStateStore.ReadRoots(c);
            var inLibraryRoot = roots.Any(root =>
                !string.Equals(root.Kind, "manual", StringComparison.Ordinal)
                && RuntimeStateStore.ContainsPath(root.PhysicalPath, candidate.PhysicalPath));
            var underManualRoot = roots.Any(root =>
                string.Equals(root.Kind, "manual", StringComparison.Ordinal)
                && RuntimeStateStore.ContainsPath(root.PhysicalPath, candidate.PhysicalPath));
            return inLibraryRoot && !underManualRoot
                ? (true, LibraryCatalogStore.UpsertCandidate(c, candidate))
                : (false, false);
        });

    public void PromoteRescannedCandidate(string physicalPath, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.PromoteRescannedCandidate(c, physicalPath, utcNow));

    public PersistedCandidate? TryGetCandidate(string candidateId)
        => Execute((c, _) => LibraryCatalogStore.TryGetCandidate(c, candidateId));

    public IReadOnlyList<PersistedCandidate> ListCandidates()
        => Execute((c, _) => LibraryCatalogStore.ListCandidates(c));

    public int IgnoreMissingCandidates(DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.IgnoreMissingCandidates(c, utcNow));

    public (int Total, IReadOnlyList<PersistedCandidate> Items) QueryCandidates(
        string? jobId,
        string? state,
        int limit,
        int offset)
        => Execute((c, _) => LibraryCatalogStore.QueryCandidates(c, jobId, state, limit, offset));

    public PersistedCandidate? TransitionCandidate(
        string candidateId, string fromState, string toState, int expectedRevision, string? gameId, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.TransitionCandidate(c, candidateId, fromState, toState, expectedRevision, gameId, utcNow));

    /// <summary>accept 原子化（R43）：建卡/复用 + 引擎标签 + 候选转移 + 匹配指纹单事务提交，conflict 不落任何写。</summary>
    public AcceptCandidateOutcome AcceptCandidate(
        string candidateId, int expectedRevision, GameCard newGame, string engine, DateTime utcNow,
        GameFingerprintData? fingerprint = null)
        => Execute((c, _) => LibraryCatalogStore.AcceptCandidate(c, candidateId, expectedRevision, newGame, engine, utcNow, fingerprint));

    /// <summary>ignore 原子化（R48）：忽略规则 + 候选转移单事务提交，conflict 不落任何写。</summary>
    public IgnoreCandidateOutcome IgnoreCandidate(
        string candidateId, int expectedRevision, IgnoreRule rule, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.IgnoreCandidate(c, candidateId, expectedRevision, rule, utcNow));

    // T18 通知批转发。

    public (NotificationBatch Batch, bool Created)? EnsureCandidateBatch(DateTime utcNow)
        => Execute((c, _) => NotificationStore.EnsureCandidateBatch(c, utcNow));

    public IReadOnlyList<NotificationBatch> ListNotifications(string? state)
        => Execute((c, _) => NotificationStore.ListBatches(c, state));

    public NotificationBatch? TryGetNotification(string notificationId)
        => Execute((c, _) => NotificationStore.TryGetBatch(c, notificationId));

    public NotificationBatch? TransitionNotification(string notificationId, string toState, DateTime utcNow)
        => Execute((c, _) => NotificationStore.TransitionBatch(c, notificationId, toState, utcNow));
}
