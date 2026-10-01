using System.Text.Json;
using GameLibrary.Application.Catalog;
using GameLibrary.Contracts;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class FlashDirectoryReviewTests
{
    private static string Fixture()
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"flash-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void FileAt(string root, string relative, string contents = "FWS")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static ScanCandidateCollector Scan(string root, SqliteLibraryStore? store = null)
    {
        var collector = new ScanCandidateCollector(GamePath.Create(root), "job-flash", new CandidateRegistry(), store?.ListFlashDirectoryRules());
        var outcome = ScanJobRunner.Run(GamePath.Create(root),
            new JobContext { JobId = "job-flash", Token = CancellationToken.None }, collector,
            rules: ScanIgnoreRuleSet.FromStore(store));
        Assert.Equal("succeeded", outcome.FinalState);
        return collector;
    }

    private static SqliteLibraryStoreOptions Options(int schema = 27) => new()
    { AppVersion = "test", ApiVersion = "1", Migrations = DatabaseMigrations.All.Where(item => item.Version <= schema).ToArray() };

    private static PersistedCandidate Persist(SqliteLibraryStore store, ScanCandidate candidate)
    {
        var row = new PersistedCandidate
        {
            CandidateId = candidate.CandidateId,
            JobId = candidate.JobId,
            Kind = "unknown",
            RelativePath = candidate.RelativePath,
            PhysicalPath = candidate.PhysicalPath,
            PayloadJson = JsonSerializer.Serialize(candidate.ToDetail(), ContractJson.Options),
            ReviewState = "pendingReview",
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.UpsertCandidate(row);
        return store.ListCandidates().Single(item => string.Equals(item.PhysicalPath, row.PhysicalPath, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Files : ICatalogFiles
    {
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        public GameFingerprintData? Fingerprint(string path, string? entryPath, string? engine, DateTime now) => null;
    }

    [Fact]
    public async Task DirectSwfsWithChildProjectsAndCollections_KeepSeparateGroups_AndConfirmedProjectStillPrunesResources()
    {
        var root = Fixture();
        for (var index = 1; index <= 3; index++) FileAt(root, $"direct{index}.swf");
        FileAt(root, "ChildProject/Main.exe", "stub");
        FileAt(root, "ChildProject/data/scene001.swf");
        FileAt(root, "ChildProject/data/scene002.swf");
        FileAt(root, "ChildCollection/a.swf"); FileAt(root, "ChildCollection/b.swf");
        FileAt(root, "ChildUnknown/Main.exe", "stub");
        var initial = Scan(root);
        Assert.Equal(4, initial.Candidates.Count);
        var direct = initial.Candidates.Single(item => item.PhysicalPath == root);
        Assert.Equal(3, direct.Flash!.Inventory.Count);
        Assert.All(direct.Flash.Inventory, entry => Assert.DoesNotContain('/', entry));
        Assert.False(direct.Flash.IncludeDescendants);
        Assert.True(direct.Flash.Complete);
        Assert.True(initial.ShouldDescend(root));
        var projectPath = Path.Combine(root, "ChildProject");
        var project = initial.Candidates.Single(item => item.PhysicalPath == projectPath);
        Assert.Equal(3, project.Flash!.Inventory.Count);
        Assert.Equal(2, initial.Candidates.Single(item => item.PhysicalPath == Path.Combine(root, "ChildCollection")).Flash!.Inventory.Count);
        Assert.Contains(initial.Candidates, item => item.PhysicalPath == Path.Combine(root, "ChildUnknown"));

        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        foreach (var item in initial.Candidates) Persist(store, item);
        store.InsertGame(new GameCard
        {
            GameId = "child-project",
            Title = "Keep child",
            RootPath = projectPath,
            Kind = "gameRoot",
            Engine = "flash",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        var service = new FlashCandidateReviewService(store, new Files());
        var directRow = store.TryGetCandidate(direct.CandidateId)!;
        Assert.Empty(service.Preview(directRow, "collection", ["direct1.swf"]));
        Assert.Null(service.Review(directRow.CandidateId, directRow.Revision, new("collection", ["direct1.swf"], [])).ErrorCode);
        Assert.Equal("active", store.TryGetGame("child-project")!.Membership);
        var afterCollection = Scan(root, store);
        Assert.Equal(4, afterCollection.Candidates.Count);
        Assert.Contains(afterCollection.Candidates, item => item.PhysicalPath == projectPath);
        Assert.Contains(afterCollection.Candidates, item => item.PhysicalPath == Path.Combine(root, "ChildCollection"));

        var projectRow = store.TryGetCandidate(project.CandidateId)!;
        var plan = service.Preview(projectRow, "project", ["Main.exe"]);
        Assert.Null(service.Review(projectRow.CandidateId, projectRow.Revision, new("project", ["Main.exe"],
            plan.Select(item => new FlashLibraryAdjustment(item.GameId, item.ExpectedRevision)).ToArray())).ErrorCode);
        var afterProject = Scan(root, store);
        var confirmed = afterProject.Candidates.Single(item => item.PhysicalPath == projectPath);
        Assert.Equal(CandidateKind.GameRoot, confirmed.Kind);
        Assert.False(confirmed.Flash!.RequiresReview);
        Assert.True(confirmed.Flash.IncludeDescendants);
        Assert.False(afterProject.ShouldDescend(projectPath));
        Assert.DoesNotContain(afterProject.Candidates, item => item.PhysicalPath.StartsWith(projectPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Contains(afterProject.Candidates, item => item.PhysicalPath == Path.Combine(root, "ChildCollection"));
    }

    [Fact]
    public void ExecutableAndSceneResources_AreOneUnknownGroup_NotThirtyFourGames()
    {
        var root = Fixture();
        FileAt(root, "Project/主入口.exe", "stub");
        FileAt(root, "Project/setup.exe", "stub");
        FileAt(root, "Project/runtime/ignored.swf");
        FileAt(root, "runtime/child/ignored.swf");
        for (var index = 0; index < 34; index++) FileAt(root, $"Project/data/scene{index:000}.swf");
        var candidate = Assert.Single(Scan(root).Candidates);
        Assert.Equal(CandidateKind.Unknown, candidate.Kind);
        Assert.True(candidate.Flash!.RequiresReview);
        Assert.Equal(35, candidate.Flash.Inventory.Count);
        Assert.Contains("主入口.exe", candidate.Flash.Inventory);
        Assert.DoesNotContain(candidate.Flash.Inventory, entry => entry.Contains("runtime") || entry.Contains("setup"));
        Assert.Equal(EngineId.Flash, Assert.Single(candidate.Engines).Engine);
        FileAt(root, "Deep/Main.exe", "stub");
        FileAt(root, "Deep/data/swf/scene001.swf");
        Assert.True(Scan(root).Candidates.Single(item => Path.GetFileName(item.PhysicalPath) == "Deep").Flash!.RequiresReview);
    }

    [Fact]
    public async Task Collection683_RequiresDirectoryConfirmation_ThenReusesSavedRule()
    {
        var root = Fixture();
        var collection = Path.Combine(root, "Collection");
        for (var index = 0; index < 683; index++) FileAt(root, $"Collection/game{index:000}.swf");
        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        var candidate = Assert.Single(Scan(collection).Candidates);
        Assert.True(candidate.Flash!.RequiresReview);
        store.SaveFlashDirectoryRule(new(collection, "collection", candidate.Flash.Inventory, candidate.Flash.Inventory), DateTime.UtcNow);
        var files = Scan(collection, store).Candidates;
        Assert.Equal(683, files.Count);
        Assert.All(files, item => { Assert.Equal(CandidateKind.FileGame, item.Kind); Assert.False(item.Flash!.RequiresReview); });
        FileAt(collection, "new.swf");
        Assert.True(Assert.Single(Scan(collection, store).Candidates).Flash!.RequiresReview);
    }

    [Fact]
    public async Task ProjectReview_PreviewsLegacySceneChanges_RequiresExactPlan_PreservesMetadataAndFiles()
    {
        var root = Fixture();
        var directory = Path.Combine(root, "Project");
        FileAt(directory, "Main.exe", "stub");
        FileAt(directory, "data/scene001.swf");
        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        var scene = new GameCard
        {
            GameId = "legacy-scene",
            Title = "User title",
            RootPath = Path.Combine(directory, "data", "scene001.swf"),
            EntryPath = Path.Combine(directory, "data", "scene001.swf"),
            Kind = "fileGame",
            Engine = "flash",
            Membership = "active",
            Favorite = true,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        store.InsertGame(scene);
        var candidate = Persist(store, Assert.Single(Scan(directory).Candidates));
        Assert.NotNull(new CandidateReviewService(store, new Files()).Prepare(candidate.CandidateId, 1, "accept").Error);
        var service = new FlashCandidateReviewService(store, new Files());
        var preview = Assert.Single(service.Preview(candidate, "project", ["Main.exe"]));
        Assert.Equal("removeFromLibrary", preview.ProposedAction);
        Assert.Equal("active", store.TryGetGame(scene.GameId)!.Membership);
        Assert.NotNull(service.Review(candidate.CandidateId, 1, new("project", ["Main.exe"], [])).ErrorCode);
        Assert.Empty(store.ListFlashDirectoryRules());
        var outcome = service.Review(candidate.CandidateId, 1, new("project", ["Main.exe"], [new(preview.GameId, preview.ExpectedRevision)]));
        Assert.Null(outcome.ErrorCode);
        var preserved = store.TryGetGame(scene.GameId)!;
        Assert.Equal("removed", preserved.Membership);
        Assert.Equal("User title", preserved.Title);
        Assert.True(preserved.Favorite);
        Assert.True(File.Exists(scene.RootPath));
        var project = Assert.Single(store.ListGames(), game => game.Membership == "active");
        Assert.Equal(Path.Combine(directory, "Main.exe"), project.EntryPath);
        Assert.False(Assert.Single(Scan(directory, store).Candidates).Flash!.RequiresReview);
    }

    [Fact]
    public async Task ResourcesAreLocalConfirmation_NotGlobalDirectoryNameIgnores_AndStalePlanCannotCommit()
    {
        var root = Fixture();
        FileAt(root, "data/scene000.swf");
        FileAt(root, "f/one.swf");
        FileAt(root, "s/two.swf");
        FileAt(root, "m/three.swf");
        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        var initial = Scan(root, store).Candidates;
        Assert.Equal(4, initial.Count);
        var candidate = Persist(store, initial.Single(item => Path.GetFileName(item.PhysicalPath) == "data"));
        var service = new FlashCandidateReviewService(store, new Files());
        Assert.NotNull(service.Review(candidate.CandidateId, 2, new("resources", [], [])).ErrorCode);
        Assert.Null(service.Review(candidate.CandidateId, 1, new("resources", [], [])).ErrorCode);
        Assert.Empty(store.ListIgnoreRules());
        Assert.Equal(3, Scan(root, store).Candidates.Count);
        FileAt(root, "Other/data/standalone.swf");
        Assert.Equal(4, Scan(root, store).Candidates.Count);
    }

    [Fact]
    public async Task MigrationClearsAllScopesOnce_BackupRetainsRules_RemovedGamesWaitForReview_AndNewIgnoresSurvive()
    {
        var root = Fixture();
        FileAt(root, "Hidden/Main.exe", "stub");
        FileAt(root, "Hidden/data.xp3", "stub");
        var data = Path.Combine(root, "db");
        var init = await SqliteLibraryStore.InitializeAsync(data, Options(26), CancellationToken.None);
        var store = init.Store!;
        var oldEpoch = store.Info.DataEpoch;
        var game = new GameCard
        {
            GameId = "removed",
            Title = "Keep title",
            RootPath = Path.Combine(root, "Hidden"),
            Kind = "gameRoot",
            Engine = "kirikiri",
            EntryPath = Path.Combine(root, "Hidden", "Main.exe"),
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        store.InsertGame(game);
        store.RemoveGame(game.GameId, 1, new IgnoreRule
        {
            IgnoreId = "exact",
            Scope = "ExactPath",
            Path = game.RootPath,
            GameId = game.GameId,
            Reason = "test",
            CreatedUtc = DateTime.UtcNow
        }, DateTime.UtcNow);
        foreach (var scope in new[] { "Subtree", "ConfirmedIdentity" }) store.InsertIgnoreRule(new IgnoreRule
        { IgnoreId = scope, Scope = scope, Path = game.RootPath, GameId = game.GameId, CreatedUtc = DateTime.UtcNow });
        Assert.Empty(Scan(root, store).Candidates);
        await store.DisposeAsync();
        var upgraded = await SqliteLibraryStore.TryOpenAsync(data, Options(), CancellationToken.None);
        store = upgraded.Store!;
        Assert.Empty(store.ListIgnoreRules());
        Assert.Equal("removed", store.TryGetGame(game.GameId)!.Membership);
        Assert.NotEqual(oldEpoch, store.Info.DataEpoch);
        Assert.Equal("pendingReview", Assert.Single(store.ListCandidates()).ReviewState);
        Assert.Single(Scan(root, store).Candidates);
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(data, "backups"), "pre-migration-v26-to-v27-*.db"));
        using (var backup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open(); using var command = backup.CreateCommand(); command.CommandText = "SELECT count(*) FROM ignore_rules";
            Assert.Equal(3L, command.ExecuteScalar());
        }
        var pending = Assert.Single(store.ListCandidates());
        var accepted = new CandidateReviewService(store, new Files()).Apply(new CandidateReviewService(store, new Files()).Prepare(pending.CandidateId, pending.Revision, "accept"));
        Assert.Null(accepted.ErrorCode);
        Assert.Equal(game.GameId, accepted.GameId);
        Assert.Equal("Keep title", store.TryGetGame(game.GameId)!.Title);
        store.InsertIgnoreRule(new IgnoreRule { IgnoreId = "new", Scope = "Subtree", Path = game.RootPath, CreatedUtc = DateTime.UtcNow });
        await store.DisposeAsync();
        var reopened = await SqliteLibraryStore.TryOpenAsync(data, Options(), CancellationToken.None);
        await using var reopenedStore = reopened.Store!;
        Assert.Equal("new", Assert.Single(reopenedStore.ListIgnoreRules()).IgnoreId);
        Assert.Single(Directory.GetFiles(Path.Combine(data, "backups"), "pre-migration-v26-to-v27-*.db"));
    }

    [Fact]
    public async Task CollectionAcceptsOnlyChosenEntries_AndChangedInventoryReopensGroupWithoutRevivingGames()
    {
        var root = Fixture();
        FileAt(root, "a.swf"); FileAt(root, "scene001.swf");
        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        var candidate = Persist(store, Assert.Single(Scan(root).Candidates));
        var result = new FlashCandidateReviewService(store, new Files()).Review(candidate.CandidateId, 1, new("collection", ["a.swf"], []));
        Assert.Null(result.ErrorCode);
        Assert.Equal("a", Assert.Single(store.ListGames()).Title);
        Assert.Equal("deferred", store.TryGetCandidate(candidate.CandidateId)!.ReviewState);
        FileAt(root, "new.swf");
        var reopened = Persist(store, Assert.Single(Scan(root, store).Candidates));
        Assert.Equal("pendingReview", reopened.ReviewState);
        Assert.Single(store.ListGames());
        var obsolete = store.TryGetCandidate(candidate.CandidateId)!;
        Assert.True(obsolete.Revision > candidate.Revision);
    }

    [Fact]
    public async Task ReviewSavepoint_RollsBackSceneAdjustmentsIfACollectionEntryCannotBeAccepted()
    {
        var root = Fixture();
        FileAt(root, "a.swf"); FileAt(root, "scene001.swf");
        var init = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), Options(), CancellationToken.None);
        await using var store = init.Store!;
        var candidate = Persist(store, Assert.Single(Scan(root).Candidates));
        store.InsertGame(new GameCard
        {
            GameId = "scene",
            Title = "Keep",
            RootPath = Path.Combine(root, "scene001.swf"),
            Kind = "fileGame",
            Engine = "flash",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertCandidate(new PersistedCandidate
        {
            CandidateId = "deferred-entry",
            PhysicalPath = Path.Combine(root, "a.swf"),
            RelativePath = "a.swf",
            Kind = "fileGame",
            PayloadJson = "{}",
            ReviewState = "deferred",
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        var service = new FlashCandidateReviewService(store, new Files());
        var plan = service.Preview(candidate, "collection", ["a.swf"]);
        var result = store.InTransaction(() => store.InReviewSavepoint(() => service.Review(candidate.CandidateId, candidate.Revision,
            new("collection", ["a.swf"], plan.Select(item => new FlashLibraryAdjustment(item.GameId, item.ExpectedRevision)).ToArray()))));
        Assert.NotNull(result.ErrorCode);
        Assert.Equal("active", store.TryGetGame("scene")!.Membership);
        Assert.Equal(1, store.TryGetGame("scene")!.Revision);
        Assert.Empty(store.ListFlashDirectoryRules());
        Assert.Equal("pendingReview", store.TryGetCandidate(candidate.CandidateId)!.ReviewState);
    }
}
