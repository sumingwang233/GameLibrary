using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Metadata;

/// <summary>
/// T14-A 资料与封面（经真实管道）：fields.set 用户来源与 Revision、assets.import
/// 复制入应用目录、5 MiB 原图及预览读取、格式与存在性校验。
/// </summary>
public sealed class GameProfileTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public GameProfileTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "profile-test",
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

    /// <summary>经扫描+接受创建一个游戏卡片，返回 (gameId, revision)。</summary>
    private async Task<(string GameId, int Revision)> CreateGameAsync(string prefix)
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "GameA"));
        File.WriteAllText(Path.Combine(root, "GameA", "Game.exe"), "x");
        File.WriteAllText(Path.Combine(root, "GameA", "data.xp3"), "x");

        await InvokeAsync("roots.add", new { root });
        for (var scan = 0; scan < 2; scan++)
        {
            var start = await InvokeAsync("scan.start", new { idempotencyKey = "scan-" + Guid.NewGuid().ToString("N"), root });
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

        // 游戏卡片 Revision 与候选 Revision 独立（卡片从 1 起）。
        var created = await InvokeAsync("games.get", new { gameId });
        var revision = created.Data.GetProperty("revision").GetInt32();
        return (gameId, revision);
    }

    [Fact]
    public async Task FieldsSet_TitleMirrorsToGamesList_WithRevisionGuard()
    {
        var (gameId, revision) = await CreateGameAsync("profile-fields");

        var stale = await InvokeAsync("fields.set", new
        {
            idempotencyKey = "stale-title",
            gameId,
            field = "title",
            value = "新标题",
            expectedRevision = revision - 1,
        });
        Assert.False(stale.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, stale.Error!.Code);

        var set = await InvokeAsync("fields.set", new
        {
            idempotencyKey = "set-title",
            gameId,
            field = "title",
            value = "我的自定义标题",
            expectedRevision = revision,
        });
        Assert.True(set.Ok, set.Error?.Message);
        Assert.Equal("user", set.Data.GetProperty("source").GetString());
        var newRevision = set.Data.GetProperty("revision").GetInt32();

        var get = await InvokeAsync("games.get", new { gameId });
        Assert.Equal("我的自定义标题", get.Data.GetProperty("title").GetString());
        Assert.Equal("user", get.Data.GetProperty("titleSource").GetString());
        Assert.Equal(newRevision, get.Data.GetProperty("revision").GetInt32());

        var badField = await InvokeAsync("fields.set", new
        {
            idempotencyKey = "bad-field",
            gameId,
            field = "developer",
            value = "x",
            expectedRevision = newRevision,
        });
        Assert.False(badField.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, badField.Error!.Code);
    }

    [Fact]
    public async Task AssetsFlow_ImportGetList_WithSizeAndFormatGuards()
    {
        var (gameId, _) = await CreateGameAsync("profile-assets");

        // 1x1 合法 PNG。
        var pngPath = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"cover-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(pngPath,
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ]);
        try
        {
            var import = await InvokeAsync("assets.import", new
            {
                idempotencyKey = "import-cover",
                gameId,
                sourcePath = pngPath,
            });
            Assert.True(import.Ok, import.Error?.Message);
            var assetId = import.Data.GetProperty("assetId").GetString()!;
            Assert.True(import.Data.GetProperty("isCurrent").GetBoolean());

            var get = await InvokeAsync("assets.get", new { assetId });
            Assert.True(get.Ok, get.Error?.Message);
            Assert.Equal("image/png", get.Data.GetProperty("mimeType").GetString());
            Assert.True(get.Data.GetProperty("dataBase64").GetString()!.Length > 0);

            var list = await InvokeAsync("assets.list", new { gameId });
            Assert.Equal(1, list.Data.GetProperty("total").GetInt32());

            var game = await InvokeAsync("games.get", new { gameId });
            Assert.Equal(assetId, game.Data.GetProperty("coverAssetId").GetString());
            var portableCover = Path.Combine(game.Data.GetProperty("rootPath").GetString()!, "cover.png");
            Assert.Equal(await File.ReadAllBytesAsync(pngPath), await File.ReadAllBytesAsync(portableCover));

            var missing = await InvokeAsync("assets.import", new
            {
                idempotencyKey = "import-missing",
                gameId,
                sourcePath = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"no-such-{Guid.NewGuid():N}.png"),
            });
            Assert.False(missing.Ok);
            Assert.Equal(ErrorCodes.NotFound, missing.Error!.Code);

            var badFormat = await InvokeAsync("assets.import", new
            {
                idempotencyKey = "import-badfmt",
                gameId,
                sourcePath = Path.ChangeExtension(pngPath, ".bmp"),
            });
            Assert.False(badFormat.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, badFormat.Error!.Code);
        }
        finally
        {
            try
            {
                File.Delete(pngPath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AssetsImport_FiveMiB_RoundTripsEvenWhenPreviewCannotDecode()
    {
        var (gameId, _) = await CreateGameAsync("profile-five-mib");
        var sourcePath = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"five-{Guid.NewGuid():N}.webp");
        // 强制原图回退；0xFB 在 Base64 中产生大量 '+'，覆盖 JSON 转义导致帧超限的风险。
        var bytes = Enumerable.Repeat((byte)0xFB, 5 * 1024 * 1024).ToArray();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        try
        {
            var imported = await InvokeAsync("assets.import", new { gameId, sourcePath, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.True(imported.Ok, imported.Error?.Message);
            var get = await InvokeAsync("assets.get", new { assetId = imported.Data.GetProperty("assetId").GetString() });
            Assert.True(get.Ok, get.Error?.Message);
            Assert.Equal(bytes, Convert.FromBase64String(get.Data.GetProperty("dataBase64").GetString()!));
            Assert.True((await InvokeAsync("games.get", new { gameId })).Ok);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task AssetsImport_OversizedImage_IsResourceTooLarge()
    {
        var (gameId, _) = await CreateGameAsync("profile-big");
        var bigPath = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"big-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(bigPath, new byte[(5 * 1024 * 1024) + 1]);
        try
        {
            var import = await InvokeAsync("assets.import", new
            {
                idempotencyKey = "import-big",
                gameId,
                sourcePath = bigPath,
            });
            Assert.False(import.Ok);
            Assert.Equal(ErrorCodes.ResourceTooLarge, import.Error!.Code);
        }
        finally
        {
            try
            {
                File.Delete(bigPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
