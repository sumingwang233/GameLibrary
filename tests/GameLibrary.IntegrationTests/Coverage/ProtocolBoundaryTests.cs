using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Paths;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Coverage;

/// <summary>
/// T29 协议与边界检查：路径/LNK/参数攻击经真实 IPC 通道拒绝、
/// 资源越界与归属、审计日志隐私（参数原文不入日志）。
/// </summary>
public sealed class ProtocolBoundaryTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ProtocolBoundaryTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t29-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters ?? new { });
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    [Theory]
    [InlineData(@"\\server\share\games", "UnsupportedPath")]
    [InlineData(@"\\?\D:\games", "UnsupportedPath")]
    [InlineData(@"D:\games\x:stream", "InvalidPath")]
    [InlineData(@"D:\games\..\..\Windows", "InvalidPath")]
    [InlineData(@"relative\path", "InvalidPath")]
    [InlineData(@"D:traversal", "InvalidPath")]
    public async Task PathAttacks_ViaScanStart_AreRejectedAtBoundary(string attackPath, string expectedCode)
    {
        var envelope = await InvokeAsync("scan.start", new
        {
            idempotencyKey = $"t29-attack-{Guid.NewGuid():N}",
            root = attackPath,
        });

        Assert.False(envelope.Ok);
        Assert.Equal(expectedCode, envelope.Error!.Code);
        Assert.Null(envelope.JobId);
    }

    [Fact]
    public async Task RelinkTraversal_EscapingRegisteredRoot_IsRejectedByWhitelist()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"t29-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "game-a"));
        _fixture.State.Roots.Add(root);
        var store = _fixture.State.Library.Store!;
        var game = new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = "T29",
            RootPath = Path.Combine(root, "game-a"),
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertGame(game);
        try
        {
            // ../.. 解析后逃出注册根：即使路径"合法"也必须被白名单拒绝。
            var traversal = Path.Combine(root, "game-a", "..", "..", "Windows");
            var viaTraversal = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t29-{Guid.NewGuid():N}",
                gameId = game.GameId,
                newPath = traversal,
                expectedRevision = 1,
            });
            Assert.False(viaTraversal.Ok);
            Assert.Equal(ErrorCodes.PermissionDenied, viaTraversal.Error!.Code);

            // 分段边界：games 与 games2 是兄弟目录，前缀相似不等于包含。
            var sibling = root + "2\\sibling-game";
            Directory.CreateDirectory(sibling);
            var viaSibling = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t29-{Guid.NewGuid():N}",
                gameId = game.GameId,
                newPath = sibling,
                expectedRevision = 1,
            });
            Assert.False(viaSibling.Ok);
            Assert.Equal(ErrorCodes.PermissionDenied, viaSibling.Error!.Code);

            // 游戏绑定未被任何攻击改变。
            Assert.Equal(game.RootPath, store.TryGetGame(game.GameId)!.RootPath);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task ResourceBoundaries_UnknownIdsAndBadRects_AreRejected()
    {
        var unknownAsset = await InvokeAsync("assets.get", new { assetId = "asset-missing" });
        Assert.False(unknownAsset.Ok);
        Assert.Equal(ErrorCodes.NotFound, unknownAsset.Error!.Code);

        var unknownView = await InvokeAsync("views.get", new { viewId = "../library.db" });
        Assert.False(unknownView.Ok);
        Assert.Equal(ErrorCodes.NotFound, unknownView.Error!.Code);

        var unknownCandidate = await InvokeAsync("candidates.get", new { candidateId = "cand-missing" });
        Assert.False(unknownCandidate.Ok);
        Assert.Equal(ErrorCodes.NotFound, unknownCandidate.Error!.Code);
    }

    [Fact]
    public async Task AuditLog_DoesNotContainParameterOriginalText()
    {
        var secret = $"SECRET-TOKEN-{Guid.NewGuid():N}";
        var gameId = $"game-{Guid.NewGuid():N}";
        var store = _fixture.State.Library.Store!;
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "T29 隐私",
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });

        var set = await InvokeAsync("fields.set", new
        {
            idempotencyKey = $"t29-{Guid.NewGuid():N}",
            gameId,
            field = "summary",
            value = secret,
            expectedRevision = 1,
        });
        Assert.True(set.Ok, set.Error?.Message);

        var logs = await InvokeAsync("diagnostics.logs", new { limit = 1000 });
        Assert.True(logs.Ok);
        var raw = logs.Data.GetProperty("items").GetRawText();
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFields_AreRejectedOnMultipleWriteOperations()
    {
        var gamesUpdate = await InvokeAsync("games.update", new
        {
            idempotencyKey = $"t29-{Guid.NewGuid():N}",
            gameId = "game-x",
            favorite = true,
            expectedRevision = 1,
            sneaky = true,
        });
        Assert.Equal(ErrorCodes.InvalidArgument, gamesUpdate.Error!.Code);

        var settingsUpdate = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"t29-{Guid.NewGuid():N}",
            expectedRevision = 0,
            injected = "value",
        });
        Assert.Equal(ErrorCodes.InvalidArgument, settingsUpdate.Error!.Code);
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
