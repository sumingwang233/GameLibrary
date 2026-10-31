using System.Buffers.Binary;
using System.Text.Json;
using GameLibrary.Application.Catalog;
using GameLibrary.Contracts;
using GameLibrary.Domain.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.States;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Catalog;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

public sealed class ScanBoundaryRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "scan-boundary-" + Guid.NewGuid().ToString("N"));

    private string Write(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [0]);
        return path;
    }

    private static byte[] EmbeddedGodot()
    {
        var bytes = new byte[92];
        "GDPC"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), 64);
        "GDPC"u8.CopyTo(bytes.AsSpan(88));
        return bytes;
    }

    [Fact]
    public void DriveRootWithEmbeddedGodotFiles_KeepsFileEvidenceAndDiscoversChildGames()
    {
        Write("Standalone.exe", EmbeddedGodot());
        Write("Second.exe", EmbeddedGodot());
        var root = GamePath.Create(Path.GetPathRoot(_root)!);
        var collector = new ScanCandidateCollector(root, "drive-fixture", new());
        var snapshot = new MappedSnapshot(root.PhysicalPath, new FileSystemDirectorySnapshot(GamePath.Create(_root)));
        collector.InspectDirectory(new(root.PhysicalPath, 0, [], []), CancellationToken.None, snapshot);
        Assert.True(collector.ShouldDescend(root.PhysicalPath));
        Assert.Equal(2, collector.Candidates.Count);
        Assert.All(collector.Candidates, candidate =>
        {
            Assert.Equal(CandidateKind.FileGame, candidate.Kind);
            Assert.Equal(EngineId.Godot, Assert.Single(candidate.Engines).Engine);
            var json = JsonSerializer.SerializeToElement(candidate.ToDetail(), ContractJson.Options);
            Assert.Equal("godot", json.GetProperty("engines")[0].GetProperty("engine").GetString());
        });
        var child = Path.Combine(_root, "NewGame");
        Write("NewGame/Game.exe");
        Write("NewGame/UnityPlayer.dll");
        Write("NewGame/Game_Data/globalgamemanagers");
        collector.InspectDirectory(new(child, 1, Directory.GetDirectories(child), Directory.GetFiles(child)), CancellationToken.None);
        Assert.Contains(collector.Candidates, candidate => candidate.PhysicalPath == child
            && candidate.Engines.Any(engine => engine.Engine == EngineId.Unity));
        Assert.DoesNotContain(collector.Candidates, candidate => candidate.PhysicalPath == root.PhysicalPath);
    }

    [Theory]
    [InlineData("pendingReview", false, "deferred")]
    [InlineData("pendingReview", true, "pendingReview")]
    [InlineData("accepted", true, "accepted")]
    [InlineData("ignored", false, "ignored")]
    [InlineData("deferred", false, "deferred")]
    public async Task InspectedDriveRoot_RetiresOnlyUnboundPending_AndSynchronizesNotifications(
        string state, bool bound, string expectedState)
    {
        Directory.CreateDirectory(_root);
        var opened = await SqliteLibraryStore.InitializeAsync(Path.Combine(_root, "db"),
            new() { AppVersion = "test", ApiVersion = "1" }, CancellationToken.None);
        await using var store = opened.Store!;
        var drive = GamePath.Create(Path.GetPathRoot(_root)!);
        var now = DateTime.UtcNow;
        store.UpsertCandidate(new()
        {
            CandidateId = "legacy-drive",
            Kind = "gameRoot",
            PhysicalPath = drive.PhysicalPath,
            RelativePath = "",
            PayloadJson = "{\"engines\":[{\"engine\":\"godot\"}],\"preserved\":true}",
            ReviewState = bound ? "pendingReview" : state,
            ObservedUtc = now,
            UpdatedUtc = now,
        });
        if (bound)
        {
            var current = store.TryGetCandidate("legacy-drive")!;
            var accepted = store.AcceptCandidate(current.CandidateId, current.Revision, new GameCard
            {
                GameId = "existing-game",
                Title = "fixture",
                RootPath = drive.PhysicalPath,
                Kind = "gameRoot",
                Membership = "active",
                AcceptedUtc = now,
                UpdatedUtc = now,
            }, "godot", now);
            Assert.Equal("existing-game", accepted.GameId);
            if (state != "accepted")
                store.TransitionCandidate(current.CandidateId, "accepted", state, accepted.Candidate.Revision, "existing-game", now);
        }
        store.EnsureCandidateBatch(now);
        var collector = new ScanCandidateCollector(drive, "repair", new());
        // No access to the real volume: all observations are routed to the isolated fixture.
        collector.InspectDirectory(new(drive.PhysicalPath, 0, [], []), CancellationToken.None,
            new MappedSnapshot(drive.PhysicalPath, new FileSystemDirectorySnapshot(GamePath.Create(_root), [], [])));
        ScanCandidatePersistence.Persist(store, null, collector, "repair", readyForReview: true);
        var result = store.TryGetCandidate("legacy-drive")!;
        Assert.Equal(expectedState, result.ReviewState);
        Assert.Contains("preserved", result.PayloadJson);
        Assert.Equal(bound ? "existing-game" : null, result.GameId);
        var revision = result.Revision;
        ScanCandidatePersistence.Persist(store, null, collector, "repair-again", readyForReview: true);
        Assert.Equal(revision, store.TryGetCandidate("legacy-drive")!.Revision);
        if (expectedState != "pendingReview") Assert.Empty(store.ListNotifications("pending"));
    }

    [Fact]
    public async Task AutomaticDiscovery_IgnoresShortcutFiles_ButKeepsExecutableAndChildProjects()
    {
        Write("Play.lnk");
        Write("Game.exe");
        Write("Child/Child.exe");
        Write("Folder.lnk/Game.exe");
        Write("Folder.lnk/data.xp3");
        var collector = new ScanCandidateCollector(GamePath.Create(_root), "shortcut-scan", new());
        ScanJobRunner.Run(GamePath.Create(_root), new() { JobId = "shortcut-scan", Token = CancellationToken.None }, collector);
        Assert.DoesNotContain(collector.Candidates, candidate => candidate.EntryCandidates.Any(entry => entry.RelativePath.EndsWith(".lnk")));
        Assert.Contains(collector.Candidates, candidate => candidate.PhysicalPath == Path.Combine(_root, "Game.exe"));
        Assert.Contains(collector.Candidates, candidate => candidate.PhysicalPath == Path.Combine(_root, "Child"));
        var opened = await SqliteLibraryStore.InitializeAsync(Path.Combine(_root, "db"),
            new() { AppVersion = "test", ApiVersion = "1" }, CancellationToken.None);
        await using var store = opened.Store!;
        ScanCandidatePersistence.Persist(store, null, collector, "shortcut-scan", readyForReview: true);
        var directory = Assert.Single(store.ListCandidates(), candidate => candidate.PhysicalPath == Path.Combine(_root, "Folder.lnk"));
        var service = new CandidateReviewService(store, new CatalogFiles());
        var prepared = service.Prepare(directory.CandidateId, directory.Revision, "accept");
        Assert.Null(prepared.Error);
        Assert.Null(service.Apply(prepared).ErrorCode);
    }

    [Theory]
    [InlineData(null, "pendingReview")]
    [InlineData("manual", "pendingReview")]
    [InlineData("library", "deferred")]
    public async Task DriveRootRetirement_RequiresCurrentRegisteredLibraryScope(string? rootKind, string expectedState)
    {
        Directory.CreateDirectory(_root);
        var opened = await SqliteLibraryStore.InitializeAsync(Path.Combine(_root, "db"),
            new() { AppVersion = "test", ApiVersion = "1" }, CancellationToken.None);
        await using var store = opened.Store!;
        var drive = GamePath.Create(Path.GetPathRoot(_root)!);
        var now = DateTime.UtcNow;
        if (rootKind is not null) store.UpsertRoot(new("root", drive.PhysicalPath, 1, now, rootKind), now);
        store.UpsertCandidate(new()
        {
            CandidateId = "legacy",
            Kind = "gameRoot",
            PhysicalPath = drive.PhysicalPath,
            RelativePath = "",
            PayloadJson = "{}",
            ReviewState = "pendingReview",
            ObservedUtc = now,
            UpdatedUtc = now,
        });
        var collector = new ScanCandidateCollector(drive, "scope", new());
        collector.InspectDirectory(new(drive.PhysicalPath, 0, [], []), CancellationToken.None,
            new MappedSnapshot(drive.PhysicalPath, new FileSystemDirectorySnapshot(GamePath.Create(_root), [], [])));
        ScanCandidatePersistence.Persist(store, null, collector, "scope", readyForReview: true, requireRegisteredRoot: true);
        Assert.Equal(expectedState, store.TryGetCandidate("legacy")!.ReviewState);
    }

    [Fact]
    public void ShortcutIdentity_RequiresVerifiedEntryOrUniquePackagedUnrealProject_NotNamesOrAncestors()
    {
        var wrapper = Write("Install/Sample.exe");
        var shipping = Write("Install/Sample/Binaries/Win64/Sample-Win64-Shipping.exe");
        Write("Install/Sample/Content/Paks/Sample.pak");
        Directory.CreateDirectory(Path.Combine(_root, "Install/Engine"));
        var game = new GameCard
        {
            GameId = "shortcut",
            Title = "same title",
            RootPath = Path.Combine(_root, "Install/Play.lnk"),
            Kind = "manualShortcut",
            EntryPath = shipping,
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        Assert.Equal(game.GameId, Assert.Single(new ShortcutGameIdentity([game]).FindMatches([wrapper])).GameId);
        var other = Write("Other/Sample.exe");
        Assert.Empty(new ShortcutGameIdentity([game]).FindMatches([other]));
        var unrelated = Write("Install/Other.exe");
        Assert.Empty(new ShortcutGameIdentity([game]).FindMatches([unrelated]));
        // A second project/entry makes the wrapper relationship ambiguous.
        Assert.Empty(new ShortcutGameIdentity([game]).FindMatches([wrapper]));
        File.Delete(unrelated);
        for (var index = 0; index < 33; index++) Directory.CreateDirectory(Path.Combine(_root, "Install", "extra-" + index));
        Assert.Empty(new ShortcutGameIdentity([game]).FindMatches([wrapper]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MappedSnapshot(string root, IDirectorySnapshot inner) : IDirectorySnapshot
    {
        public string RootPhysicalPath => root;
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public long? FileSize(string path) => inner.FileSize(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public FileProbe TryReadFirstBytes(string path, int count) => inner.TryReadFirstBytes(path, count);
        public FileProbe TryReadBytesAt(string path, long offset, int count) => inner.TryReadBytesAt(path, offset, count);
        public ExecutableIdentity? ReadExecutableIdentity(string path) => inner.ReadExecutableIdentity(path);
    }
}
