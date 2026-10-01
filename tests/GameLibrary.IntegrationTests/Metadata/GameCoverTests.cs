using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Metadata;

public sealed class GameCoverTests : IClassFixture<PipeServerFixture>
{
    // Pillow 生成的 2×2 RGBA 单色图，lossless=True；不使用任何用户图片。
    internal const string WebPBase64 = "UklGRh4AAABXRUJQVlA4TBEAAAAvAUAAAAdQsGIUtP+BiOh/AAA=";
    private readonly PipeServerFixture _fixture;
    public GameCoverTests(PipeServerFixture fixture) => _fixture = fixture;

    private GameCard CreateGame(string kind = "gameRoot")
    {
        var root = Path.Combine(Path.GetTempPath(), "GameLibrary-tests", $"cover-{Guid.NewGuid():N}");
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

    private static byte[] Png(System.Drawing.Color color)
    {
        using var image = new System.Drawing.Bitmap(2, 2);
        using (var graphics = System.Drawing.Graphics.FromImage(image)) graphics.Clear(color);
        using var output = new MemoryStream();
        image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    internal static void AssertPixelsEqual(byte[] expectedBytes, byte[] actualBytes)
    {
        using var expectedStream = new MemoryStream(expectedBytes);
        using var actualStream = new MemoryStream(actualBytes);
        using var expected = new System.Drawing.Bitmap(expectedStream);
        using var actual = new System.Drawing.Bitmap(actualStream);
        Assert.Equal(expected.Size, actual.Size);
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }

    [Fact]
    public void HistoricalCover_IsCopiedOnce_ExistingDifferentExtensionIsPreserved()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var source = Path.Combine(game.RootPath, "import.jpg");
        var original = Png(System.Drawing.Color.Red);
        File.WriteAllBytes(source, original);
        store.ImportAsset(game.GameId, source, DateTime.UtcNow);
        ReconcileService.CheckGames(store, DateTime.UtcNow, synchronizeCovers: false);
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.png")));
        ReconcileService.CheckGames(store, DateTime.UtcNow);
        var portableBytes = File.ReadAllBytes(Path.Combine(game.RootPath, "cover.png"));
        AssertPixelsEqual(original, portableBytes);
        File.Move(Path.Combine(game.RootPath, "cover.png"), Path.Combine(game.RootPath, "cover.jpg"));
        var replacement = Path.Combine(game.RootPath, "new.png");
        File.WriteAllBytes(replacement, Png(System.Drawing.Color.Blue));
        store.ImportAsset(game.GameId, replacement, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, game));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.png")));
        Assert.Equal(portableBytes, File.ReadAllBytes(Path.Combine(game.RootPath, "cover.jpg")));
    }

