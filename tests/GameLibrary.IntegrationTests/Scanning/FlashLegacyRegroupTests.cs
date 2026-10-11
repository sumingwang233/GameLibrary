using System.Text.Json;
using System.Text.Json.Nodes;
using GameLibrary.Application.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class FlashLegacyRegroupTests
{
    [Fact]
    public async Task InitialRegroupScan_RetriesAfterManualScanWithoutWaitingAnHour()
    {
        var root = Fixture();
        var roots = new RootRegistry();
        roots.AddExisting("root", root, DateTime.UtcNow, "library");
        var called = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new ScanCoordinator(roots, new EventStream(), path =>
        {
            called.TrySetResult(path);
            return new JobOutcome("succeeded", null);
        }, TimeSpan.FromHours(1));
        coordinator.ManualScanRunning = true;
        coordinator.RequestInitialScan([root]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Interlocked.Read(ref coordinator.SkippedCount) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(Interlocked.Read(ref coordinator.SkippedCount) > 0);
        Assert.False(called.Task.IsCompleted);
        coordinator.ManualScanRunning = false;
        Assert.Equal(root, await called.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static string Fixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameLibrary.Tests", $"flash-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void FileAt(string root, string relative, string contents = "FWS")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static async Task<SqliteLibraryStore> Open(string fixture)
    {
        var opened = await SqliteLibraryStore.InitializeAsync(Path.Combine(fixture, "db"), new()
        {
            AppVersion = "test",
            ApiVersion = "1",
            Migrations = DatabaseMigrations.All.Where(migration => migration.Version <= 27).ToArray(),
        }, CancellationToken.None);
        Assert.NotNull(opened.Store);
        Assert.Equal(27, opened.Store!.Info.SchemaVersion);
        Assert.Equal("completed", opened.Store.ReadExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key='v1.7.2.ignoreReset'";
            return command.ExecuteScalar();
        }));
        return opened.Store;
    }

    private static void Register(SqliteLibraryStore store, string path, string kind = "library") =>
        store.UpsertRoot(new($"root-{Guid.NewGuid():N}", path, 1, DateTime.UtcNow, kind), DateTime.UtcNow);

    private static ScanCandidateCollector Scan(string root, SqliteLibraryStore store)
    {
        var collector = new ScanCandidateCollector(GamePath.Create(root), "job-regroup", new CandidateRegistry(),
            store.ListFlashDirectoryRules());
        var result = ScanJobRunner.Run(GamePath.Create(root),
            new JobContext { JobId = "job-regroup", Token = CancellationToken.None }, collector,
            rules: ScanIgnoreRuleSet.FromStore(store));
        Assert.Equal("succeeded", result.FinalState);
        return collector;
    }

    private static void Persist(SqliteLibraryStore store, ScanCandidateCollector collector) =>
        ScanCandidatePersistence.Persist(store, null, collector, "job-regroup", requireRegisteredRoot: true);

    private static GameCard SeedGame(SqliteLibraryStore store, string path, string id, bool removed = true)
    {
        var now = DateTime.UtcNow;
        store.InsertGame(new()
        {
            GameId = id,
            Title = $"User title {id}",
            RootPath = path,
            EntryPath = path,
            Kind = "fileGame",
            Engine = "flash",
            Membership = "active",
            Favorite = true,
            TranslationInherited = true,
            TranslationOverride = "NotRequired",
            Availability = "available",
            AcceptedUtc = now.AddDays(-5),
            UpdatedUtc = now.AddDays(-5),
        });
        store.WriteExclusive((connection, _) =>
        {
            // Seed the already migrated schema27 snapshot; creating a new ignore here would change the scenario.
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE games SET membership=$membership, revision=$revision, translation_override='NotRequired' WHERE game_id=$id";
            command.Parameters.AddWithValue("$membership", removed ? "removed" : "active");
            command.Parameters.AddWithValue("$revision", removed ? 7 : 1);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
        return store.TryGetGame(id)!;
    }

    private static PersistedCandidate SeedLegacy(SqliteLibraryStore store, string path, string id,
        string state = "pendingReview", string? gameId = null, bool flashNull = false, string? supersededBy = null)
    {
        var payload = new JsonObject
        {
            ["engines"] = JsonNode.Parse("[{\"engine\":\"flash\"}]"),
            ["legacyHint"] = id,
        };
        if (flashNull) payload["flash"] = null;
        if (supersededBy is not null) payload["flashSupersededBy"] = supersededBy;
        store.UpsertCandidate(new()
        {
            CandidateId = id,
            JobId = null,
            Kind = "fileGame",
            PhysicalPath = path,
            RelativePath = Path.GetFileName(path),
            PayloadJson = payload.ToJsonString(),
            ReviewState = state,
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        if (gameId is not null) store.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE candidates SET game_id=$game WHERE candidate_id=$id";
            command.Parameters.AddWithValue("$game", gameId);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
        return store.TryGetCandidate(id)!;
    }

    private static PersistedCandidate At(SqliteLibraryStore store, string path) =>
        store.ListCandidates().Single(candidate => string.Equals(candidate.PhysicalPath, path, StringComparison.OrdinalIgnoreCase));

    private static void AssertRetired(SqliteLibraryStore store, PersistedCandidate original, string directory)
    {
        var current = store.TryGetCandidate(original.CandidateId)!;
        Assert.Equal("deferred", current.ReviewState);
        Assert.Equal(original.Revision + 1, current.Revision);
        Assert.Equal(original.GameId, current.GameId);
        using var payload = JsonDocument.Parse(current.PayloadJson);
        Assert.Equal(directory, payload.RootElement.GetProperty("flashSupersededBy").GetString());
        Assert.Equal(original.CandidateId, payload.RootElement.GetProperty("legacyHint").GetString());
    }

    private sealed class Files : ICatalogFiles
    {
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        public GameFingerprintData? Fingerprint(string path, string? entryPath, string? engine, DateTime now) => null;
    }

    [Theory]
    [InlineData(false, "a.swf")]
    [InlineData(true, "Épreuve.swf")]
    public async Task Schema27LegacyFiles_RegroupImmediately_ThenExplicitCollectionReusesBoundGame(
        bool flashNull, string selectedName)
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        FileAt(root, selectedName); FileAt(root, "b.swf"); FileAt(root, "observed.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var selectedGame = SeedGame(store, Path.Combine(root, selectedName), "original-selected");
        var otherGame = SeedGame(store, Path.Combine(root, "b.swf"), "original-unselected");
        // Same-path history must not make the bound candidate accept a different ID or revive both games.
        SeedGame(store, selectedGame.RootPath, "other-same-path");
        var selected = SeedLegacy(store, selectedGame.RootPath, "legacy-selected", gameId: selectedGame.GameId, flashNull: flashNull);
        var other = SeedLegacy(store, otherGame.RootPath, "legacy-other", gameId: otherGame.GameId, flashNull: !flashNull);
        var observed = SeedLegacy(store, Path.Combine(root, "observed.swf"), "legacy-observed", "observed");
        var gamesBefore = store.ListGames().ToArray();
        Assert.Equal(root, Assert.Single(store.ListLegacyFlashScanRoots()));
        store.EnsureCandidateBatch(DateTime.UtcNow);
        Assert.Equal(2, Assert.Single(store.ListNotifications("pending")).CandidateIds.Count);

        Persist(store, Scan(root, store));

        var group = At(store, root);
        Assert.Equal("pendingReview", group.ReviewState);
        Assert.True(FlashCandidateReviewService.ReadGroup(group.PayloadJson)!.RequiresReview);
        AssertRetired(store, selected, root); AssertRetired(store, other, root); AssertRetired(store, observed, root);
        Assert.Equal(gamesBefore, store.ListGames().ToArray());
        Assert.Empty(store.ListLegacyFlashScanRoots());
        var notification = Assert.Single(store.ListNotifications("pending"));
        Assert.Equal(group.CandidateId, Assert.Single(notification.CandidateIds));
        Assert.Equal("发现 1 个新游戏候选", notification.Title);

        var service = new FlashCandidateReviewService(store, new Files());
        Assert.Empty(service.Preview(group, "collection", [selectedName]));
        var accepted = service.Review(group.CandidateId, group.Revision, new("collection", [selectedName], []));
        Assert.Null(accepted.ErrorCode);
        var restored = store.TryGetCandidate(selected.CandidateId)!;
        Assert.Equal("accepted", restored.ReviewState);
        Assert.Equal(selectedGame.GameId, restored.GameId);
        Assert.Equal("active", store.TryGetGame(selectedGame.GameId)!.Membership);
        var actualGame = store.TryGetGame(selectedGame.GameId)!;
        Assert.Equal(selectedGame.Title, actualGame.Title);
        Assert.Equal(selectedGame.Favorite, actualGame.Favorite);
        Assert.Equal(selectedGame.TranslationOverride, actualGame.TranslationOverride);
        Assert.Equal(selectedGame.TranslationInherited, actualGame.TranslationInherited);
        Assert.True(actualGame.AcceptedUtc > selectedGame.AcceptedUtc); // Explicit re-accept is a new library addition.
        Assert.Equal(selectedGame.EntryPath, actualGame.EntryPath);
        Assert.Equal(otherGame, store.TryGetGame(otherGame.GameId));
        Assert.Equal("removed", store.TryGetGame("other-same-path")!.Membership);
        Assert.Equal(3, store.ListGames().Count);
        Assert.Equal("deferred", store.TryGetCandidate(other.CandidateId)!.ReviewState);
        Assert.True(File.Exists(selectedGame.RootPath)); Assert.True(File.Exists(otherGame.RootPath));
        store.EnsureCandidateBatch(DateTime.UtcNow);
        Assert.Empty(store.ListNotifications("pending"));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("unregistered")]
    [InlineData("offline")]
    [InlineData("missing")]
    public async Task LegacyRootsOutsideScannableBoundary_DoNotRetireFiles(string boundary)
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        if (boundary == "offline") root = Path.Combine(
            Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\")
                .First(drive => !Directory.Exists(drive)), $"FlashOffline-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "Child");
        if (boundary is not ("offline" or "missing")) FileAt(child, "a.swf");
        await using var store = await Open(fixture);
        if (boundary != "unregistered") Register(store, root);
        if (boundary == "manual") Register(store, child, "manual");
        var legacy = SeedLegacy(store, Path.Combine(child, "a.swf"), "legacy-boundary");
        if (boundary == "offline") Assert.Equal(root, Assert.Single(store.ListLegacyFlashScanRoots()));
        else Assert.Empty(store.ListLegacyFlashScanRoots());
        var collector = boundary is "offline" or "missing"
            ? new ScanCandidateCollector(GamePath.Create(root), "job-regroup", new CandidateRegistry()) : Scan(root, store);
        Persist(store, collector);
        Assert.Equal(legacy, store.TryGetCandidate(legacy.CandidateId));
        Assert.Empty(store.ListGames());
        Assert.Single(store.ListCandidates());
    }

    [Fact]
    public async Task IncompleteInventory_CannotRetireEvenCoveredLegacyFile()
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        FileAt(root, "a.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var legacy = SeedLegacy(store, Path.Combine(root, "a.swf"), "legacy-partial");
        var incomplete = Assert.Single(Scan(root, store).FlashGroups) with { Complete = false };
        Assert.Equal(0, store.InTransaction(() => store.SupersedeLegacyFlashCandidates(incomplete, DateTime.UtcNow)));
        Assert.Equal(legacy, store.TryGetCandidate(legacy.CandidateId));
        Assert.Empty(store.ListGames());
    }

    [Theory]
    [InlineData("deferred", false)]
    [InlineData("ignored", false)]
    [InlineData("accepted", false)]
    [InlineData("deferred", true)]
    public async Task CollectionCannotOverrideUserStateOrAnotherGroup_AndRollsBackEarlierChanges(string state, bool wrongGroup)
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        FileAt(root, "a.swf"); FileAt(root, "z.swf"); FileAt(root, "scene001.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var firstGame = SeedGame(store, Path.Combine(root, "a.swf"), "first");
        var blockedGame = SeedGame(store, Path.Combine(root, "z.swf"), "blocked");
        SeedGame(store, Path.Combine(root, "scene001.swf"), "scene", removed: false);
        SeedLegacy(store, firstGame.RootPath, "first-candidate", gameId: firstGame.GameId);
        var blocked = SeedLegacy(store, blockedGame.RootPath, "blocked-candidate", state, blockedGame.GameId,
            supersededBy: wrongGroup ? Path.Combine(fixture, "OtherGroup") : null);
        Persist(store, Scan(root, store));
        Assert.Equal(blocked, store.TryGetCandidate(blocked.CandidateId));
        var group = At(store, root);
        var candidatesBefore = store.ListCandidates().OrderBy(row => row.CandidateId).ToArray();
        var gamesBefore = store.ListGames().ToArray();
        var service = new FlashCandidateReviewService(store, new Files());
        var plan = Assert.Single(service.Preview(group, "collection", ["a.swf", "z.swf"]));
        Assert.Equal("scene", plan.GameId);
        var result = store.InTransaction(() => store.InReviewSavepoint(() => service.Review(group.CandidateId, group.Revision,
            new("collection", ["a.swf", "z.swf"], [new(plan.GameId, plan.ExpectedRevision)]))));
        Assert.Equal("RevisionConflict", result.ErrorCode);
        Assert.Equal(candidatesBefore, store.ListCandidates().OrderBy(row => row.CandidateId).ToArray());
        Assert.Equal(gamesBefore, store.ListGames().ToArray());
        Assert.Empty(store.ListFlashDirectoryRules());
    }

    [Theory]
    [InlineData("ExactPath", true)]
    [InlineData("Subtree", true)]
    [InlineData("ConfirmedIdentity", true)]
    [InlineData("ConfirmedIdentity", false)]
    public async Task SelectedIgnoredEntry_CannotRestoreEvenWhenGameIdentityComesFromPathFallback(string scope, bool bound)
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        FileAt(root, "a.swf"); FileAt(root, "scene001.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var game = SeedGame(store, Path.Combine(root, "a.swf"), "ignored-game");
        SeedGame(store, Path.Combine(root, "scene001.swf"), "scene", removed: false);
        SeedLegacy(store, game.RootPath, "ignored-entry", gameId: bound ? game.GameId : null);
        Persist(store, Scan(root, store));
        store.InsertIgnoreRule(new()
        {
            IgnoreId = "new-ignore",
            Scope = scope,
            Path = scope == "Subtree" ? root : game.RootPath,
            GameId = game.GameId,
            CreatedUtc = DateTime.UtcNow,
        });
        var candidatesBefore = store.ListCandidates().OrderBy(row => row.CandidateId).ToArray();
        var gamesBefore = store.ListGames().ToArray();
        var group = At(store, root);
        var service = new FlashCandidateReviewService(store, new Files());
        var plan = Assert.Single(service.Preview(group, "collection", ["a.swf"]));
        var result = store.InTransaction(() => store.InReviewSavepoint(() => service.Review(group.CandidateId, group.Revision,
            new("collection", ["a.swf"], [new(plan.GameId, plan.ExpectedRevision)]))));
        Assert.Equal("RevisionConflict", result.ErrorCode);
        Assert.Equal(candidatesBefore, store.ListCandidates().OrderBy(row => row.CandidateId).ToArray());
        Assert.Equal(gamesBefore, store.ListGames().ToArray());
        Assert.Equal("new-ignore", Assert.Single(store.ListIgnoreRules()).IgnoreId);
        Assert.Empty(store.ListFlashDirectoryRules());
    }

    [Fact]
    public async Task ProjectSelectionCannotBypassIgnoredMainFile_AndRollsBackAdjustmentPlan()
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        FileAt(root, "Main.swf"); FileAt(root, "scene001.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var main = SeedGame(store, Path.Combine(root, "Main.swf"), "main");
        SeedGame(store, Path.Combine(root, "scene001.swf"), "scene", removed: false);
        SeedLegacy(store, main.RootPath, "main-candidate", gameId: main.GameId);
        Persist(store, Scan(root, store));
        store.InsertIgnoreRule(new()
        {
            IgnoreId = "ignored-main",
            Scope = "ExactPath",
            Path = main.RootPath,
            GameId = main.GameId,
            CreatedUtc = DateTime.UtcNow,
        });
        var group = At(store, root);
        var candidatesBefore = store.ListCandidates().OrderBy(row => row.CandidateId).ToArray();
        var gamesBefore = store.ListGames().ToArray();
        var service = new FlashCandidateReviewService(store, new Files());
        var plan = Assert.Single(service.Preview(group, "project", ["Main.swf"]));
        var result = store.InTransaction(() => store.InReviewSavepoint(() => service.Review(group.CandidateId, group.Revision,
            new("project", ["Main.swf"], [new(plan.GameId, plan.ExpectedRevision)]))));
        Assert.Equal("RevisionConflict", result.ErrorCode);
        Assert.Equal(candidatesBefore, store.ListCandidates().OrderBy(row => row.CandidateId).ToArray());
        Assert.Equal(gamesBefore, store.ListGames().ToArray());
        Assert.Equal("ignored-main", Assert.Single(store.ListIgnoreRules()).IgnoreId);
        Assert.Empty(store.ListFlashDirectoryRules());
    }

    [Fact]
    public async Task MixedRoot_ResourcesAndConfirmedCollection_RetireOnlyTheirExactInventory()
    {
        var fixture = Fixture();
        var root = Path.Combine(fixture, "Library");
        for (var index = 1; index <= 3; index++) FileAt(root, $"direct{index}.swf");
        FileAt(root, "Project/Main.exe", "stub"); FileAt(root, "Project/data/scene001.swf");
        FileAt(root, "Collection/selected.swf"); FileAt(root, "Collection/unselected.swf");
        FileAt(root, "Resources/scene001.swf");
        var outside = Path.Combine(fixture, "Outside", "a.swf");
        FileAt(Path.GetDirectoryName(outside)!, "a.swf");
        await using var store = await Open(fixture);
        Register(store, root);
        var initial = Scan(root, store);
        var collectionPath = Path.Combine(root, "Collection");
        var resourcesPath = Path.Combine(root, "Resources");
        var collection = initial.FlashGroups.Single(group => group.DirectoryPath == collectionPath);
        var resources = initial.FlashGroups.Single(group => group.DirectoryPath == resourcesPath);
        store.SaveFlashDirectoryRule(new(collectionPath, "collection", ["selected.swf"], collection.Inventory), DateTime.UtcNow);
        store.SaveFlashDirectoryRule(new(resourcesPath, "resources", [], resources.Inventory), DateTime.UtcNow);
        var acceptedGame = SeedGame(store, Path.Combine(root, "direct1.swf"), "user-accepted", removed: false);
        var direct = Enumerable.Range(1, 3).Select(index => SeedLegacy(store, Path.Combine(root, $"direct{index}.swf"), $"direct-{index}",
            state: index == 1 ? "accepted" : "pendingReview", gameId: index == 1 ? acceptedGame.GameId : null)).ToArray();
        var project = SeedLegacy(store, Path.Combine(root, "Project", "data", "scene001.swf"), "project-scene");
        var selected = SeedLegacy(store, Path.Combine(collectionPath, "selected.swf"), "selected", "observed");
        var unselected = SeedLegacy(store, Path.Combine(collectionPath, "unselected.swf"), "unselected");
        var resource = SeedLegacy(store, Path.Combine(resourcesPath, "scene001.swf"), "resource");
        var outsideCandidate = SeedLegacy(store, outside, "outside");
        var collector = Scan(root, store);
        var directGroup = collector.FlashGroups.Single(group => group.DirectoryPath == root);
        Assert.Equal(3, directGroup.Inventory.Count);
        Assert.False(directGroup.IncludeDescendants);
        Assert.Equal(4, collector.FlashGroups.Count);
        Assert.DoesNotContain(collector.Candidates, candidate => candidate.PhysicalPath == resourcesPath || candidate.PhysicalPath == collectionPath);
        Assert.Contains(collector.Candidates, candidate => candidate.PhysicalPath == selected.PhysicalPath);

        Persist(store, collector);

        Assert.Equal(direct[0], store.TryGetCandidate(direct[0].CandidateId));
        foreach (var candidate in direct.Skip(1)) AssertRetired(store, candidate, root);
        AssertRetired(store, project, Path.Combine(root, "Project"));
        AssertRetired(store, unselected, collectionPath);
        AssertRetired(store, resource, resourcesPath);
        var selectedAfter = store.TryGetCandidate(selected.CandidateId)!;
        Assert.Equal("pendingReview", selectedAfter.ReviewState);
        Assert.False(FlashCandidateReviewService.ReadGroup(selectedAfter.PayloadJson)!.RequiresReview);
        using var payload = JsonDocument.Parse(selectedAfter.PayloadJson);
        Assert.False(payload.RootElement.TryGetProperty("flashSupersededBy", out _));
        Assert.Equal(outsideCandidate, store.TryGetCandidate(outsideCandidate.CandidateId));
        Assert.Equal("pendingReview", At(store, root).ReviewState);
        Assert.Equal("pendingReview", At(store, Path.Combine(root, "Project")).ReviewState);
        Assert.Equal(acceptedGame, Assert.Single(store.ListGames()));
        var pending = store.ListCandidates().Where(candidate => candidate.ReviewState == "pendingReview")
            .Select(candidate => candidate.CandidateId).Order(StringComparer.Ordinal).ToArray();
        var notified = store.ListNotifications("pending").SelectMany(batch => batch.CandidateIds).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(pending, notified);
    }
}
