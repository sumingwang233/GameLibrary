using System.Text.Json;
using GameLibrary.Application.Catalog;
using GameLibrary.Contracts;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

public sealed record FlashLibraryAdjustment(string GameId, int ExpectedRevision);
public sealed record FlashReviewSelection(string Kind, IReadOnlyList<string> EntryPaths,
    IReadOnlyList<FlashLibraryAdjustment> Adjustments);
public sealed record FlashAdjustmentPreview(string GameId, int ExpectedRevision, string Title,
    string RootPath, string? EntryPath, string ProposedAction);

/// <summary>目录判断和库调整共用一个审核事务；预览不写，提交只触碰 SQLite。</summary>
public sealed class FlashCandidateReviewService(SqliteLibraryStore store, ICatalogFiles files)
{
    public static FlashDirectoryGroup? ReadGroup(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("flash", out var group) && group.ValueKind == JsonValueKind.Object
            ? group.Deserialize<FlashDirectoryGroup>(ContractJson.Options) : null;
    }

    public IReadOnlyList<FlashAdjustmentPreview> Preview(PersistedCandidate candidate, string kind,
        IReadOnlyList<string> entryPaths)
    {
        var group = ReadGroup(candidate.PayloadJson) ?? throw new ArgumentException("候选不是 Flash 目录分组，请重新扫描");
        if (!string.Equals(candidate.PhysicalPath, group.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请从目录分组审核，不可用单文件候选调整整个目录");
        ValidateSelection(group, kind, entryPaths);
        var selected = entryPaths.Select(entry => ResolveEntry(group.DirectoryPath, entry)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = kind == "project" ? group.DirectoryPath : null;
        return store.ListGames().Where(game => game.Membership == "active"
                && RuntimeStateStore.ContainsPath(group.DirectoryPath, game.RootPath)
                && (group.IncludeDescendants || kind is "project" or "resources"
                    || string.Equals(game.RootPath, group.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                    || game.Kind is "fileGame" or "manualFile"
                        && string.Equals(Path.GetDirectoryName(game.RootPath), group.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(game.RootPath, group.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                    || game.Engine?.Equals("flash", StringComparison.OrdinalIgnoreCase) == true
                    || Path.GetExtension(game.RootPath).Equals(".swf", StringComparison.OrdinalIgnoreCase)))
            .Select(game => new FlashAdjustmentPreview(game.GameId, game.Revision, game.Title, game.RootPath, game.EntryPath,
                kind == "project" && string.Equals(game.RootPath, target, StringComparison.OrdinalIgnoreCase)
                    ? "setEntry" : kind == "collection" && selected.Contains(game.RootPath) ? "keep" : "removeFromLibrary"))
            .Where(game => game.ProposedAction != "keep")
            .OrderBy(game => game.GameId, StringComparer.Ordinal).ToArray();
    }

    public CandidateReviewOutcome Review(string candidateId, int revision, FlashReviewSelection selection)
    {
        var candidate = store.TryGetCandidate(candidateId);
        if (candidate is null) return new(candidateId, ErrorCode: "NotFound", ErrorMessage: "候选不存在");
        if (candidate.Revision != revision || candidate.ReviewState != "pendingReview") return Conflict(candidateId);
        try
        {
            var group = ReadGroup(candidate.PayloadJson) ?? throw new ArgumentException("候选不是 Flash 目录分组，请重新扫描");
            if (!string.Equals(candidate.PhysicalPath, group.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请从目录分组审核，不可用单文件候选调整整个目录");
            ValidateSelection(group, selection.Kind, selection.EntryPaths);
            var (fresh, complete) = FlashDirectoryInspector.Inspect(GamePath.Create(group.DirectoryPath), CancellationToken.None,
                group.IncludeDescendants);
            if (fresh is null || !complete || !fresh.EntryCandidates.Select(item => item.RelativePath).Order(StringComparer.Ordinal)
                .SequenceEqual(group.Inventory.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return new(candidateId, ErrorCode: "RevisionConflict", ErrorMessage: "Flash 目录结构已变化或读取不完整，请重新扫描");
            var ruleInventory = group.Inventory;
            if (!group.IncludeDescendants && selection.Kind is "project" or "resources")
            {
                var (project, projectComplete) = FlashDirectoryInspector.Inspect(GamePath.Create(group.DirectoryPath), CancellationToken.None);
                if (project is null || !projectComplete)
                    return new(candidateId, ErrorCode: "RevisionConflict", ErrorMessage: "Flash 目录读取不完整，请重新扫描");
                ruleInventory = project.EntryCandidates.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray();
            }
            var games = selection.EntryPaths.Select(entry => NewGame(group.DirectoryPath, entry, selection.Kind, candidate)).ToArray();
            var fingerprints = games.Select(game => files.Fingerprint(game.RootPath, game.EntryPath, "flash", DateTime.UtcNow)).ToArray();
            return store.InTransaction(() =>
            {
                var current = store.TryGetCandidate(candidateId)!;
                if (current.Revision != revision || current.PayloadJson != candidate.PayloadJson || current.ReviewState != "pendingReview")
                    throw new InvalidOperationException("候选已变更");
                var plan = Preview(current, selection.Kind, selection.EntryPaths);
                if (!plan.Select(item => new FlashLibraryAdjustment(item.GameId, item.ExpectedRevision))
                    .OrderBy(item => item.GameId, StringComparer.Ordinal)
                    .SequenceEqual(selection.Adjustments.OrderBy(item => item.GameId, StringComparer.Ordinal)))
                    throw new InvalidOperationException("请确认完整调整清单；记录版本变化后必须重新预览");
                var now = DateTime.UtcNow;
                foreach (var adjustment in plan)
                    store.ApplyFlashGameAdjustment(adjustment.GameId, adjustment.ExpectedRevision,
                        adjustment.ProposedAction == "removeFromLibrary" ? "removed" : "active",
                        adjustment.ProposedAction == "setEntry" ? games[0].EntryPath : null, now);
                // 明确选中的入口覆盖已移除记录的旧入口；复用其 ID 与元数据，仍只在本次 accept 生效。
                var existingGames = store.ListGames().ToLookup(item => item.RootPath, StringComparer.OrdinalIgnoreCase);
                foreach (var game in games)
                {
                    var removed = existingGames[game.RootPath].FirstOrDefault(item => item.Membership == "removed");
                    if (removed is not null)
                        store.ApplyFlashGameAdjustment(removed.GameId, removed.Revision, "active", game.EntryPath, now);
                }
                CandidateReviewOutcome result;
                if (selection.Kind == "project")
                {
                    var outcome = store.AcceptCandidate(candidateId, revision, games[0], "flash", now, fingerprints[0]);
                    if (outcome.Status == "conflict") throw new InvalidOperationException("候选已变更");
                    result = new(candidateId, outcome.Candidate.ReviewState, outcome.Candidate.Revision, outcome.GameId,
                        Fingerprint: fingerprints[0], Created: outcome.Status == "accepted");
                }
                else
                {
                    var children = store.ListCandidates().ToLookup(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < games.Length; index++)
                    {
                        var game = games[index];
                        var child = children[game.RootPath].FirstOrDefault();
                        if (child?.ReviewState == "accepted" && child.GameId is not null
                            && store.TryGetGame(child.GameId)?.Membership == "active") continue;
                        if (child is not null && child.ReviewState != "pendingReview")
                            throw new InvalidOperationException("选中入口已有未完成审核状态，请先重新扫描或撤销忽略");
                        if (child is null)
                        {
                            child = new PersistedCandidate
                            {
                                CandidateId = $"cand-{Guid.NewGuid():N}",
                                JobId = current.JobId,
                                Kind = "fileGame",
                                PhysicalPath = game.RootPath,
                                RelativePath = selection.EntryPaths[index],
                                PayloadJson = current.PayloadJson,
                                ReviewState = "pendingReview",
                                ObservedUtc = now,
                                UpdatedUtc = now,
                            };
                            store.UpsertCandidate(child);
                        }
                        var accepted = store.AcceptCandidate(child.CandidateId, child.Revision, game, "flash", now, fingerprints[index]);
                        if (accepted.Status == "conflict") throw new InvalidOperationException("合集入口已变更");
                    }
                    var updated = store.TransitionCandidate(candidateId, "pendingReview", "deferred", revision, null, now)
                        ?? throw new InvalidOperationException("目录候选已变更");
                    result = new(candidateId, updated.ReviewState, updated.Revision);
                }
                if (selection.Kind is "project" or "resources")
                    foreach (var child in store.ListCandidates().Where(item => item.ReviewState == "pendingReview"
                        && !string.Equals(item.PhysicalPath, group.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                        && RuntimeStateStore.ContainsPath(group.DirectoryPath, item.PhysicalPath)))
                        store.TransitionCandidate(child.CandidateId, "pendingReview", "deferred", child.Revision, null, now);
                store.SaveFlashDirectoryRule(new(group.DirectoryPath, selection.Kind, selection.EntryPaths, ruleInventory), now);
                return result;
            });
        }
        catch (ArgumentException ex) { return new(candidateId, ErrorCode: "InvalidArgument", ErrorMessage: ex.Message); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { return new(candidateId, ErrorCode: "RevisionConflict", ErrorMessage: ex.Message); }
    }

    private static GameCard NewGame(string directory, string entry, string kind, PersistedCandidate candidate)
    {
        var path = ResolveEntry(directory, entry);
        using var payload = JsonDocument.Parse(candidate.PayloadJson);
        return new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = kind == "project" ? Path.GetFileName(directory) : Path.GetFileNameWithoutExtension(path),
            RootPath = kind == "project" ? directory : path,
            Kind = kind == "project" ? "gameRoot" : "fileGame",
            Engine = "flash",
            EntryPath = path,
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            TranslationInherited = payload.RootElement.TryGetProperty("classification", out var classification)
                && classification.TryGetProperty("requiredByToolNeed", out var need) && need.GetBoolean(),
        };
    }

    private static void ValidateSelection(FlashDirectoryGroup group, string kind, IReadOnlyList<string> entries)
    {
        if (kind is not ("project" or "collection" or "resources") || !group.Complete
            || kind == "project" && entries.Count != 1 || kind == "collection" && entries.Count == 0
            || kind == "resources" && entries.Count != 0 || entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count
            || entries.Any(entry => !group.Inventory.Contains(entry, StringComparer.Ordinal)))
            throw new ArgumentException("请确认完整目录，项目选择一个主入口、合集选择独立入口，资源目录不选择入口");
        foreach (var entry in entries) _ = ResolveEntry(group.DirectoryPath, entry);
    }

    private static string ResolveEntry(string directory, string entry)
    {
        if (Path.IsPathRooted(entry) || entry.Split('/', '\\').Any(segment => segment is "" or "." or "..") || entry.Contains(':'))
            throw new ArgumentException("入口必须是目录内的相对路径");
        var path = Path.GetFullPath(Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar)));
        for (var current = path; RuntimeStateStore.ContainsPath(directory, current); current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("入口不能经过系统链接");
            if (string.Equals(current, directory, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }

    private static CandidateReviewOutcome Conflict(string id) => new(id, ErrorCode: "RevisionConflict", ErrorMessage: "候选版本或状态已变化，请刷新后审核");
}
