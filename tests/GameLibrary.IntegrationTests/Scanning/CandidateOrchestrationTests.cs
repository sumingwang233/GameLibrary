using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// 候选编排：安装根、合集诊断、游戏目录边界、引擎冲突与空目录。
/// 直接驱动 ScanJobRunner + CandidateRegistry，不经管道。
/// </summary>
public sealed class CandidateOrchestrationTests
{
    private static string NewFixtureRoot()
    {
        var path = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs", $"candorche-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static (ScanCandidateCollector Collector, CandidateRegistry Registry) RunScan(string rootPath)
    {
        var validation = GamePath.TryCreate(rootPath);
        Assert.True(validation.IsValid);
        var registry = new CandidateRegistry();
        var collector = new ScanCandidateCollector(validation.Path!, "job-test", registry);
        var context = new JobContext { JobId = "job-test", Token = CancellationToken.None };
        var outcome = ScanJobRunner.Run(validation.Path!, context, collector);
        Assert.Equal("succeeded", outcome.FinalState);
        return (collector, registry);
    }

    [Fact]
    public void SingleKirikiriGame_ProducesGameRootCandidate()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "[LunaSoft]", "ExampleA");
            WriteFile(Path.Combine(game, "Game.exe"));
            WriteFile(Path.Combine(game, "data.xp3"));

            var (_, registry) = RunScan(root);

