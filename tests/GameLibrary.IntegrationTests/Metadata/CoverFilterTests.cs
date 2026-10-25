using System.Text.Json;
using GameLibrary.Cli;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Metadata;

public sealed class CoverFilterTests(PipeServerFixture fixture) : IClassFixture<PipeServerFixture>
{
    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            fixture.State.DataDirectory, "cover-filter-test", CancellationToken.None);
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            OperationId = operationId,
            Parameters = JsonSerializer.SerializeToElement(parameters, ContractJson.Options),
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CurrentCoverFiltersBeforePaging_AndResetHistoryCountsAsNoCover()
    {
        var store = fixture.State.Library.Store!;
        var prefix = $"cover-query-{Guid.NewGuid():N}";
        var games = Enumerable.Range(0, 5).Select(i => new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = $"{prefix}-{i}",
            RootPath = Path.Combine(fixture.State.DataDirectory, $"game-{i}"),
            Kind = "gameRoot",
            Membership = "active",
            Favorite = i < 2,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        }).ToArray();
        foreach (var game in games) store.InsertGame(game);
        var first = store.ImportAsset(games[0].GameId, "test-cover.png", DateTime.UtcNow);
        store.ImportAsset(games[0].GameId, "replacement.png", DateTime.UtcNow);
        store.ImportAsset(games[1].GameId, "test-cover.png", DateTime.UtcNow);
        store.ImportAsset(games[2].GameId, "historical.png", DateTime.UtcNow);
        store.ResetCover(games[2].GameId);
        Assert.Single(store.ListAssets(games[2].GameId));
        store.RemoveGame(games[4].GameId, 1, new IgnoreRule
        {
            IgnoreId = $"ignore-{Guid.NewGuid():N}",
            Scope = "ExactPath",
            Path = games[4].RootPath,
            GameId = games[4].GameId,
            Reason = "test",
            CreatedUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);

        foreach (var (cover, expected) in new[] { (true, games.Take(2)), (false, games.Skip(2).Take(2)) })
        {
            var ids = new List<string>();
            foreach (var offset in new[] { 0, 1, 2 })
            {
                var page = await InvokeAsync("games.list", new { search = prefix, hasCover = cover, sort = "title", limit = 1, offset });
                Assert.True(page.Ok, page.Error?.Message);
                Assert.Equal(2, page.Data.GetProperty("total").GetInt32());
                foreach (var item in page.Data.GetProperty("items").EnumerateArray())
                {
                    ids.Add(item.GetProperty("gameId").GetString()!);
                    Assert.Equal(cover, item.GetProperty("coverAssetId").ValueKind != JsonValueKind.Null);
                }
            }
            Assert.Equal(expected.Select(game => game.GameId), ids);
        }
        var all = await InvokeAsync("games.list", new { search = prefix, hasCover = (bool?)null });
        Assert.Equal(4, all.Data.GetProperty("total").GetInt32());
        var favoriteWithoutCover = await InvokeAsync("games.list", new { search = prefix, favorite = true, hasCover = false });
        Assert.Equal(0, favoriteWithoutCover.Data.GetProperty("total").GetInt32());
        store.ChooseAsset(games[0].GameId, first.AssetId);
        store.ResetCover(games[1].GameId);
        Assert.Equal(1, store.QueryGames(prefix, false, null, "title", 1, 0, true).Total);
        Assert.Equal(3, store.QueryGames(prefix, false, null, "title", 1, 0, false).Total);
    }

    [Fact]
    public async Task SavedViewPreservesFalseOnPatch_AndExplicitNullClearsIt()
    {
        var search = $"cover-view-{Guid.NewGuid():N}";
        var store = fixture.State.Library.Store!;
        var games = Enumerable.Range(0, 2).Select(i => new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = $"{search}-{i}",
            RootPath = Path.Combine(fixture.State.DataDirectory, search, i.ToString()),
            Kind = "gameRoot",
            Membership = "active",
            Favorite = true,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        }).ToArray();
        foreach (var game in games) store.InsertGame(game);
        store.ImportAsset(games[0].GameId, "test-cover.png", DateTime.UtcNow);
        var created = await InvokeAsync("views.create", new { name = search, search, hasCover = false, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        var viewId = created.Data.GetProperty("viewId").GetString()!;
        Assert.False(created.Data.GetProperty("hasCover").GetBoolean());
        var viaView = await InvokeAsync("games.list", new { viewId, hasCover = true, limit = 1 });
        Assert.True(viaView.Ok, viaView.Error?.Message);
        Assert.Equal(1, viaView.Data.GetProperty("total").GetInt32());
        Assert.Equal(games[1].GameId, Assert.Single(viaView.Data.GetProperty("items").EnumerateArray()).GetProperty("gameId").GetString());
        var updated = await InvokeAsync("views.update", new { viewId, favoriteOnly = true, expectedRevision = 1, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(updated.Ok, updated.Error?.Message);
        Assert.False(updated.Data.GetProperty("hasCover").GetBoolean());
        var got = await InvokeAsync("views.get", new { viewId });
        Assert.False(got.Data.GetProperty("hasCover").GetBoolean());
        var listed = await InvokeAsync("views.list", new { });
        Assert.False(listed.Data.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("viewId").GetString() == viewId).GetProperty("hasCover").GetBoolean());
        Assert.False(fixture.State.Library.Store!.TryGetView(viewId)!.HasCover);
        var cleared = await InvokeAsync("views.update", new { viewId, hasCover = (bool?)null, expectedRevision = 2, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(cleared.Ok, cleared.Error?.Message);
        Assert.Equal(JsonValueKind.Null, cleared.Data.GetProperty("hasCover").ValueKind);
        Assert.Null(fixture.State.Library.Store.TryGetView(viewId)!.HasCover);
        var all = await InvokeAsync("games.list", new { viewId, limit = 1 });
        Assert.Equal(2, all.Data.GetProperty("total").GetInt32());
        var withCover = await InvokeAsync("views.update", new { viewId, hasCover = true, expectedRevision = 3, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(withCover.Ok, withCover.Error?.Message);
        var covered = await InvokeAsync("games.list", new { viewId, limit = 1 });
        Assert.Equal(games[0].GameId, Assert.Single(covered.Data.GetProperty("items").EnumerateArray()).GetProperty("gameId").GetString());
        Assert.Null(LibraryView.ParseFilter("{}").HasCover);
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("[]")]
    public async Task InvalidCoverTypeIsRejected(string json)
    {
        var hasCover = JsonSerializer.Deserialize<JsonElement>(json);
        foreach (var operation in new[] { "games.list", "views.create" })
        {
            var result = await InvokeAsync(operation, new { hasCover, name = "bad-cover-filter", idempotencyKey = Guid.NewGuid().ToString() });
            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
        }
    }

    [Theory]
    [InlineData("--with-cover", true)]
    [InlineData("--without-cover", false)]
    [InlineData("--all-covers", null)]
    public void CliPreservesCoverTristate(string flag, bool? expected)
    {
        var parsed = CommandLine.Parse(["games", "list", flag]);
        Assert.True(parsed.IsValid, parsed.Error);
        Assert.True(parsed.HasCoverSpecified);
        Assert.Equal(expected, parsed.HasCover);
        Assert.False(CommandLine.Parse(["views", "update"]).HasCoverSpecified);
    }
}
