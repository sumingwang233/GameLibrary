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
        if (!TryFlashSelection(request.Parameters, out var flashSelection, out var error))
            return IpcRequests.InvalidArgument(request, error!);
        if (flashSelection is not null)
        {
            if (action != "accept") return IpcRequests.InvalidArgument(request, "Flash 目录判断只能随 accept 提交");
            return ToEnvelope(request, store, new FlashCandidateReviewService(store, new CatalogFiles())
                .Review(candidateId, revision.Value, flashSelection));
        }
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
        var inputs = new List<(string Id, int Revision, FlashReviewSelection? Flash)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("candidateId", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                || !item.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt32(out var number) || number < 1 || !ids.Add(id.GetString()!))
                return IpcRequests.InvalidArgument(request, "候选 ID 必须唯一且 expectedRevision 为正整数");
            if (!TryFlashSelection(item, out var flash, out var error)) return IpcRequests.InvalidArgument(request, error!);
            if (flash is not null && action != "accept") return IpcRequests.InvalidArgument(request, "Flash 目录判断只能随 accept 提交");
            inputs.Add((id.GetString()!, number, flash));
        }
        var service = new CandidateReviewService(store, new CatalogFiles());
        var prepared = inputs.Select(item => service.Prepare(item.Id, item.Revision, action)).ToArray();
        var flashService = new FlashCandidateReviewService(store, new CatalogFiles());
        var results = store.InTransaction(() => inputs.Select((item, index) => store.InReviewSavepoint(() =>
            item.Flash is null ? service.Apply(prepared[index]) : flashService.Review(item.Id, item.Revision, item.Flash))).ToArray());
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

    public Envelope<object> Inspect(IpcRequest request)
    {
        var store = storeAccessor();
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        if (!IpcRequests.TryGetStringParameter(request, "candidateId", out var id))
            return IpcRequests.InvalidArgument(request, "缺少 candidateId 参数");
        var candidate = store.TryGetCandidate(id);
        if (candidate is null) return IpcRequests.NotFound(request, "候选不存在");
        try
        {
            var group = FlashCandidateReviewService.ReadGroup(candidate.PayloadJson);
            using var payload = JsonDocument.Parse(candidate.PayloadJson);
            IReadOnlyList<FlashAdjustmentPreview> adjustments = [];
            if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters)
            {
                var hasKind = parameters.TryGetProperty("flashKind", out var kind) && kind.ValueKind != JsonValueKind.Null;
                var hasPaths = parameters.TryGetProperty("entryPaths", out var paths) && paths.ValueKind != JsonValueKind.Null;
                if (hasKind || hasPaths)
                {
                    if (!hasKind || kind.ValueKind != JsonValueKind.String || !hasPaths || paths.ValueKind != JsonValueKind.Array
                        || paths.EnumerateArray().Any(path => path.ValueKind != JsonValueKind.String))
                        return IpcRequests.InvalidArgument(request, "Flash 预览需要 flashKind 与 entryPaths 字符串数组");
                    adjustments = new FlashCandidateReviewService(store, new CatalogFiles()).Preview(candidate,
                        kind.GetString()!, paths.EnumerateArray().Select(path => path.GetString()!).ToArray());
                }
            }
            return new()
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    candidateId = id,
                    revision = candidate.Revision,
                    flash = group,
                    entryCandidates = payload.RootElement.TryGetProperty("entryCandidates", out var entries) ? entries.Clone() : (JsonElement?)null,
                    adjustments
                },
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return IpcRequests.InvalidArgument(request, ex.Message); }
    }

    private static bool TryFlashSelection(JsonElement? parameters, out FlashReviewSelection? selection, out string? error)
    {
        selection = null;
        error = null;
        if (parameters is not { ValueKind: JsonValueKind.Object } input) return true;
        // Generated MCP 参数使用 ContractJson.Never：可选字段的 null 等价于未提供。
        var hasKind = input.TryGetProperty("flashKind", out var kind) && kind.ValueKind != JsonValueKind.Null;
        var hasEntries = input.TryGetProperty("entryPaths", out var entries) && entries.ValueKind != JsonValueKind.Null;
        var hasAdjustments = input.TryGetProperty("adjustments", out var adjustments) && adjustments.ValueKind != JsonValueKind.Null;
        if (!hasKind && !hasEntries && !hasAdjustments) return true;
        if (hasKind && hasEntries && hasAdjustments
            && kind.ValueKind == JsonValueKind.String && (kind.GetString() is "project" or "collection" or "resources")
            && entries.ValueKind == JsonValueKind.Array
            && entries.GetArrayLength() <= 10000 && entries.EnumerateArray().All(entry => entry.ValueKind == JsonValueKind.String)
            && adjustments.ValueKind == JsonValueKind.Array
            && adjustments.GetArrayLength() <= 10000)
        {
            var items = new List<FlashLibraryAdjustment>();
            foreach (var item in adjustments.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("gameId", out var game)
                    || game.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(game.GetString())
                    || !item.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number
                    || !revision.TryGetInt32(out var number) || number < 1)
                { error = "adjustments 需要 gameId 与正整数 expectedRevision"; return false; }
                items.Add(new(game.GetString()!, number));
            }
            selection = new(kind.GetString()!, entries.EnumerateArray().Select(entry => entry.GetString()!).ToArray(), items);
            return true;
        }
        error = "Flash 审核需要 flashKind(project/collection/resources)、entryPaths 与已确认 adjustments 清单";
        return false;
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