            var candidate = Assert.Single(registry.List());
            Assert.Equal(CandidateKind.GameRoot, candidate.Kind);
            Assert.Equal(EngineId.Kirikiri, Assert.Single(candidate.Engines).Engine);
            Assert.Equal(DetectionConfidence.High, candidate.Confidence);
            Assert.Equal(CandidateReviewState.Observed, candidate.ReviewState);
            Assert.False(candidate.EngineConflict);
            Assert.Equal("Game.exe", Assert.Single(candidate.EntryCandidates).RelativePath);
            var tag = Assert.Single(candidate.Classification.Tags);
            Assert.Equal(FolderTagKind.Publisher, tag.Kind);
            Assert.Equal("LunaSoft", tag.CanonicalValue);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void CollectionWithTwoEngineRoots_ProducesTwoRootsAndContainer()
    {
        var root = NewFixtureRoot();
        try
        {
            var collection = Path.Combine(root, "TwoGames");
            var a = Path.Combine(collection, "GameA");
            WriteFile(Path.Combine(a, "GameA.exe"));
            WriteFile(Path.Combine(a, "UnityPlayer.dll"));
            WriteFile(Path.Combine(a, "GameA_Data", "globalgamemanagers"));
            var b = Path.Combine(collection, "GameB");
            WriteFile(Path.Combine(b, "Game.exe"));
            WriteFile(Path.Combine(b, "www", "js", "rpg_core.js"));

            var (_, registry) = RunScan(root);

            Assert.Equal(2, registry.List().Count(c => c.Kind == CandidateKind.GameRoot));
            var container = Assert.Single(registry.List(), c => c.Kind == CandidateKind.Container);
            Assert.Equal(2, container.DirectGameRootChildren);
            Assert.Empty(container.Engines);
            Assert.Null(container.Confidence);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void PlayableRoot_StopsBeforeNestedResources()
    {
        var root = NewFixtureRoot();
        try
        {
            // 顶层本体：Unity 启动根；内层 Games/Game1 独立引擎根。
            WriteFile(Path.Combine(root, "Main.exe"));
            WriteFile(Path.Combine(root, "UnityPlayer.dll"));
            WriteFile(Path.Combine(root, "Main_Data", "globalgamemanagers"));
            var nested = Path.Combine(root, "Games", "Game1");
            WriteFile(Path.Combine(nested, "Game.exe"));
            WriteFile(Path.Combine(nested, "data.xp3"));

            var (_, registry) = RunScan(root);

            var launcher = Assert.Single(registry.List());
            Assert.Equal(CandidateKind.GameRoot, launcher.Kind);
            Assert.Equal("", launcher.RelativePath);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void TwoHighEnginesInSameDirectory_RecordsEngineConflictWithoutPickingOne()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "Conflict");
            WriteFile(Path.Combine(game, "Example.exe"));
            WriteFile(Path.Combine(game, "UnityPlayer.dll"));
            WriteFile(Path.Combine(game, "Example_Data", "globalgamemanagers"));
            WriteFile(Path.Combine(game, "Other.exe"));
            WriteFile(Path.Combine(game, "data.xp3"));

            var (_, registry) = RunScan(root);

            var candidate = Assert.Single(registry.List());
            Assert.True(candidate.EngineConflict);
            Assert.Equal(2, candidate.Engines.Count);
            Assert.Contains(candidate.Engines, e => e.Engine == EngineId.Unity);
            Assert.Contains(candidate.Engines, e => e.Engine == EngineId.Kirikiri);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void TreeWithoutEngineEvidence_ProducesNoCandidates()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteFile(Path.Combine(root, "readme.txt"));
            WriteFile(Path.Combine(root, "Empty", "note.md"));

            var (_, registry) = RunScan(root);

            Assert.Empty(registry.List());
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task DeepJapaneseCollection_FindsBothVersionsAndSkipsTranslationTools()
    {
        var root = NewFixtureRoot();
        try
        {
            var collection = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("dir", 13)), "[LunaSoft] マジック&スラッシュ");
            foreach (var version in new[] { "Ver1.0.0", "Ver1.1.0" })
            {
                var game = Path.Combine(collection, version);
                WriteFile(Path.Combine(game, "Game.exe"));
                WriteFile(Path.Combine(game, "UnityPlayer.dll"));
                WriteFile(Path.Combine(game, "Game_Data", "globalgamemanagers"));
                WriteFile(Path.Combine(game, "汉化工具", "Injector.exe"));
                WriteFile(Path.Combine(game, "汉化工具", "data.xp3"));
            }

            var path = GamePath.Create(root);
            var collector = new ScanCandidateCollector(path, "job-deep", new CandidateRegistry());
            ScanCoverageData? coverage = null;
            ScanJobRunner.Run(path, new JobContext { JobId = "job-deep", Token = CancellationToken.None }, collector,
                options: new ScanWalkOptions { MaxDirectories = 2 }, onCompleted: result => coverage = result);

            Assert.Equal(ScanCompletion.Complete, coverage!.Completion);
            Assert.Equal(17, coverage.ScannedDirectories);
            var games = collector.Candidates.Where(c => c.Kind != CandidateKind.Container).ToArray();
            Assert.Equal(2, games.Length);
            Assert.All(games, game => Assert.Equal(CandidateKind.GameRoot, game.Kind));
            Assert.Contains(games, game => game.PhysicalPath == Path.Combine(collection, "Ver1.0.0"));
            Assert.Contains(games, game => game.PhysicalPath == Path.Combine(collection, "Ver1.1.0"));

            var initialized = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "db"), new SqliteLibraryStoreOptions
            {
                AppVersion = "test",
                ApiVersion = "1",
                Migrations = DatabaseMigrations.All,
            }, CancellationToken.None);
            Assert.True(initialized.IsOpened, initialized.Detail);
            await using var store = initialized.Store!;
            ScanCandidatePersistence.Persist(store, null, collector, "job-deep", readyForReview: true);
            Assert.Equal(2, store.ListCandidates().Count);
            Assert.All(store.ListCandidates(), candidate => Assert.Equal("gameRoot", candidate.Kind));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void UnknownEngineDirectory_WithExecutable_ProducesReviewCandidate()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "UnknownAdventure");
            WriteFile(Path.Combine(game, "UnknownAdventure.exe"));
            WriteFile(Path.Combine(game, "content.pak"));

            var (_, registry) = RunScan(root);