    [Fact]
    public void ExistingCover_IsImportedIntoOwnedStorage_AndNotDuplicated()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var source = Path.Combine(game.RootPath, "COVER.PNG");
        var bytes = Png(System.Drawing.Color.Red);
        File.WriteAllBytes(source, bytes);
        Assert.Null(GameCoverService.Synchronize(store, game));
        Assert.Null(GameCoverService.Synchronize(store, game));
        var asset = Assert.Single(store.ListAssets(game.GameId));
        Assert.NotEqual(source, asset.FilePath);
        Assert.Equal(bytes, File.ReadAllBytes(asset.FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void FileGame_CopiesCoverToParent_AndRemovedGameIsSkipped()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "game");
        var source = Path.Combine(game.RootPath, "source.png");
        var bytes = Png(System.Drawing.Color.Blue);
        File.WriteAllBytes(source, bytes);
        store.ImportAsset(game.GameId, source, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, game with { Membership = "removed" }));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "Game.cover.png")));
        Assert.Null(GameCoverService.Synchronize(store, game with { Kind = "fileGame", RootPath = exe }));
        AssertPixelsEqual(bytes, File.ReadAllBytes(Path.Combine(game.RootPath, "Game.cover.png")));
        Assert.Equal("game", File.ReadAllText(exe));
    }

    [Fact]
    public void ExplicitReplacement_PreservesExternalHistory_AndRestoresIt()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var oldBytes = Png(System.Drawing.Color.Red);
        var coverPath = Path.Combine(game.RootPath, "cover.jpg");
        File.WriteAllBytes(coverPath, oldBytes);
        var nextPath = Path.Combine(game.RootPath, "source.png");
        var nextBytes = Png(System.Drawing.Color.Blue);
        File.WriteAllBytes(nextPath, nextBytes);
        var selected = store.ImportAsset(game.GameId, nextPath, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, game, replaceExisting: true));
        Assert.False(File.Exists(coverPath));
        var portable = Path.Combine(game.RootPath, "cover.png");
        var selectedPortableBytes = File.ReadAllBytes(portable);
        AssertPixelsEqual(nextBytes, selectedPortableBytes);
        var previous = Assert.Single(store.ListAssets(game.GameId), asset => !asset.IsCurrent);
        Assert.Equal(oldBytes, File.ReadAllBytes(previous.FilePath));
        Assert.Equal(selected.AssetId, Assert.Single(store.ListAssets(game.GameId), asset => asset.IsCurrent).AssetId);
        store.ChooseAsset(game.GameId, previous.AssetId);
        Assert.Null(GameCoverService.Synchronize(store, game, replaceExisting: true));
        AssertPixelsEqual(oldBytes, File.ReadAllBytes(portable));
        Assert.Contains(store.ListAssets(game.GameId), asset => !asset.IsCurrent && File.ReadAllBytes(asset.FilePath).SequenceEqual(selectedPortableBytes));
        Assert.Equal(oldBytes, File.ReadAllBytes(previous.FilePath));
        Assert.True(File.Exists(nextPath));
    }

    [Fact]
    public void FileGamesInSameDirectory_HaveSeparatePortableCovers()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var firstPath = Path.Combine(game.RootPath, "first.png");
        var secondPath = Path.Combine(game.RootPath, "second.png");
        File.WriteAllBytes(firstPath, Png(System.Drawing.Color.Red));
        File.WriteAllBytes(secondPath, Png(System.Drawing.Color.Blue));
        store.ImportAsset(game.GameId, firstPath, DateTime.UtcNow);
        var first = game with { Kind = "fileGame", RootPath = Path.Combine(game.RootPath, "first.swf") };
        Assert.Null(GameCoverService.Synchronize(store, first));
        var initial = File.ReadAllBytes(Path.Combine(game.RootPath, "first.cover.png"));
        var second = first with { GameId = $"game-{Guid.NewGuid():N}", RootPath = Path.Combine(game.RootPath, "second.swf") };
        store.InsertGame(second);
        store.ImportAsset(second.GameId, secondPath, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, second));
        Assert.Equal(initial, File.ReadAllBytes(Path.Combine(game.RootPath, "first.cover.png")));
        Assert.NotEqual(initial, File.ReadAllBytes(Path.Combine(game.RootPath, "second.cover.png")));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "cover.png")));
    }

    [Fact]
    public void FailedAtomicReplacement_LeavesOriginalCoverAndHistoryIntact()
    {
        var store = _fixture.State.Library.Store!;
        var game = CreateGame();
        var coverPath = Path.Combine(game.RootPath, "cover.png");
        var previousBytes = Png(System.Drawing.Color.Red);
        File.WriteAllBytes(coverPath, previousBytes);
        var source = Path.Combine(game.RootPath, "next.png");
        File.WriteAllBytes(source, Png(System.Drawing.Color.Blue));
        store.ImportAsset(game.GameId, source, DateTime.UtcNow);
        using (var locked = new FileStream(coverPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(GameCoverService.Synchronize(store, game, replaceExisting: true));
        Assert.Equal(previousBytes, File.ReadAllBytes(coverPath));
        Assert.Contains(store.ListAssets(game.GameId), asset => !asset.IsCurrent && File.ReadAllBytes(asset.FilePath).SequenceEqual(previousBytes));
        Assert.Empty(Directory.EnumerateFiles(game.RootPath, ".gamelibrary-cover-*.tmp"));
    }

    [Fact]
    public void SafeDecode_RejectsInvalidBytesAndOversizedDimensions()
    {
        Assert.ThrowsAny<Exception>(() => GameCoverService.PrepareImage([1, 2, 3]));
        using var image = new System.Drawing.Bitmap(8193, 1);
        using var output = new MemoryStream();
        image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(output.ToArray()));
    }

    [Fact]
    public void PrepareImage_PngPreservesAllArgbPixels_IncludingTransparency()
    {
        using var image = new System.Drawing.Bitmap(2, 2);
        image.SetPixel(0, 0, System.Drawing.Color.FromArgb(128, 24, 96, 160));
        image.SetPixel(1, 0, System.Drawing.Color.FromArgb(64, 13, 97, 151));
        image.SetPixel(0, 1, System.Drawing.Color.Transparent);
        image.SetPixel(1, 1, System.Drawing.Color.Red);
        using var output = new MemoryStream();
        image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        var original = output.ToArray();
        var prepared = GameCoverService.PrepareImage(original);
        Assert.Equal(".png", prepared.Extension);
        AssertPixelsEqual(original, prepared.Bytes);
        AssertPixelsEqual(original, GameCoverService.PrepareImage(prepared.Bytes).Bytes);
    }

    [Theory]
    [InlineData(WebPBase64)]
    [InlineData("UklGRjgAAABXRUJQVlA4ICwAAADQAQCdASoCAAIAAUAmJaACdLoB+AADsAD+8U2v/M0YSkjyA/+YJX5BuuQAAA==")]
    [InlineData("UklGRlgAAABXRUJQVlA4WAoAAAAQAAAAAQAAAQAAQUxQSAUAAAAAgICAgABWUDggLAAAANABAJ0BKgIAAgABQCYloAJ0ugH4AAOwAP7xTa/8zRhKSPID/5glfkG65AAA")]
    public void PrepareImage_RealLosslessLossyAndExtendedWebP_PreservesBytes(string encoded)
    {
        // 三个 fixture 均由 Pillow 的 Image.new(...).save(format="WEBP") 生成。
        var bytes = Convert.FromBase64String(encoded);
        var prepared = GameCoverService.PrepareImage(bytes);
        Assert.Equal(".webp", prepared.Extension);
        Assert.Same(bytes, prepared.Bytes);
    }

    [Fact]
    public void PrepareImage_WebP_ValidatesContainerDimensionsAndFiveMiBBoundary()
    {
        var fixture = Convert.FromBase64String(WebPBase64);
        var oversizedDimension = (byte[])fixture.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversizedDimension.AsSpan(21, 4), 8192u | (1u << 14));
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(oversizedDimension));
        var badChunk = (byte[])fixture.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badChunk.AsSpan(16, 4), uint.MaxValue);
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(badChunk));
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(fixture[..^1]));
        var badPadding = (byte[])fixture.Clone();
        badPadding[^1] = 1;
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(badPadding));
        // RIFF 允许未知 chunk；用合法 JUNK chunk 补齐到恰好 5 MiB。
        var boundary = new byte[GameCoverService.MaxBytes];
        fixture.CopyTo(boundary, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boundary.AsSpan(4, 4), (uint)boundary.Length - 8);
        "JUNK"u8.CopyTo(boundary.AsSpan(fixture.Length, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boundary.AsSpan(fixture.Length + 4, 4), (uint)(boundary.Length - fixture.Length - 8));
        Assert.Equal(".webp", GameCoverService.PrepareImage(boundary).Extension);
        Array.Resize(ref boundary, boundary.Length + 1);
        Assert.Throws<ArgumentException>(() => GameCoverService.PrepareImage(boundary));
    }

    [Fact]
    public void WebP_AutomaticFillAndExplicitRestore_KeepFormatAndSeparateGames()
    {
        var store = _fixture.State.Library.Store!;
        var directoryGame = CreateGame();
        var first = directoryGame with { Kind = "fileGame", RootPath = Path.Combine(directoryGame.RootPath, "first.swf") };
        var second = first with { GameId = $"game-{Guid.NewGuid():N}", RootPath = Path.Combine(directoryGame.RootPath, "second.swf") };
        store.InsertGame(second);
        var bytes = Convert.FromBase64String(WebPBase64);
        var firstCover = Path.Combine(directoryGame.RootPath, "first.cover.webp");
        File.WriteAllBytes(firstCover, bytes);
        Assert.Null(GameCoverService.Synchronize(store, first));
        var original = Assert.Single(store.ListAssets(first.GameId));
        Assert.EndsWith(".webp", original.FilePath);
        store.ImportAsset(second.GameId, original.FilePath, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, second));
        var secondCover = Path.Combine(directoryGame.RootPath, "second.cover.webp");
        Assert.Equal(bytes, File.ReadAllBytes(secondCover));
        var pngPath = Path.Combine(directoryGame.RootPath, "replacement.png");
        File.WriteAllBytes(pngPath, Png(System.Drawing.Color.Red));
        store.ImportAsset(first.GameId, pngPath, DateTime.UtcNow);
        Assert.Null(GameCoverService.Synchronize(store, first, replaceExisting: true));
        Assert.False(File.Exists(firstCover));
        Assert.Equal(bytes, File.ReadAllBytes(original.FilePath));
        store.ChooseAsset(first.GameId, original.AssetId);
        Assert.Null(GameCoverService.Synchronize(store, first, replaceExisting: true));
        Assert.Equal(bytes, File.ReadAllBytes(firstCover));
        Assert.Equal(bytes, File.ReadAllBytes(secondCover));
        Assert.False(File.Exists(Path.Combine(directoryGame.RootPath, "first.cover.png")));
        Assert.False(File.Exists(Path.Combine(directoryGame.RootPath, "second.cover.png")));
        Assert.False(File.Exists(Path.Combine(directoryGame.RootPath, "cover.png")));
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
