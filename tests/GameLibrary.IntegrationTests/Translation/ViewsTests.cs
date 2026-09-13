using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>T15-C：views 六操作（内置+自定义）、激活状态、games.list viewId 过滤。</summary>
public sealed class ViewsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ViewsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t15c-test",
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

    private string InsertGame(string title, bool favorite)
    {
        var store = _fixture.State.Library.Store!;
        var gameId = $"game-{Guid.NewGuid():N}";
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = title,
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            Favorite = favorite,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        return gameId;
    }

    [Fact]
    public async Task ViewsList_ShowsBuiltinsBeforeAnyCustom()
    {
        var envelope = await InvokeAsync("views.list");

        Assert.True(envelope.Ok, envelope.Error?.Message);
        var items = envelope.Data.GetProperty("items");
        var ids = items.EnumerateArray().Select(i => i.GetProperty("viewId").GetString()).ToList();
        Assert.Equal(["all", "favorites", "pending"], ids);
        Assert.All(items.EnumerateArray(), i => Assert.Equal("builtin", i.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task ViewsCreateUpdateRemove_LifecycleWithRevision()
    {
        var create = await InvokeAsync("views.create", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            name = "我的收藏搜索",
            search = "Test",
            favoriteOnly = true,
        });
        Assert.True(create.Ok, create.Error?.Message);
        var viewId = create.Data.GetProperty("viewId").GetString();
        Assert.Equal(1, create.Data.GetProperty("revision").GetInt32());

        var update = await InvokeAsync("views.update", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            viewId,
            name = "改名视图",
            expectedRevision = 1,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal(2, update.Data.GetProperty("revision").GetInt32());
        Assert.Equal("改名视图", update.Data.GetProperty("name").GetString());
        // 未提供的 filter 字段保持不变。
        Assert.True(update.Data.GetProperty("favoriteOnly").GetBoolean());

        var remove = await InvokeAsync("views.remove", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            viewId,
            expectedRevision = 2,
        });
        Assert.True(remove.Ok, remove.Error?.Message);

        var get = await InvokeAsync("views.get", new { viewId });
        Assert.False(get.Ok);
        Assert.Equal(ErrorCodes.NotFound, get.Error!.Code);
    }

    [Fact]
    public async Task ViewsActivate_BuiltinAndCustom_SetActiveState()
    {
        var activate = await InvokeAsync("views.activate", new
        {
            idempotencyKey = "t15c-act-favorites",
            viewId = "favorites",
        });
        Assert.True(activate.Ok, activate.Error?.Message);

        var create = await InvokeAsync("views.create", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            name = "激活目标",
        });
        var viewId = create.Data.GetProperty("viewId").GetString();
        var activateCustom = await InvokeAsync("views.activate", new
        {
            idempotencyKey = $"t15c-act-{viewId}",
            viewId,
        });
        Assert.True(activateCustom.Ok, activateCustom.Error?.Message);

        var list = await InvokeAsync("views.list");
        Assert.Equal(viewId, list.Data.GetProperty("activeViewId").GetString());
        var activeItems = list.Data.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("active").GetBoolean())
            .Select(i => i.GetProperty("viewId").GetString())
            .ToList();
        Assert.Single(activeItems, viewId);
    }

    [Fact]
    public async Task ViewsActivate_UnknownView_IsNotFound()
    {
        var envelope = await InvokeAsync("views.activate", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            viewId = "view-missing",
        });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.NotFound, envelope.Error!.Code);
    }

    [Fact]
    public async Task ViewsUpdateOrRemove_Builtin_IsRejected()
    {
        var update = await InvokeAsync("views.update", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            viewId = "all",
            name = "改不动",
            expectedRevision = 1,
        });
        Assert.False(update.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, update.Error!.Code);

        var remove = await InvokeAsync("views.remove", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            viewId = "favorites",
            expectedRevision = 1,
        });
        Assert.False(remove.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, remove.Error!.Code);
    }

    [Fact]
    public async Task GamesList_WithViewId_AppliesViewFilter()
    {
        var favGame = InsertGame($"收藏游戏 {Guid.NewGuid():N}", favorite: true);
        var plainGame = InsertGame($"普通游戏 {Guid.NewGuid():N}", favorite: false);

        var favorites = await InvokeAsync("games.list", new { viewId = "favorites" });
        Assert.True(favorites.Ok, favorites.Error?.Message);
        var favIds = favorites.Data.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("gameId").GetString()).ToList();
        Assert.Contains(favGame, favIds);
        Assert.DoesNotContain(plainGame, favIds);

        var create = await InvokeAsync("views.create", new
        {
            idempotencyKey = $"t15c-{Guid.NewGuid():N}",
            name = "只看收藏",
            favoriteOnly = true,
        });
        var viewId = create.Data.GetProperty("viewId").GetString();
        var viaCustom = await InvokeAsync("games.list", new { viewId });
        var customIds = viaCustom.Data.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("gameId").GetString()).ToList();
        Assert.Contains(favGame, customIds);
        Assert.DoesNotContain(plainGame, customIds);
    }
}
