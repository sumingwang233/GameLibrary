using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

internal sealed class TitlesHandler(HostRuntimeState state)
{
    public Envelope<object> Cancel(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "jobId", out var id)) return IpcRequests.InvalidArgument(request, "需要 jobId");
        var job = state.Jobs.Get(id);
        if (job is null) return IpcRequests.NotFound(request, "作业不存在");
        if (job.Kind != "titleTranslation") return IpcRequests.InvalidArgument(request, "该接口只取消名称翻译；其他作业请使用对应域的取消接口");
        state.Jobs.RequestCancel(id);
        return new() { RequestId = request.RequestId, Ok = true, Status = OperationStatus.Completed, Data = new { jobId = id, state = state.Jobs.Get(id)!.State } };
    }

    public Envelope<object> Translate(IpcRequest request)
    {
        var store = state.Library.Store;
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        if (!IpcRequests.TryGetStringListParameter(request, "gameIds", out var ids) || ids.Count is < 1 or > 10000 || ids.Any(string.IsNullOrWhiteSpace))
            return IpcRequests.InvalidArgument(request, "gameIds 必须是 1–10000 个游戏 ID 的数组");
        var engine = store.ReadSettings().TitleTranslationEngine;
        if (request.Parameters!.Value.TryGetProperty("engine", out var engineValue))
        {
            if (engineValue.ValueKind != JsonValueKind.String || engineValue.GetString() is not ("balanced" or "google" or "bing"))
                return IpcRequests.InvalidArgument(request, "engine 必须是 balanced / google / bing");
            engine = engineValue.GetString()!;
        }
        var force = false;
        if (request.Parameters.Value.TryGetProperty("force", out var forceValue))
        {
            if (forceValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return IpcRequests.InvalidArgument(request, "force 必须是布尔值");
            force = forceValue.GetBoolean();
        }
        var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
        var jobId = state.Jobs.Create("titleTranslation", context => RunAsync(store, selected, engine, force, context));
        return new() { RequestId = request.RequestId, Ok = true, Status = OperationStatus.Accepted, JobId = jobId, Data = new { jobId } };
    }

    public Envelope<object> Edit(IpcRequest request, bool display)
    {
        var store = state.Library.Store;
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var id)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var revision) || revision is null
            || !IpcRequests.TryGetStringParameter(request, display ? "mode" : "title", out var value)
            || (display ? value is not ("original" or "translated") : string.IsNullOrWhiteSpace(value) || value.Length > 4000))
            return IpcRequests.InvalidArgument(request, "需要 gameId、expectedRevision 和有效的 mode / title");
        var alias = store.ReadTitleTranslation(id);
        if (alias is null) return IpcRequests.NotFound(request, "该游戏尚无译名");
        var updated = store.EditTitleTranslation(id, display ? null : value, display ? value : null, revision.Value, DateTime.UtcNow);
        if (updated is null) return IpcRequests.Failure(request, ErrorCodes.RevisionConflict, "游戏已发生变更，请刷新后重试");
        state.Events.Publish("game.updated", id, new { gameId = id, revision = updated }, DateTime.UtcNow);
        return new() { RequestId = request.RequestId, Ok = true, Status = OperationStatus.Completed, Data = Patch(store, id) };
    }

    private async Task<JobOutcome> RunAsync(SqliteLibraryStore store, string[] ids, string engine, bool force, JobContext context)
    {
        var items = new List<TitleItem>();
        var completedIds = new HashSet<string>(StringComparer.Ordinal);
        var sync = new object();
        var next = 0;
        var failed = 0;
        var succeeded = 0;
        var skipped = 0;
        var cancelled = false;
        void Report() => context.ReportProgress(new
        {
            total = ids.Length,
            completed = items.Count,
            succeeded,
            failed,
            skipped,
            pending = ids.Length - items.Count,
            stopReason = state.TitleTranslations.Unavailable ? "enginesUnavailable" : null,
            items = items.ToArray(),
            unprocessedGameIds = ids.Where(id => !completedIds.Contains(id)).ToArray(),
        });
        void Add(string id, string status, string? reason = null, string? provider = null, object? patch = null)
        {
            lock (sync)
            {
                items.Add(new TitleItem(id, status, reason, provider, patch));
                completedIds.Add(id);
                if (status == "succeeded") succeeded++;
                else if (status == "failed") failed++;
                else skipped++;
                Report();
            }
        }
        Report();
        async Task Worker()
        {
            while (!state.TitleTranslations.Unavailable)
            {
                context.Token.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref next) - 1;
                if (index >= ids.Length) return;
                var id = ids[index];
                int revision;
                string original;
                using (store.BeginReadSnapshot())
                {
                    var game = store.TryGetGame(id);
                    if (game is null || game.Membership != "active") { Add(id, "skipped", "inactive"); continue; }
                    if (!force && store.ReadTitleTranslation(id) is not null) { Add(id, "skipped", "alreadyTranslated"); continue; }
                    revision = game.Revision;
                    original = store.EffectiveField(id, "title", game.Title).Value ?? "";
                }
                if (string.IsNullOrWhiteSpace(original) || original.Length > 1000) { Add(id, "skipped", "invalidTitle"); continue; }
                var result = await state.TitleTranslations.TranslateAsync(original, engine, context.Token);
                context.Token.ThrowIfCancellationRequested();
                if (result.Title is null) { Add(id, "failed", result.Error, result.Provider); continue; }
                if (string.Equals(result.Title, original, StringComparison.Ordinal)) { Add(id, "skipped", "unchanged", result.Provider); continue; }
                var updated = store.SaveTitleTranslation(id, original, result.Title, result.Provider!, revision, DateTime.UtcNow);
                if (updated is null) { Add(id, "skipped", "revisionConflict", result.Provider); continue; }
                state.Events.Publish("game.updated", id, new { gameId = id, revision = updated }, DateTime.UtcNow);
                Add(id, "succeeded", provider: result.Provider, patch: Patch(store, id));
            }
        }
        try { await Task.WhenAll(Worker(), Worker()); }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested) { cancelled = true; }
        lock (sync) Report();
        if (cancelled) return JobOutcome.Cancelled();
        return failed > 0 || items.Count < ids.Length ? JobOutcome.Failed("部分名称未翻译，可重试失败或未处理项") : JobOutcome.Succeeded();
    }

    private sealed record TitleItem(string GameId, string Status, string? Reason, string? Provider, object? Patch);

    private static object Patch(SqliteLibraryStore store, string id)
    {
        using var snapshot = store.BeginReadSnapshot();
        var game = store.TryGetGame(id)!;
        var original = store.EffectiveField(id, "title", game.Title).Value ?? "";
        var alias = store.ReadTitleTranslation(id);
        return new
        {
            gameId = id,
            revision = game.Revision,
            title = alias is { DisplayMode: "translated" } ? alias.TranslatedTitle : original,
            originalTitle = original,
            translatedTitle = alias?.TranslatedTitle,
            titleDisplayMode = alias?.DisplayMode ?? "original",
            titleTranslation = alias
        };
    }
}
