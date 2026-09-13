using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Metadata;

/// <summary>
/// T14-B：fields.clear（用户清空）与 fields.reset（恢复自动值）、assets.choose/crop/reset/remove、
/// metadata.preview/refresh（AutoValue 更新不覆盖用户层）。
/// </summary>
public sealed class GameProfileLifecycleTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public GameProfileLifecycleTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "lifecycle-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters);
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    private async Task<(string GameId, int Revision)> CreateGameAsync(string prefix)
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "GameA"));
        File.WriteAllText(Path.Combine(root, "GameA", "Game.exe"), "x");
        File.WriteAllText(Path.Combine(root, "GameA", "data.xp3"), "x");

        await InvokeAsync("roots.add", new { root });
        for (var scan = 0; scan < 2; scan++)
        {
            var start = await InvokeAsync("scan.start", new { root });
            Assert.True(start.Ok, start.Error?.Message);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var status = await InvokeAsync("jobs.get", new { jobId = start.JobId! });
                if (status.Data.GetProperty("state").GetString() == "succeeded")
                {
                    break;
                }

                await Task.Delay(50);
            }
        }

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("kind").GetString() == "gameRoot"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var accept = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = $"accept-{item.GetProperty("candidateId").GetString()}",
            candidateId = item.GetProperty("candidateId").GetString(),
            expectedRevision = item.GetProperty("revision").GetInt32(),
        });
        Assert.True(accept.Ok, accept.Error?.Message);
        var gameId = accept.Data.GetProperty("gameId").GetString()!;
        var created = await InvokeAsync("games.get", new { gameId });
        return (gameId, created.Data.GetProperty("revision").GetInt32());
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(0x66, 0xc0, 0xf4));
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private async Task<string> CreateImageFileAsync(string prefix, int width, int height)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, CreatePng(width, height));
        return path;
    }

    [Fact]
    public async Task FieldsClear_ThenReset_RestoresAutoTitle()
    {
        var (gameId, revision) = await CreateGameAsync("lifecycle-fields");

        var set = await InvokeAsync("fields.set", new
        {
            idempotencyKey = $"set-{gameId}",
            gameId,
            field = "title",
            value = "用户标题",
            expectedRevision = revision,
        });
        Assert.True(set.Ok, set.Error?.Message);
        var afterSet = set.Data.GetProperty("revision").GetInt32();

        // 清空：value=null，title 镜像为空串（≠自动值）。
        var clear = await InvokeAsync("fields.clear", new
        {
            idempotencyKey = $"clear-{gameId}",
            gameId,
            field = "title",
            expectedRevision = afterSet,
        });
        Assert.True(clear.Ok, clear.Error?.Message);
        var afterClear = await InvokeAsync("games.get", new { gameId });
        Assert.Equal(string.Empty, afterClear.Data.GetProperty("title").GetString());
        Assert.Equal("user", afterClear.Data.GetProperty("titleSource").GetString());

        // 重置：回到自动层（首次 set 前登记的自动标题 = 根目录名 GameA）。
        var reset = await InvokeAsync("fields.reset", new
        {
            idempotencyKey = $"reset-{gameId}",
            gameId,
            field = "title",
            expectedRevision = clear.Data.GetProperty("revision").GetInt32(),
        });
        Assert.True(reset.Ok, reset.Error?.Message);
        var afterReset = await InvokeAsync("games.get", new { gameId });
        Assert.Equal("GameA", afterReset.Data.GetProperty("title").GetString());
        Assert.Equal("auto", afterReset.Data.GetProperty("titleSource").GetString());
    }

    [Fact]
    public async Task MetadataPreviewAndRefresh_UpdateAutoWithoutTouchingUserLayer()
    {
        var (gameId, revision) = await CreateGameAsync("lifecycle-meta");

        var preview = await InvokeAsync("metadata.preview", new { gameId });
        Assert.True(preview.Ok, preview.Error?.Message);
        Assert.Contains(preview.Data.GetProperty("suggestions").EnumerateArray(),
            s => s.GetProperty("field").GetString() == "title"
                && s.GetProperty("value").GetString() == "GameA");

        // 用户层先行，refresh 不得覆盖。
        await InvokeAsync("fields.set", new
        {
            idempotencyKey = $"user-{gameId}",
            gameId,
            field = "title",
            value = "用户保留",
            expectedRevision = revision,
        });

        var refresh = await InvokeAsync("metadata.refresh", new { idempotencyKey = $"meta-{gameId}", gameId });
        Assert.True(refresh.Ok, refresh.Error?.Message);
        Assert.NotNull(refresh.JobId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var job = await InvokeAsync("jobs.get", new { jobId = refresh.JobId! });
            if (job.Data.GetProperty("state").GetString() == "succeeded")
            {
                break;
            }

            await Task.Delay(50);
        }

        var after = await InvokeAsync("games.get", new { gameId });
        Assert.Equal("用户保留", after.Data.GetProperty("title").GetString());
        Assert.Equal("user", after.Data.GetProperty("titleSource").GetString());
    }

    [Fact]
    public async Task AssetsChooseCropResetRemove_FullLifecycle()
    {
        var (gameId, revision) = await CreateGameAsync("lifecycle-assets");

        // 两张封面：320x180 与 64x32。
        var cover1 = await CreateImageFileAsync("lc-c1", 320, 180);
        var cover2 = await CreateImageFileAsync("lc-c2", 64, 32);
        try
        {
            var import1 = await InvokeAsync("assets.import", new { idempotencyKey = "c1", gameId, sourcePath = cover1 });
            var import2 = await InvokeAsync("assets.import", new { idempotencyKey = "c2", gameId, sourcePath = cover2 });
            Assert.True(import1.Ok && import2.Ok);
            var asset1 = import1.Data.GetProperty("assetId").GetString()!;
            var asset2 = import2.Data.GetProperty("assetId").GetString()!;
            Assert.True(import2.Data.GetProperty("isCurrent").GetBoolean()); // 后导入者为当前

            // choose 切回第一张。
            var choose = await InvokeAsync("assets.choose", new
            {
                idempotencyKey = $"choose-{asset1}",
                gameId,
                assetId = asset1,
                expectedRevision = revision,
            });
            Assert.True(choose.Ok, choose.Error?.Message);
            var game = await InvokeAsync("games.get", new { gameId });
            Assert.Equal(asset1, game.Data.GetProperty("coverAssetId").GetString());

            // crop 第二张（64x32 → 32x16），新资产为当前。
            var crop = await InvokeAsync("assets.crop", new
            {
                idempotencyKey = $"crop-{asset2}",
                assetId = asset2,
                x = 0,
                y = 0,
                width = 32,
                height = 16,
            });
            Assert.True(crop.Ok, crop.Error?.Message);
            var croppedId = crop.Data.GetProperty("newAssetId").GetString()!;
            Assert.True(crop.Data.GetProperty("isCurrent").GetBoolean());

            // 越界裁切被拒绝。
            var outOfBounds = await InvokeAsync("assets.crop", new
            {
                idempotencyKey = $"crop-oob-{asset2}",
                assetId = asset2,
                x = 0,
                y = 0,
                width = 999,
                height = 16,
            });
            Assert.False(outOfBounds.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, outOfBounds.Error!.Code);

            // reset：无当前封面。
            var reset = await InvokeAsync("assets.reset", new { idempotencyKey = $"resetcover-{gameId}", gameId });
            Assert.True(reset.Ok, reset.Error?.Message);
            game = await InvokeAsync("games.get", new { gameId });
            Assert.True(game.Data.GetProperty("coverAssetId").ValueKind == JsonValueKind.Null);

            // remove：非当前资产可删；已删资产再删 → NotFound。
            var remove = await InvokeAsync("assets.remove", new { idempotencyKey = $"rm-{asset1}", assetId = asset1 });
            Assert.True(remove.Ok, remove.Error?.Message);
            var removeAgain = await InvokeAsync("assets.remove", new { idempotencyKey = $"rm2-{asset1}", assetId = asset1 });
            Assert.False(removeAgain.Ok);
            Assert.Equal(ErrorCodes.NotFound, removeAgain.Error!.Code);
            _ = croppedId;
        }
        finally
        {
            try
            {
                File.Delete(cover1);
                File.Delete(cover2);
            }
            catch (IOException)
            {
            }
        }
    }
}
