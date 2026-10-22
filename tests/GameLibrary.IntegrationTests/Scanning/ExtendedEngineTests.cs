using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Domain.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class ExtendedEngineTests
{
    [Theory]
    [InlineData("rpgMakerXp", "RGSS104E.dll", "Data/Scripts.rxdata")]
    [InlineData("rpgMakerVx", "RGSS202E.dll", "Game.rgss2a")]
    [InlineData("rpgMakerVxAce", "RGSS301.dll", "Game.rgss3a")]
    [InlineData("godot", "", "Game.pck")]
    [InlineData("tyrano", "", "tyrano/tyrano.js")]
    [InlineData("unreal", "", "Example/Content/Paks/Game.pak")]
    public async Task ActualSnapshotAndScanSerialization_ProduceCanonicalEngine_AndManualCreateUsesSameEvidence(string expected, string library, string marker)
    {
        var root = Path.Combine(Path.GetTempPath(), "GameLibrary-engines-" + Guid.NewGuid().ToString("N"));
        void Write(string path, string content = "test")
        {
            var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, content);
        }
        try
        {
            Write("Game.exe", "Never execute");
            Write(marker);
            if (expected == "godot") File.WriteAllBytes(Path.Combine(root, marker), [0x47, 0x44, 0x50, 0x43, 2, 0, 0, 0, 4, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0]);
            if (library != "") Write("Game.ini", "[Game]\nLibrary=" + library);
            if (expected == "tyrano") { Write("data/system/Config.tjs"); Write("index.html"); }
            if (expected == "unreal") { Directory.CreateDirectory(Path.Combine(root, "Engine")); Write("Example/Binaries/Win64/Example-Win64-Shipping.exe", "Never execute"); }
            var path = GamePath.Create(root);
            var report = EngineDetectorSet.Default.DetectAll(new FileSystemDirectorySnapshot(path));
            var result = Assert.Single(report.Results, result => result.Confidence == DetectionConfidence.High);
            Assert.Equal(expected, JsonSerializer.SerializeToElement(result.Engine, ContractJson.Options).GetString());
            Assert.Equal(expected, GameEngineRecognition.Detect(root, Path.Combine(root, "Game.exe")));
            var registry = new CandidateRegistry();
            var collector = new ScanCandidateCollector(path, "scan", registry);
            Assert.Equal("succeeded", ScanJobRunner.Run(path, new JobContext { JobId = "scan", Token = CancellationToken.None }, collector).FinalState);
            var candidate = Assert.Single(collector.Candidates, candidate => candidate.PhysicalPath == root);
            var payload = JsonSerializer.SerializeToElement(candidate.ToDetail(), ContractJson.Options);
            Assert.Contains(payload.GetProperty("engines").EnumerateArray(), item => item.GetProperty("engine").GetString() == expected);
            var opened = await SqliteLibraryStore.InitializeAsync(Path.Combine(root, "library"), new() { AppVersion = "test", ApiVersion = "1" }, CancellationToken.None);
            await using (var store = opened.Store!)
            {
                var now = DateTime.UtcNow;
                var unknown = new GameCard
                {
                    GameId = "unknown",
                    Title = "Keep",
                    RootPath = root,
                    EntryPath = Path.Combine(root, "Game.exe"),
                    Kind = "gameRoot",
                    Membership = "active",
                    AcceptedUtc = now,
                    UpdatedUtc = now
                };
                store.InsertGame(unknown);
                store.InsertGame(unknown with { GameId = "recognized", Engine = "unity" });
                Assert.False(store.SetUnknownGameEngine("unknown", "godot", 0, now));
                ScanCandidatePersistence.Persist(store, null, collector, "scan", readyForReview: true);
                Assert.Equal(expected, store.TryGetGame("unknown")!.Engine);
                Assert.Equal("unity", store.TryGetGame("recognized")!.Engine);
                Assert.Equal(now, store.TryGetGame("unknown")!.AcceptedUtc);
                Assert.Contains(store.ListGameTags("unknown"), tag => tag.Kind == "engine" && tag.Name == expected);
            }
            // Removing the second evidence must not leave a high-confidence guess from the EXE name alone.
            File.Delete(Path.Combine(root, marker));
            if (expected == "unreal") Directory.Delete(Path.Combine(root, "Example/Content/Paks"));
            Assert.Null(GameEngineRecognition.Detect(root, Path.Combine(root, "Game.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
