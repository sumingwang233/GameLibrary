using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Pagination;

/// <summary>games.list 游标分页：nextOffset/hasMore，以及 limit&lt;=0 的全量旧语义。</summary>
public sealed class GameListPaginationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public GameListPaginationTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "cursor-test",
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

    [Fact]
    public async Task CursorPagination_ReturnsNextOffsetAndHasMore()
    {
        var search = $"cursor-{Guid.NewGuid():N}";
        var store = _fixture.State.Library.Store!;
        for (var i = 0; i < 65; i++)
        {
            var gameId = $"game-{Guid.NewGuid():N}";
            store.InsertGame(new GameCard
            {
                GameId = gameId,
                Title = $"{search}-{i:D3}",
                RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
                Kind = "GameRoot",
                Membership = "active",
                AcceptedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
        }

        // 第一页：65 条里取 60 条，hasMore=true、nextOffset 指向下一页。
        var firstPage = await InvokeAsync("games.list", new { search, sort = "title", limit = 60, offset = 0 });
        Assert.True(firstPage.Ok, firstPage.Error?.Message);
        Assert.Equal(65, firstPage.Data.GetProperty("total").GetInt32());
        Assert.Equal(60, firstPage.Data.GetProperty("items").GetArrayLength());
        Assert.True(firstPage.Data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(60, firstPage.Data.GetProperty("nextOffset").GetInt32());

        // 第二页：用 nextOffset 作为 offset，剩 5 条，已到末页。
        var secondPage = await InvokeAsync("games.list", new { search, sort = "title", limit = 60, offset = 60 });
        Assert.True(secondPage.Ok, secondPage.Error?.Message);
        Assert.Equal(65, secondPage.Data.GetProperty("total").GetInt32());
        Assert.Equal(5, secondPage.Data.GetProperty("items").GetArrayLength());
        Assert.False(secondPage.Data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secondPage.Data.GetProperty("nextOffset").ValueKind);

        // limit<=0 保持全量旧语义：一次返回全部，游标字段为 false/null。
        var full = await InvokeAsync("games.list", new { search, sort = "title", limit = 0, offset = 0 });
        Assert.True(full.Ok, full.Error?.Message);
        Assert.Equal(65, full.Data.GetProperty("items").GetArrayLength());
        Assert.False(full.Data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, full.Data.GetProperty("nextOffset").ValueKind);

        // 不带 limit（既有 CLI/MCP 消费方）同样是全量。
        var legacy = await InvokeAsync("games.list", new { search, sort = "title" });
        Assert.True(legacy.Ok, legacy.Error?.Message);
        Assert.Equal(65, legacy.Data.GetProperty("items").GetArrayLength());
        Assert.False(legacy.Data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, legacy.Data.GetProperty("nextOffset").ValueKind);
    }
}
