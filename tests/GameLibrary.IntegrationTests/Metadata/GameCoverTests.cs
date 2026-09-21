using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Metadata;

public sealed class GameCoverTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;
    public GameCoverTests(PipeServerFixture fixture) => _fixture = fixture;

    private GameCard CreateGame(string kind = "gameRoot")
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var game = new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = "封面测试",
            RootPath = root,
            Kind = kind,
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _fixture.State.Library.Store!.InsertGame(game);
        return game;
    }

    [Fact]
    public void HistoricalCover_IsCopiedOnce_ExistingDifferentExtensionIsPreserved()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var source = Path.Combine(game.RootPath, "import.jpg");
        File.WriteAllText(source, "original");
        store.ImportAsset(game.GameId, source, DateTime.UtcNow);
        ReconcileService.CheckGames(store, DateTime.UtcNow, synchronizeCovers: false);
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.jpg")));
        ReconcileService.CheckGames(store, DateTime.UtcNow);
        Assert.Equal("original", File.ReadAllText(Path.Combine(game.RootPath, "cover.jpg")));
        var replacement = Path.Combine(game.RootPath, "new.png");
        File.WriteAllText(replacement, "replacement");
        store.ImportAsset(game.GameId, replacement, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, game));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.png")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(game.RootPath, "cover.jpg")));
    }

    [Fact]
    public void ExistingCover_IsImportedIntoOwnedStorage_AndNotDuplicated()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var source = Path.Combine(game.RootPath, "COVER.PNG");
        File.WriteAllText(source, "portable-cover");
        Assert.Null(GameCoverService.Synchronize(store, game));
        Assert.Null(GameCoverService.Synchronize(store, game));
        var asset = Assert.Single(store.ListAssets(game.GameId));
        Assert.NotEqual(source, asset.FilePath);
        Assert.Equal("portable-cover", File.ReadAllText(asset.FilePath));
        Assert.Equal("portable-cover", File.ReadAllText(source));
    }

    [Fact]
    public void FileGame_CopiesCoverToParent_AndRemovedGameIsSkipped()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "game");
        var source = Path.Combine(game.RootPath, "source.webp");
        File.WriteAllText(source, "cover");
        store.ImportAsset(game.GameId, source, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, game with { Membership = "removed" }));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.webp")));
        Assert.Null(GameCoverService.Synchronize(store, game with { Kind = "fileGame", RootPath = exe }));
        Assert.Equal("cover", File.ReadAllText(Path.Combine(game.RootPath, "cover.webp")));
        Assert.Equal("game", File.ReadAllText(exe));
    }

    [Fact]
    public void Reconcile_OldRecycleCandidate_IsIgnoredWithoutChangingFiles()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var path = Path.Combine(game.RootPath, "$RECYCLE.BIN", "old");
        Directory.CreateDirectory(path);
        var executable = Path.Combine(path, "Game.exe");
        File.WriteAllText(executable, "preserve");
        var id = $"candidate-{Guid.NewGuid():N}";
        store.UpsertCandidate(new PersistedCandidate
        {
            CandidateId = id,
            Kind = "gameRoot",
            RelativePath = "old",
            PhysicalPath = path,
            PayloadJson = "{}",
            ReviewState = "pendingReview",
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        var notification = store.EnsureCandidateBatch(DateTime.UtcNow);
        ReconcileService.CheckGames(store, DateTime.UtcNow);
        Assert.Equal("ignored", store.TryGetCandidate(id)!.ReviewState);
        Assert.Equal("acknowledged", store.TryGetNotification(notification!.Value.Batch.NotificationId)!.State);
        Assert.Equal("preserve", File.ReadAllText(executable));
    }
}
