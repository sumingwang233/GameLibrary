using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Games;

/// <summary>
/// optimize-v1.7.23：批量写操作（games.update_batch / games.remove_batch / tags.assign_batch）。
/// 走真实 IPC + 真实 SQLite 库：验证逐项回执同序、单项失败不影响其它项、幂等重放、
/// 以及整批级参数校验（重复 gameId、超限）。
/// </summary>
public sealed class BatchWritesTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public BatchWritesTests(PipeServerFixture fixture) => _fixture = fixture;

    private SqliteLibraryStore Store => _fixture.State.Library.Store!;

    private async Task<Envelope<JsonElement>> InvokeAsync(string operation, object parameters)
    {
        await using var connection = await HostConnection.ConnectAsync(
            _fixture.State.DataDirectory,
            "batch-writes-test",
            CancellationToken.None);
        return await connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                OperationId = operation,
                Parameters = JsonSerializer.SerializeToElement(parameters),
            },
            CancellationToken.None);
    }

    private string AddGame(string title)
    {
        var id = "game-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(_fixture.State.DataDirectory, "test-games", id);
        Directory.CreateDirectory(root);
        Store.InsertGame(new GameCard
        {
            GameId = id,
            Title = title,
            RootPath = root,
            Kind = "folderGame",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        return id;
    }

    private string AddTag(string name)
    {
        var id = "tag-" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        Store.CreateTag(new PersistedTag(id, "user", name, null, 1, 0, now, now));
        return id;
    }

    [Fact]
    public async Task GamesUpdateBatch_AppliesEachItemAndIsolatesRevisionConflict()
    {
        var first = AddGame("Batch First");
        var second = AddGame("Batch Second");

        var response = await InvokeAsync("games.update_batch", new
        {
            items = new object[]
            {
                new { gameId = first, expectedRevision = 1, favorite = true, rating = 5 },
                new { gameId = second, expectedRevision = 9, played = true },
            },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        Assert.True(response.Ok, response.Error?.Message);
        var items = response.Data.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(first, items[0].GetProperty("gameId").GetString());
        Assert.True(items[0].GetProperty("ok").GetBoolean());
        Assert.Equal(2, items[0].GetProperty("revision").GetInt32());
        Assert.Equal(second, items[1].GetProperty("gameId").GetString());
        Assert.False(items[1].GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.RevisionConflict, items[1].GetProperty("errorCode").GetString());
        Assert.Equal(1, response.Data.GetProperty("updated").GetInt32());
        Assert.Equal(1, response.Data.GetProperty("failed").GetInt32());

        var updated = Store.TryGetGame(first)!;
        Assert.True(updated.Favorite);
        Assert.Equal(5, updated.Rating);
        Assert.Equal(2, updated.Revision);

        var untouched = Store.TryGetGame(second)!;
        Assert.False(untouched.Played);
        Assert.Equal(1, untouched.Revision);
    }

    [Fact]
    public async Task GamesUpdateBatch_ReplaysIdempotencyKeyWithoutWritingAgain()
    {
        var game = AddGame("Batch Replay");
        var input = new
        {
            items = new object[] { new { gameId = game, expectedRevision = 1, played = true } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        };

        var first = await InvokeAsync("games.update_batch", input);
        var replay = await InvokeAsync("games.update_batch", input);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.Equal(first.Data.GetRawText(), replay.Data.GetRawText());
        Assert.Equal(2, Store.TryGetGame(game)!.Revision);
    }

    [Fact]
    public async Task GamesUpdateBatch_ReportsPerItemInvalidArgumentWithoutFailingBatch()
    {
        var valid = AddGame("Batch Valid");
        var invalid = AddGame("Batch Invalid");

        var response = await InvokeAsync("games.update_batch", new
        {
            items = new object[]
            {
                new { gameId = valid, expectedRevision = 1, favorite = true },
                new { gameId = invalid, expectedRevision = 1, rating = 9 },
            },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        Assert.True(response.Ok, response.Error?.Message);
        var items = response.Data.GetProperty("items");
        Assert.True(items[0].GetProperty("ok").GetBoolean());
        Assert.False(items[1].GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.InvalidArgument, items[1].GetProperty("errorCode").GetString());
        Assert.Equal(1, Store.TryGetGame(invalid)!.Revision);
    }

    [Fact]
    public async Task GamesUpdateBatch_RejectsDuplicateAndOversizedItems()
    {
        var game = AddGame("Batch Duplicate");
        var duplicate = await InvokeAsync("games.update_batch", new
        {
            items = new object[]
            {
                new { gameId = game, expectedRevision = 1, favorite = true },
                new { gameId = game, expectedRevision = 1, played = true },
            },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.False(duplicate.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, duplicate.Error?.Code);

        var oversized = await InvokeAsync("games.update_batch", new
        {
            items = Enumerable.Range(0, 1001)
                .Select(index => new { gameId = $"overflow-{index}", expectedRevision = 1, favorite = true })
                .ToArray(),
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.False(oversized.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, oversized.Error?.Code);

        Assert.Equal(1, Store.TryGetGame(game)!.Revision);
    }

    [Fact]
    public async Task TagsAssignBatch_AssignsTagAndKeepsAlreadyPresentItemSuccessful()
    {
        var first = AddGame("Tag Batch First");
        var second = AddGame("Tag Batch Second");
        var tagId = AddTag("batch-tag");
        var key = Guid.NewGuid().ToString("N");

        var response = await InvokeAsync("tags.assign_batch", new
        {
            tagId,
            items = new object[]
            {
                new { gameId = first, expectedRevision = 1 },
                new { gameId = second, expectedRevision = 9 },
            },
            idempotencyKey = key,
        });

        Assert.True(response.Ok, response.Error?.Message);
        var items = response.Data.GetProperty("items");
        Assert.True(items[0].GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.RevisionConflict, items[1].GetProperty("errorCode").GetString());
        Assert.Contains(Store.ListGameTags(first), tag => tag.Name == "batch-tag");
        Assert.Empty(Store.ListGameTags(second));

        // 已挂项再提交一次：仍算成功，且不重复建关联、不 bump 游戏 Revision。
        var replay = await InvokeAsync("tags.assign_batch", new
        {
            tagId,
            items = new object[] { new { gameId = first, expectedRevision = 1 } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.True(replay.Data.GetProperty("items")[0].GetProperty("ok").GetBoolean());
        Assert.Single(Store.ListGameTags(first));
        Assert.Equal(1, Store.TryGetGame(first)!.Revision);
    }

    [Fact]
    public async Task TagsAssignBatch_ReportsUnknownTagAtEnvelopeLevel()
    {
        var game = AddGame("Tag Batch Unknown");

        var response = await InvokeAsync("tags.assign_batch", new
        {
            tagId = "tag-missing-" + Guid.NewGuid().ToString("N"),
            items = new object[] { new { gameId = game, expectedRevision = 1 } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.NotFound, response.Error?.Code);
        Assert.Empty(Store.ListGameTags(game));
    }

    [Fact]
    public async Task GamesRemoveBatch_MarksRemovedAndIsolatesFailedItem()
    {
        var first = AddGame("Remove First");
        var second = AddGame("Remove Second");

        var response = await InvokeAsync("games.remove_batch", new
        {
            items = new object[]
            {
                new { gameId = first, expectedRevision = 1 },
                new { gameId = second, expectedRevision = 9 },
            },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        Assert.True(response.Ok, response.Error?.Message);
        var items = response.Data.GetProperty("items");
        Assert.True(items[0].GetProperty("ok").GetBoolean());
        Assert.Equal(2, items[0].GetProperty("revision").GetInt32());
        Assert.Equal(ErrorCodes.RevisionConflict, items[1].GetProperty("errorCode").GetString());

        var removed = Store.TryGetGame(first)!;
        Assert.Equal("removed", removed.Membership);
        Assert.Equal(2, removed.Revision);
        // 与单条 games.remove 一致：登记精确路径忽略规则，避免重扫复活。
        Assert.Contains(Store.ListIgnoreRules(), rule => rule.GameId == first && rule.Path == removed.RootPath);
        Assert.Equal("active", Store.TryGetGame(second)!.Membership);

        // 已移除项再次批量提交：逐项失败，不抛整批错误。
        var again = await InvokeAsync("games.remove_batch", new
        {
            items = new object[] { new { gameId = first, expectedRevision = 2 } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.True(again.Ok, again.Error?.Message);
        Assert.False(again.Data.GetProperty("items")[0].GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.InvalidArgument, again.Data.GetProperty("items")[0].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task GamesRemoveBatch_RejectsDuplicateGameIds()
    {
        var game = AddGame("Remove Duplicate");

        var response = await InvokeAsync("games.remove_batch", new
        {
            items = new object[]
            {
                new { gameId = game, expectedRevision = 1 },
                new { gameId = game, expectedRevision = 1 },
            },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Equal("active", Store.TryGetGame(game)!.Membership);
    }
}
