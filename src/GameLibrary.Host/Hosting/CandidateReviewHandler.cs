using System.Text.Json;
using GameLibrary.Application.Catalog;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Identity;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Catalog;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

internal sealed class CandidateReviewHandler(Func<SqliteLibraryStore?> storeAccessor, EventStream events)
{
    public Envelope<object> CandidateReview(IpcRequest request, string action)
    {
        var store = storeAccessor();
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        if (!IpcRequests.TryGetStringParameter(request, "candidateId", out var candidateId))
            return IpcRequests.InvalidArgument(request, "缺少 candidateId 参数");
        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var revision) || revision is null)
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        var service = new CandidateReviewService(store, new CatalogFiles());
        return ToEnvelope(request, store, service.Apply(service.Prepare(candidateId, revision.Value, action)));
    }

    public Envelope<object> ReviewBatch(IpcRequest request)
    {
        var store = storeAccessor();
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        if (!IpcRequests.TryGetStringParameter(request, "action", out var action) || action is not ("accept" or "defer" or "ignore"))
            return IpcRequests.InvalidArgument(request, "action 必须为 accept/defer/ignore");
        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters
            || !parameters.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() is < 1 or > 1000)
            return IpcRequests.InvalidArgument(request, "items 必须包含 1–1000 项");
        var inputs = new List<(string Id, int Revision)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("candidateId", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                || !item.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt32(out var number) || number < 1 || !ids.Add(id.GetString()!))
                return IpcRequests.InvalidArgument(request, "候选 ID 必须唯一且 expectedRevision 为正整数");
            inputs.Add((id.GetString()!, number));
        }
        var service = new CandidateReviewService(store, new CatalogFiles());
        var prepared = inputs.Select(item => service.Prepare(item.Id, item.Revision, action)).ToArray();
        var results = service.ReviewBatch(prepared);
        // Optional cover synchronization and events follow the successful outer commit.
        var output = results.Select(result => new
        {
            candidateId = result.CandidateId,
            result = ToEnvelope(request, store, result),
        }).ToArray();
        return new()
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { items = output }
        };
    }

    private Envelope<object> ToEnvelope(IpcRequest request, SqliteLibraryStore store, CandidateReviewOutcome result)
    {
        if (result.ErrorCode is not null)
            return IpcRequests.Failure(request, result.ErrorCode, result.ErrorMessage!);
        IReadOnlyList<SimilarGameSuggestion> similar = [];
        if (result.Created)
        {
            if (store.TryGetGame(result.GameId!) is { } game) _ = GameCoverService.Synchronize(store, game);
            if (result.Fingerprint is { } fingerprint)
                similar = FingerprintSuggestions.Compute(store, new MatchFingerprint
                {
                    StrategyVersion = fingerprint.StrategyVersion,
                    Entries = JsonSerializer.Deserialize<MatchFingerprint.FingerprintEntry[]>(fingerprint.EntriesJson, ContractJson.Options)!,
                }, result.GameId!);
            events.Publish("game.created", $"game:{result.GameId}",
                new
                {
                    gameId = result.GameId,
                    fromCandidate = result.CandidateId,
                    similarTo = FingerprintSuggestions.ToPayload(similar)
                }, DateTime.UtcNow);
        }
        events.Publish("candidate.updated", $"candidate:{result.CandidateId}",
            new { candidateId = result.CandidateId, reviewState = result.State }, DateTime.UtcNow);
        return new()
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                reviewState = result.State,
                revision = result.Revision,
                gameId = result.GameId,
                ignoreId = result.IgnoreId,
                similarTo = FingerprintSuggestions.ToPayload(similar)
            },
        };
    }
}
