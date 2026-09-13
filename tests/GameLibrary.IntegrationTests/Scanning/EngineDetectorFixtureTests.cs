using System.Text;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Domain.Paths;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>
/// 任务书夹具形态（简化生成版）：五类引擎在真实磁盘上的识别。
/// SWF 用最小合法头样本 + 损坏样本；其余为存在性/结构证据。
/// </summary>
public sealed class EngineDetectorFixtureTests
{
    private static string NewFixtureRoot()
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"detectors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static DetectionReport Detect(string directory)
    {
        var snapshot = new FileSystemDirectorySnapshot(GamePath.Create(directory));
        return new EngineDetectorSet(
        [
            new UnityDetector(),
            new RpgMakerMvMzDetector(),
            new RenpyDetector(),
            new KirikiriDetector(),
            new FlashDetector(),
        ]).DetectAll(snapshot);
    }

    [Fact]
    public void KirikiriFixture_GameExePlusXp3_IsHigh()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "[ANIM]", "ExampleA");
            WriteFile(Path.Combine(game, "Game.exe"));
            WriteFile(Path.Combine(game, "data.xp3"));

            var report = Detect(game);

            var result = Assert.Single(report.Results);
            Assert.Equal(EngineId.Kirikiri, result.Engine);
            Assert.Equal(DetectionConfidence.High, result.Confidence);
            Assert.Null(report.Conflict);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void UnityFixture_PairedDataWithCrashHandler_PicksGameEntryOnly()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "[toolNeed]", "[Unity]", "ExampleC");
            WriteFile(Path.Combine(game, "ExampleC.exe"));
            WriteFile(Path.Combine(game, "UnityPlayer.dll"));
            WriteFile(Path.Combine(game, "UnityCrashHandler64.exe"));
            WriteFile(Path.Combine(game, "ExampleC_Data", "globalgamemanagers"));

            var report = Detect(game);

            var result = Assert.Single(report.Results);
            Assert.Equal(EngineId.Unity, result.Engine);
            Assert.Equal(DetectionConfidence.High, result.Confidence);
            var candidate = Assert.Single(result.EntryCandidates);
            Assert.Equal("ExampleC.exe", candidate.RelativePath);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void RpgMakerFixture_WwwLayout_IsHigh()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "[RPG]", "ExampleD");
            WriteFile(Path.Combine(game, "Game.exe"));
            WriteFile(Path.Combine(game, "www", "js", "rpg_core.js"));
            WriteFile(Path.Combine(game, "www", "data", "System.json"));

            var report = Detect(game);

            var result = Assert.Single(report.Results);
            Assert.Equal(EngineId.RpgMakerMvMz, result.Engine);
            Assert.Equal(DetectionConfidence.High, result.Confidence);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void RenpyFixture_PlaintextRpy_IsHigh()
    {
        var root = NewFixtureRoot();
        try
        {
            var game = Path.Combine(root, "RenpyExample");
            WriteFile(Path.Combine(game, "Example.exe"));
            WriteFile(Path.Combine(game, "renpy", "marker"));
            WriteFile(Path.Combine(game, "game", "options.rpy"));

            var report = Detect(game);

            var result = Assert.Single(report.Results);
            Assert.Equal(EngineId.Renpy, result.Engine);
            Assert.Equal(DetectionConfidence.High, result.Confidence);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void FlashFixture_ValidAndCorruptSwfs_ProducePerFileCandidates()
    {
        var root = NewFixtureRoot();
        try
        {
            var collection = Path.Combine(root, "FlashCollection");
            Directory.CreateDirectory(collection);
            File.WriteAllBytes(Path.Combine(collection, "a.swf"), Encoding.ASCII.GetBytes("FWS\x06\x00\x00\x00\x00\x00"));
            File.WriteAllBytes(Path.Combine(collection, "b.swf"), Encoding.ASCII.GetBytes("CWS\x06\x00\x00\x00\x00\x00"));
            File.WriteAllBytes(Path.Combine(collection, "broken.swf"), Encoding.ASCII.GetBytes("BAD!"));

            var report = Detect(collection);

            var result = Assert.Single(report.Results);
            Assert.Equal(EngineId.Flash, result.Engine);
            Assert.Equal(DetectionConfidence.High, result.Confidence);
            Assert.Equal(2, result.EntryCandidates.Count);
            Assert.Contains(result.Evidence, e => e.RelativePath == "broken.swf" && e.Polarity == EvidencePolarity.Negative);
        }
        finally
        {
            TryCleanup(root);
        }
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