            var candidate = Assert.Single(registry.List());
            Assert.Equal(CandidateKind.Unknown, candidate.Kind);
            Assert.Empty(candidate.Engines);
            Assert.Equal("UnknownAdventure.exe", Assert.Single(candidate.EntryCandidates).RelativePath);
            Assert.Contains(candidate.Evidence, e => e.RuleId == "generic.executable");
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void UnknownEngineDirectory_WithManyOrdinaryFiles_StillFindsExecutable()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "LargeUnknownGame");
            for (var i = 0; i < 300; i++)
            {
                WriteFile(Path.Combine(game, $"asset-{i:D3}.dat"));
            }

            WriteFile(Path.Combine(game, "ZetaGame.exe"));

            var (_, registry) = RunScan(root);

            var candidate = Assert.Single(registry.List());
            Assert.Equal(CandidateKind.Unknown, candidate.Kind);
            Assert.Equal("ZetaGame.exe", Assert.Single(candidate.EntryCandidates).RelativePath);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void RootLevelExecutables_ProduceIndependentFileGameCandidates()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteFile(Path.Combine(root, "GameOne.exe"));
            WriteFile(Path.Combine(root, "GameTwo.exe"));

            var (_, registry) = RunScan(root);

            var candidates = registry.List();
            Assert.Equal(2, candidates.Count);
            Assert.All(candidates, candidate => Assert.Equal(CandidateKind.FileGame, candidate.Kind));
            Assert.Equal(
                ["GameOne.exe", "GameTwo.exe"],
                candidates.Select(candidate => candidate.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void InstallerAndRuntimeExecutables_DoNotProduceCandidates()
    {
        var root = NewFixtureRoot();
        try
        {
            WriteFile(Path.Combine(root, "Installers", "setup.exe"));
            WriteFile(Path.Combine(root, "_CommonRedist", "DXSETUP.exe"));
            WriteFile(Path.Combine(root, "Tool", "UnityCrashHandler64.exe"));

            var (_, registry) = RunScan(root);

            Assert.Empty(registry.List());
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void KnownEngineCandidate_IsNotDuplicatedByGenericDiscovery()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "KnownGame");
            WriteFile(Path.Combine(game, "KnownGame.exe"));
            WriteFile(Path.Combine(game, "UnityPlayer.dll"));
            WriteFile(Path.Combine(game, "KnownGame_Data", "globalgamemanagers"));

            var (_, registry) = RunScan(root);

            var candidate = Assert.Single(registry.List());
            Assert.Equal(CandidateKind.GameRoot, candidate.Kind);
            Assert.Equal(EngineId.Unity, Assert.Single(candidate.Engines).Engine);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void ScanJobRunner_ContinuesAcrossDirectoryBudgetUntilCoverageIsComplete()
    {
        var root = NewFixtureRoot();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                WriteFile(Path.Combine(root, $"Dir{i}", "note.txt"));
            }

            var path = GamePath.Create(root);
            var context = new JobContext { JobId = "job-budget", Token = CancellationToken.None };
            ScanCoverageData? completed = null;

            var outcome = ScanJobRunner.Run(
                path,
                context,
                options: new ScanWalkOptions
                {
                    MaxDirectories = 2,
                    TimeBudget = TimeSpan.FromSeconds(30),
                },
                onCompleted: coverage => completed = coverage);

            Assert.Equal("succeeded", outcome.FinalState);
            Assert.NotNull(completed);
            Assert.Equal(ScanCompletion.Complete, completed!.Completion);
            Assert.Equal(7, completed.ScannedDirectories);
            Assert.Equal(6, completed.ObservedFileEntries);
            Assert.Equal(0, completed.UnvisitedBranches);
            Assert.Null(completed.ResumeTokenJson);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Registry_ListFiltersByJobId_GetRoundTrips()
    {
        var registry = new CandidateRegistry();
        var candidate = new ScanCandidate
        {
            CandidateId = "cand-1",
            JobId = "job-1",
            ScanRoot = @"D:\scan-root",
            PhysicalPath = @"D:\scan-root\GameA",
            RelativePath = "GameA",
            Kind = CandidateKind.GameRoot,
            Engines = [],
            Evidence = [],
            EntryCandidates = [],
            Classification = new FolderClassification([], RequiredByToolNeed: false, ToolNeedSourceSegment: null, RulesVersion: 1),
            ObservedUtc = DateTime.UtcNow,
        };
        registry.Add(candidate);

        Assert.Single(registry.List("job-1"));
        Assert.Empty(registry.List("job-other"));
        Assert.Equal(candidate.CandidateId, registry.Get(candidate.CandidateId)!.CandidateId);
        Assert.Null(registry.Get("cand-missing"));
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
