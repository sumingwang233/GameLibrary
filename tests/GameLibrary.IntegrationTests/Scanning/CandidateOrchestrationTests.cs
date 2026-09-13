using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// T05-B 候选编排（策划案 5.3）：安装根、合集容器、嵌套候选、引擎冲突与空目录。
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
    public void NestedGameInsideLauncherRoot_IsNestedCandidate()
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

            Assert.Equal(2, registry.List().Count);
            var nestedCandidate = Assert.Single(registry.List(), c => c.Kind == CandidateKind.NestedCandidate);
            Assert.EndsWith("Games/Game1", nestedCandidate.RelativePath, StringComparison.Ordinal);
            Assert.Equal(EngineId.Kirikiri, Assert.Single(nestedCandidate.Engines).Engine);
            var launcher = Assert.Single(registry.List(), c => c.Kind == CandidateKind.GameRoot);
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
