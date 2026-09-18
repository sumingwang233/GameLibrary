using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>自定义缓存位置的所有权、安全边界与可再生预览端到端验证。</summary>
public sealed class CacheLocationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public CacheLocationTests(PipeServerFixture fixture) => _fixture = fixture;

    private string DataDirectory => Path.Combine(
        @"D:\Official\GameLibrary\artifacts\test-runs", _fixture.TestId, "data");

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            DataDirectory, "cache-location-test", CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters ?? new { }));
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = $"cache-{Guid.NewGuid():N}",
            OperationId = operationId,
            Parameters = json.RootElement.Clone(),
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CustomCache_GeneratesAndRebuildsPreview_WithoutTouchingParentFiles()
    {
        var reset = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"cache-reset-{Guid.NewGuid():N}",
        });
        Assert.True(reset.Ok, reset.Error?.Message);

        var gameRoot = Path.Combine(DataDirectory, "cache-game-root");
        Directory.CreateDirectory(gameRoot);
        var executable = Path.Combine(gameRoot, "CacheGame.exe");
        await File.WriteAllTextAsync(executable, "test-stub");
        var cover = Path.Combine(gameRoot, "cover.png");
        await File.WriteAllBytesAsync(cover, CreatePng(1280, 720));

        var addRoot = await InvokeAsync("roots.add", new { root = gameRoot });
        Assert.True(addRoot.Ok, addRoot.Error?.Message);
        var create = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"cache-game-{Guid.NewGuid():N}",
            sourcePath = executable,
        });
        Assert.True(create.Ok, create.Error?.Message);
        var gameId = create.Data.GetProperty("gameId").GetString()!;
        var import = await InvokeAsync("assets.import", new
        {
            idempotencyKey = $"cache-cover-{Guid.NewGuid():N}",
            gameId,
            sourcePath = cover,
        });
        Assert.True(import.Ok, import.Error?.Message);
        var assetId = import.Data.GetProperty("assetId").GetString()!;

        var cacheParent = Path.Combine(DataDirectory, "chosen-cache-parent");
        Directory.CreateDirectory(cacheParent);
        var parentFile = Path.Combine(cacheParent, "keep-me.txt");
        await File.WriteAllTextAsync(parentFile, "user-owned");
        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"cache-location-{Guid.NewGuid():N}",
            expectedRevision = reset.Data.GetProperty("revision").GetInt32(),
            cacheParentDirectory = cacheParent,
        });
        Assert.True(update.Ok, update.Error?.Message);

        var firstRead = await InvokeAsync("assets.get", new { assetId });
        Assert.True(firstRead.Ok, firstRead.Error?.Message);
        Assert.Equal("image/png", firstRead.Data.GetProperty("mimeType").GetString());

        var ownedRoot = Assert.Single(Directory.GetDirectories(
            Path.Combine(cacheParent, "GameLibraryCache")));
        var marker = Path.Combine(ownedRoot, ".gamelibrary-owner");
        var previews = Path.Combine(ownedRoot, "previews");
        Assert.True(File.Exists(marker));
        Assert.Single(Directory.GetFiles(previews, "*.png"));

        var rebuild = await InvokeAsync("diagnostics.cache_rebuild", new
        {
            idempotencyKey = $"cache-rebuild-{Guid.NewGuid():N}",
        });
        Assert.True(rebuild.Ok, rebuild.Error?.Message);
        Assert.True(rebuild.Data.GetProperty("removedFiles").GetInt64() >= 1);
        Assert.True(File.Exists(parentFile));
        Assert.True(File.Exists(marker));
        Assert.Empty(Directory.GetFiles(previews, "*.png"));

        var secondRead = await InvokeAsync("assets.get", new { assetId });
        Assert.True(secondRead.Ok, secondRead.Error?.Message);
        Assert.Single(Directory.GetFiles(previews, "*.png"));

        await File.WriteAllTextAsync(marker, "not-this-library");
        var refused = await InvokeAsync("diagnostics.cache_rebuild", new
        {
            idempotencyKey = $"cache-refuse-{Guid.NewGuid():N}",
        });
        Assert.False(refused.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, refused.Error!.Code);
        Assert.Single(Directory.GetFiles(previews, "*.png"));
        Assert.True(File.Exists(parentFile));

        var defaultLocation = await InvokeAsync("settings.update", new Dictionary<string, object?>
        {
            ["idempotencyKey"] = $"cache-default-{Guid.NewGuid():N}",
            ["expectedRevision"] = update.Data.GetProperty("revision").GetInt32(),
            ["cacheParentDirectory"] = null,
        });
        Assert.True(defaultLocation.Ok, defaultLocation.Error?.Message);
        Assert.Equal(JsonValueKind.Null,
            defaultLocation.Data.GetProperty("cacheParentDirectory").ValueKind);
        Assert.True(File.Exists(parentFile));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task CacheParent_InsideRegisteredGameRoot_IsRejectedWithoutCreatingCache()
    {
        var reset = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"cache-reset-{Guid.NewGuid():N}",
        });
        var gameRoot = Path.Combine(DataDirectory, "cache-boundary-root");
        var requested = Path.Combine(gameRoot, "cache-location");
        Directory.CreateDirectory(requested);
        var addRoot = await InvokeAsync("roots.add", new { root = gameRoot });
        Assert.True(addRoot.Ok, addRoot.Error?.Message);

        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"cache-boundary-{Guid.NewGuid():N}",
            expectedRevision = reset.Data.GetProperty("revision").GetInt32(),
            cacheParentDirectory = requested,
        });

        Assert.False(update.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, update.Error!.Code);
        Assert.Contains("游戏库", update.Error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(requested, "GameLibraryCache")));

        var occupiedParent = Path.Combine(DataDirectory, "cache-name-occupied");
        Directory.CreateDirectory(occupiedParent);
        await File.WriteAllTextAsync(Path.Combine(occupiedParent, "GameLibraryCache"), "user-file");
        var occupied = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"cache-occupied-{Guid.NewGuid():N}",
            expectedRevision = reset.Data.GetProperty("revision").GetInt32(),
            cacheParentDirectory = occupiedParent,
        });
        Assert.False(occupied.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, occupied.Error!.Code);
        Assert.Contains("文件占用", occupied.Error.Message, StringComparison.Ordinal);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(0x2d, 0x8c, 0xc4));
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
}
