using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Identity;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 候选审核域处理器：candidates.accept / candidates.defer / candidates.ignore 状态机。
/// Store 经委托每请求取当前值（library.init / backups.restore 会整体替换 Library，
/// 禁止构造时缓存 store 引用）；Events 为 init-only 引用（换库由 EventStream.BindStore
/// 在其内部重绑）。由 DispatchCore 调用，天然继承幂等收据与串行门等中间件。
/// </summary>
internal sealed class CandidateReviewHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly EventStream _events;

    public CandidateReviewHandler(Func<SqliteLibraryStore?> storeAccessor, EventStream events)
    {
        _storeAccessor = storeAccessor;
        _events = events;
    }

    /// <summary>
    /// 审核（accept/defer/ignore）：仅 PendingReview 可转移（Deferred 需先重新查看）；
    /// accept 按路径幂等返回既有 GameId；ignore 同时登记 ExactPath 忽略规则。
    /// </summary>
    public Envelope<object> CandidateReview(IpcRequest request, string action)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）；候选审核需要库实例");
        }

        if (!IpcRequests.TryGetStringParameter(request, "candidateId", out var candidateId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 candidateId 参数");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数（以 candidates.get 的 revision 为准）");
        }

        var current = store.TryGetCandidate(candidateId);
        if (current is null)
        {
            return IpcRequests.NotFound(request, $"候选不存在：{candidateId}");
        }

        if (action == "accept")
        {
            store.IgnoreMissingCandidates(DateTime.UtcNow);
            current = store.TryGetCandidate(candidateId)!;
            if (!File.Exists(current.PhysicalPath) && !Directory.Exists(current.PhysicalPath))
                return IpcRequests.InvalidArgument(request, "游戏路径已不存在，请重新扫描整理后的目录");
        }

        if (action == "accept" && current.ReviewState == "accepted" && current.GameId is not null)
        {
            // 幂等：重试同候选返回已有 GameId，不重复建卡；similarTo 不重算（安全回放既有结果语义）。
            return CandidateReviewResult(request, current.ReviewState, current.Revision, current.GameId, null, []);
        }

        if (current.ReviewState != "pendingReview")
        {
            return IpcRequests.InvalidArgument(request, $"候选当前状态 {current.ReviewState}；仅 pendingReview 可执行 {action}（重扫可将 observed 晋升）");
        }

        var utcNow = DateTime.UtcNow;
        if (action == "accept")
        {
            // 匹配指纹（ADR-0001 第四键）：在 handler 内、store 锁外计算（哈希是文件 I/O，
            // 不得持 _sync 执行），随 R43 单事务并入 game_fingerprints——建卡/标签/候选转移/指纹
            // 要么全提交要么全不落。整根不可读 → fingerprint=null 仍照常 accept（指纹是线索不是身份）。
            var entryPath = TopEntry(current.PayloadJson, current.PhysicalPath, current.Kind);
            var engine = TopEngine(current.PayloadJson);
            var fingerprint = MatchFingerprintCalculator.Calculate(current.PhysicalPath, entryPath, engine);
            GameFingerprintData? fingerprintData = null;
            if (fingerprint is not null)
            {
                fingerprintData = new GameFingerprintData(
                    fingerprint.StrategyVersion,
                    JsonSerializer.Serialize(fingerprint.Entries, ContractJson.Options),
                    utcNow);
            }

            // R43：建卡/复用 + 引擎标签 + 候选转移 + 指纹单事务提交；conflict 不落任何写
            //（哈希浪费仅发生在 revision 竞态下，上方已先本地校验 pendingReview）。
            var outcome = store.AcceptCandidate(
                candidateId,
                expectedRevision.Value,
                new GameCard
                {
                    GameId = $"game-{Guid.NewGuid():N}",
                    Title = TitleFromPath(current.PhysicalPath, current.RelativePath, current.Kind),
                    RootPath = current.PhysicalPath,
                    Kind = current.Kind,
                    Engine = engine,
                    EntryPath = entryPath,
                    Membership = "active",
                    TranslationInherited = RequiredByToolNeed(current.PayloadJson),
                    AcceptedUtc = utcNow,
                    UpdatedUtc = utcNow,
                },
                engine ?? "",
                utcNow,
                fingerprintData);
            if (outcome.Status == "conflict")
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.RevisionConflict,
                        Message = $"候选 Revision 不一致：期望 {expectedRevision}，当前 {outcome.Candidate.Revision}（状态 {outcome.Candidate.ReviewState}）",
                        Retryable = false,
                    },
                };
            }

            if (outcome.Status == "accepted")
            {
                if (store.TryGetGame(outcome.GameId!) is { } acceptedGame)
                    _ = GameCoverService.Synchronize(store, acceptedGame);
                // 相似建议：对比集合排除自身（刚 accept 的游戏 membership='active' 且指纹行同事务已插入）。
                var similarTo = fingerprint is null
                    ? []
                    : FingerprintSuggestions.Compute(store, fingerprint, outcome.GameId);
                _events.Publish("game.created", $"game:{outcome.GameId}", new
                {
                    gameId = outcome.GameId,
                    fromCandidate = candidateId,
                    title = TitleFromPath(current.PhysicalPath, current.RelativePath, current.Kind),
                    similarTo = FingerprintSuggestions.ToPayload(similarTo),
                }, DateTime.UtcNow);

                return CandidateReviewResult(
                    request, outcome.Candidate.ReviewState, outcome.Candidate.Revision, outcome.GameId, null, similarTo);
            }

            return CandidateReviewResult(
                request, outcome.Candidate.ReviewState, outcome.Candidate.Revision, outcome.GameId, null, []);
        }

        if (action == "ignore")
        {
            // R48：忽略规则 + 候选转移单事务；conflict 不落任何写。
            var outcome = store.IgnoreCandidate(
                candidateId,
                expectedRevision.Value,
                new IgnoreRule
                {
                    IgnoreId = $"ignore-{Guid.NewGuid():N}",
                    Scope = "ExactPath",
                    Path = current.PhysicalPath,
                    Reason = "candidates.ignore",
                    CreatedUtc = utcNow,
                },
                utcNow);
            if (outcome.Status == "conflict")
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.RevisionConflict,
                        Message = $"候选 Revision 不一致：期望 {expectedRevision}，当前 {outcome.Candidate.Revision}（状态 {outcome.Candidate.ReviewState}）",
                        Retryable = false,
                    },
                };
            }

            return CandidateReviewResult(request, "ignored", outcome.Candidate.Revision, null, outcome.IgnoreId, []);
        }

        // defer：单步转移（accept/ignore 已在各自原子路径提前返回）。
        var updated = store.TransitionCandidate(
            candidateId, "pendingReview", "deferred", expectedRevision.Value, gameId: null, utcNow);
        if (updated is null)
        {
            var latest = store.TryGetCandidate(candidateId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"候选 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                },
            };
        }

        return CandidateReviewResult(request, updated.ReviewState, updated.Revision, updated.GameId, null, []);
    }

    private static Envelope<object> CandidateReviewResult(
        IpcRequest request,
        string state,
        int revision,
        string? gameId,
        string? ignoreId,
        IReadOnlyList<SimilarGameSuggestion> similarTo) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                reviewState = state,
                revision,
                gameId,
                ignoreId,
                similarTo = FingerprintSuggestions.ToPayload(similarTo),
            },
        };

    private static string TitleFromPath(string physicalPath, string relativePath, string? kind = null)
    {
        if (string.Equals(kind, "fileGame", StringComparison.Ordinal))
        {
            return Path.GetFileNameWithoutExtension(physicalPath);
        }

        return Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? (relativePath.Length > 0 ? relativePath.Split('/')[^1] : physicalPath);
    }

    private static string? TopEngine(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var engines = document.RootElement.GetProperty("engines");
            if (engines.GetArrayLength() == 0)
            {
                return null;
            }

            return engines[0].GetProperty("engine").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TopEntry(string payloadJson, string physicalPath, string kind)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var entries = document.RootElement.GetProperty("entryCandidates");
            if (entries.GetArrayLength() == 0)
            {
                return null;
            }

            var relativePath = entries[0].GetProperty("relativePath").GetString();
            if (relativePath is null)
            {
                return null;
            }

            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            return string.Equals(kind, "fileGame", StringComparison.Ordinal)
                ? physicalPath
                : Path.GetFullPath(Path.Combine(
                    physicalPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>从候选 payload 读取祖先 [toolNeed] 继承标记（accept 时落库到 games.translation_inherited）。</summary>
    private static bool RequiredByToolNeed(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement
                .GetProperty("classification")
                .GetProperty("requiredByToolNeed")
                .GetBoolean();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
