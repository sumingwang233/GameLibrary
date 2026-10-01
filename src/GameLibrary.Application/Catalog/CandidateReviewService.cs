using System.Text.Json;
using GameLibrary.Domain.Catalog;

namespace GameLibrary.Application.Catalog;

public interface ICandidateReviewStore
{
    PersistedCandidate? TryGetCandidate(string candidateId);
    AcceptCandidateOutcome AcceptCandidate(string candidateId, int expectedRevision, GameCard game,
        string engine, DateTime utcNow, GameFingerprintData? fingerprint = null);
    IgnoreCandidateOutcome IgnoreCandidate(string candidateId, int expectedRevision, IgnoreRule rule, DateTime utcNow);
    PersistedCandidate? TransitionCandidate(string candidateId, string fromState, string toState,
        int expectedRevision, string? gameId, DateTime utcNow);
    T InTransaction<T>(Func<T> action);
    CandidateReviewOutcome InReviewSavepoint(Func<CandidateReviewOutcome> action);
}

public interface ICatalogFiles
{
    bool Exists(string path);
    GameFingerprintData? Fingerprint(string path, string? entryPath, string? engine, DateTime utcNow);
}

public sealed record CandidateReviewOutcome(string CandidateId, string? State = null, int Revision = 0,
    string? GameId = null, string? IgnoreId = null, string? ErrorCode = null, string? ErrorMessage = null,
    GameFingerprintData? Fingerprint = null, bool Created = false);

public sealed record PreparedReview(string CandidateId, int ExpectedRevision, string Action,
    PersistedCandidate? Candidate, GameCard? Game, GameFingerprintData? Fingerprint, CandidateReviewOutcome? Error);

public sealed class CandidateReviewService(ICandidateReviewStore store, ICatalogFiles files)
{
    public PreparedReview Prepare(string candidateId, int revision, string action)
    {
        var candidate = store.TryGetCandidate(candidateId);
        CandidateReviewOutcome? error = null;
        if (candidate is null) error = new(candidateId, ErrorCode: "NotFound", ErrorMessage: $"候选不存在：{candidateId}");
        else if (action == "accept" && !files.Exists(candidate.PhysicalPath))
            error = new(candidateId, ErrorCode: "InvalidArgument", ErrorMessage: "游戏路径已不存在，请重新扫描整理后的目录");
        else if (candidate.ReviewState != "pendingReview" && !(action == "accept" && candidate.ReviewState == "accepted"))
            error = new(candidateId, ErrorCode: "InvalidArgument", ErrorMessage: $"候选当前状态 {candidate.ReviewState}；仅 pendingReview 可执行 {action}");
        if (error is not null || action != "accept" || candidate!.ReviewState == "accepted")
            return new(candidateId, revision, action, candidate, null, null, error);
        var now = DateTime.UtcNow;
        var entry = TopEntry(candidate.PayloadJson, candidate.PhysicalPath, candidate.Kind);
        var engine = TopEngine(candidate.PayloadJson);
        var fingerprint = files.Fingerprint(candidate.PhysicalPath, entry, engine, now);
        return new(candidateId, revision, action, candidate, new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = TitleFromPath(candidate.PhysicalPath, candidate.RelativePath, candidate.Kind),
            RootPath = candidate.PhysicalPath,
            Kind = candidate.Kind,
            Engine = engine,
            EntryPath = entry,
            Membership = "active",
            TranslationInherited = RequiredByToolNeed(candidate.PayloadJson),
            AcceptedUtc = now,
            UpdatedUtc = now,
        }, fingerprint, null);
    }

    public CandidateReviewOutcome Apply(PreparedReview prepared)
    {
        if (prepared.Error is not null) return prepared.Error;
        var current = store.TryGetCandidate(prepared.CandidateId);
        if (current is null) return new(prepared.CandidateId, ErrorCode: "NotFound", ErrorMessage: "候选已不存在");
        if (prepared.Action == "accept" && current.ReviewState == "accepted" && current.GameId is not null)
            return new(current.CandidateId, current.ReviewState, current.Revision, current.GameId);
        if (current.Revision != prepared.ExpectedRevision || current.ReviewState != "pendingReview"
            || current.PhysicalPath != prepared.Candidate!.PhysicalPath || current.PayloadJson != prepared.Candidate.PayloadJson)
            return Conflict(current);
        var now = DateTime.UtcNow;
        if (prepared.Action == "accept")
        {
            var outcome = store.AcceptCandidate(current.CandidateId, prepared.ExpectedRevision, prepared.Game!,
                prepared.Game!.Engine ?? "", now, prepared.Fingerprint);
            return outcome.Status == "conflict" ? Conflict(outcome.Candidate) :
                new(current.CandidateId, outcome.Candidate.ReviewState, outcome.Candidate.Revision,
                    outcome.GameId, Fingerprint: prepared.Fingerprint, Created: outcome.Status == "accepted");
        }
        if (prepared.Action == "ignore")
        {
            var outcome = store.IgnoreCandidate(current.CandidateId, prepared.ExpectedRevision,
                new IgnoreRule
                {
                    IgnoreId = $"ignore-{Guid.NewGuid():N}",
                    Scope = "ExactPath",
                    Path = current.PhysicalPath,
                    Reason = "candidates.ignore",
                    CreatedUtc = now
                }, now);
            return outcome.Status == "conflict" ? Conflict(outcome.Candidate) :
                new(current.CandidateId, "ignored", outcome.Candidate.Revision, IgnoreId: outcome.IgnoreId);
        }
        var updated = store.TransitionCandidate(current.CandidateId, "pendingReview", "deferred",
            prepared.ExpectedRevision, null, now);
        return updated is null ? Conflict(current) :
            new(current.CandidateId, updated.ReviewState, updated.Revision, updated.GameId);
    }

    public IReadOnlyList<CandidateReviewOutcome> ReviewBatch(IReadOnlyList<PreparedReview> prepared) =>
        store.InTransaction(() => prepared.Select(item => store.InReviewSavepoint(() => Apply(item))).ToArray());

    private static CandidateReviewOutcome Conflict(PersistedCandidate candidate) =>
        new(candidate.CandidateId, Revision: candidate.Revision, ErrorCode: "RevisionConflict",
            ErrorMessage: $"候选 Revision 不一致，当前 {candidate.Revision}（状态 {candidate.ReviewState}）");

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
