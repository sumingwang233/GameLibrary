using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Launching;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Manual;

public sealed class GameExperienceTests(PipeServerFixture fixture) : IClassFixture<PipeServerFixture>
{
    private async Task<Envelope<JsonElement>> Invoke(string operation, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(fixture.State.DataDirectory, "experience-test", CancellationToken.None);
        return await client.InvokeAsync(new IpcRequest { RequestId = Guid.NewGuid().ToString("N"), OperationId = operation,
            Parameters = JsonSerializer.SerializeToElement(parameters, ContractJson.Options) }, CancellationToken.None);
    }

    private async Task<string> CreateUnityGame(bool file)
    {
        var root = Path.Combine(fixture.State.DataDirectory, "unity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Game_Data"));
        File.WriteAllText(Path.Combine(root, "Game.exe"), "Never execute");
        File.WriteAllText(Path.Combine(root, "UnityPlayer.dll"), "Never execute");
        File.WriteAllText(Path.Combine(root, "Game_Data", "globalgamemanagers"), "test");
        Assert.True((await Invoke("roots.add", new { root })).Ok);
        var created = await Invoke("games.create", new { sourcePath = file ? Path.Combine(root, "Game.exe") : root, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(created.Ok, created.Error?.Message);
        Assert.Equal("unity", created.Data.GetProperty("engine").GetString());
        return created.Data.GetProperty("gameId").GetString()!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualUnityGame_DetectsCanonicalEngine_AssignsEngineTag_AndUntranslatedTagTriggersSetup(bool file)
    {
        var id = await CreateUnityGame(file);
        var detail = await Invoke("games.get", new { gameId = id });
        Assert.Equal("unity", detail.Data.GetProperty("engine").GetString());
        Assert.Contains(detail.Data.GetProperty("tags").EnumerateArray(), tag => tag.GetProperty("kind").GetString() == "engine" && tag.GetProperty("name").GetString() == "unity");
        var tagId = fixture.State.Library.Store!.TryGetTagByName("user", "未翻译")?.TagId;
        if (tagId is null)
        {
            var tag = await Invoke("tags.create", new { name = "未翻译", idempotencyKey = Guid.NewGuid().ToString() });
            Assert.True(tag.Ok, tag.Error?.Message);
            tagId = tag.Data.GetProperty("tagId").GetString();
        }
        // Reproduce a manual card from the previous version, with an absent engine field.
        var store = fixture.State.Library.Store!;
        store.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE games SET engine = NULL WHERE game_id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
        var assigned = await Invoke("tags.assign", new { gameId = id, tagId, expectedRevision = store.TryGetGame(id)!.Revision, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(assigned.Ok, assigned.Error?.Message);
        Assert.Equal("unity", store.TryGetGame(id)!.Engine);
        var status = await Invoke("unity_translation.status", new { gameId = id });
        Assert.True(status.Ok, status.Error?.Message);
        Assert.Contains("needs_settings", status.Data.GetRawText());
        Assert.Empty(fixture.State.Launches.History());
    }

    [Fact]
    public async Task Preferences_PersistIndependently_ValidateStars_AndRejectStaleRevision()
    {
        var id = await CreateUnityGame(false);
        var original = fixture.State.Library.Store!.TryGetGame(id)!;
        var key = Guid.NewGuid().ToString();
        var patch = new { gameId = id, favorite = true, played = true, rating = 5, expectedRevision = original.Revision, idempotencyKey = key };
        var updated = await Invoke("games.update", patch);
        Assert.True(updated.Ok, updated.Error?.Message);
        Assert.True((await Invoke("games.update", patch)).Ok); // Durable replay does not apply twice.
        var saved = fixture.State.Library.Store!.TryGetGame(id)!;
        Assert.Equal(original.Revision + 1, saved.Revision);
        Assert.Equal(original.AcceptedUtc, saved.AcceptedUtc);
        Assert.True(saved.Played); Assert.True(saved.Favorite); Assert.Equal(5, saved.Rating);
        var cleared = await Invoke("games.update", new { gameId = id, favorite = (bool?)null, played = false, rating = 0, expectedRevision = saved.Revision, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(cleared.Ok, cleared.Error?.Message);
        var detail = (await Invoke("games.get", new { gameId = id })).Data;
        Assert.True(detail.GetProperty("favorite").GetBoolean());
        Assert.False(detail.GetProperty("played").GetBoolean()); Assert.Equal(0, detail.GetProperty("rating").GetInt32());
        var revision = detail.GetProperty("revision").GetInt32();
        foreach (object? rating in new object?[] { -1, 6, 1.5, "5", null })
        {
            var rejected = await Invoke("games.update", new { gameId = id, rating, expectedRevision = revision, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.False(rejected.Ok); Assert.Equal(ErrorCodes.InvalidArgument, rejected.Error!.Code);
        }
        var stale = await Invoke("games.update", new { gameId = id, rating = 2, expectedRevision = original.Revision, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.False(stale.Ok); Assert.Equal(ErrorCodes.RevisionConflict, stale.Error!.Code);
        Assert.Equal(revision, fixture.State.Library.Store.TryGetGame(id)!.Revision);
        var listed = (await Invoke("games.list", new { })).Data.GetProperty("items").EnumerateArray().Single(game => game.GetProperty("gameId").GetString() == id);
        Assert.False(listed.GetProperty("played").GetBoolean()); Assert.Equal(0, listed.GetProperty("rating").GetInt32());
    }

    [Fact]
    public async Task CliPlayedAndRating_ReachTheUpdateOperationThroughTheRealParserAndSerializer()
    {
        var id = await CreateUnityGame(false);
        var revision = fixture.State.Library.Store!.TryGetGame(id)!.Revision;
        string[] arguments = ["games", "update", "--game-id", id, "--played", "--rating", "5", "--expected-revision", revision.ToString(),
            "--data-dir", fixture.State.DataDirectory, "--no-start", "--format", "json"];
        // Execute only the CLI entry point, against the isolated pipe; no process or game launch.
        var exit = await Task.Run(() => (int)typeof(GameLibrary.Cli.CommandLine).Assembly.EntryPoint!.Invoke(null, [arguments])!);
        Assert.Equal(0, exit);
        var game = fixture.State.Library.Store.TryGetGame(id)!;
        Assert.True(game.Played); Assert.Equal(5, game.Rating);
    }

    [Theory]
    [InlineData("exited", 0)]
    [InlineData("exited", 1)]
    [InlineData("processStartFailed", 0)]
    public async Task FinishedAttempt_NeverMarksPlayed_AndDoesNotChangeRatingOrFavorite(string state, int code)
    {
        var id = await CreateUnityGame(false);
        var store = fixture.State.Library.Store!;
        var original = store.TryGetGame(id)!;
        Assert.NotNull(store.SetGamePreferences(id, true, null, 4, original.Revision, DateTime.UtcNow));
        var attempt = new LaunchAttempt { AttemptId = Guid.NewGuid().ToString(), IdempotencyKey = Guid.NewGuid().ToString(),
            GameId = id, ProfileId = "isolated-profile", State = state, ExecutablePath = Path.Combine(original.RootPath, "Game.exe"),
            WorkingDirectory = original.RootPath, Arguments = [], ProcessStartedUtc = state == "exited" ? DateTime.UtcNow.AddMinutes(-1) : null,
            FinishedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow.AddMinutes(-1), ExitCode = code };
        fixture.State.Launches.OnAttemptChanged!(attempt); // Production persistence hook; no process is started.
        var saved = store.TryGetGame(id)!;
        fixture.State.Launches.OnAttemptChanged!(attempt);
        Assert.Equal(saved.Revision, store.TryGetGame(id)!.Revision);
        Assert.False(saved.Played); Assert.True(saved.Favorite); Assert.Equal(4, saved.Rating);
        Assert.Equal(original.AcceptedUtc, saved.AcceptedUtc);
    }

    [Fact]
    public async Task PlayedLibraries_FilterBeforePaging_CombineFilters_AndMoveGamesWithoutLosingMetadata()
    {
        var store = fixture.State.Library.Store!;
        var prefix = "played-query-" + Guid.NewGuid().ToString("N");
        var cards = Enumerable.Range(0, 6).Select(i => new GameCard
        {
            GameId = Guid.NewGuid().ToString("N"), Title = $"{prefix}-{i}",
            RootPath = Path.Combine(fixture.State.DataDirectory, prefix, i.ToString()),
            Kind = "gameRoot", Membership = "active", Engine = "unity", Favorite = i < 4, Played = i % 2 == 1, Rating = 4,
            AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        }).ToArray();
        foreach (var card in cards) store.InsertGame(card);
        store.RemoveGame(cards[5].GameId, 1, new IgnoreRule
        {
            IgnoreId = Guid.NewGuid().ToString("N"), Scope = "ExactPath", Path = cards[5].RootPath,
            GameId = cards[5].GameId, Reason = "isolated test", CreatedUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);
        store.ImportAsset(cards[0].GameId, "isolated-cover.png", DateTime.UtcNow);
        store.ImportAsset(cards[1].GameId, "isolated-cover.png", DateTime.UtcNow);
        var tag = await Invoke("tags.create", new { name = prefix, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(tag.Ok, tag.Error?.Message);
        var tagId = tag.Data.GetProperty("tagId").GetString()!;
        foreach (var index in new[] { 0, 1, 4 }) store.AssignTag(cards[index].GameId, tagId, DateTime.UtcNow);

        foreach (var played in new[] { false, true })
        {
            var expected = cards.Take(5).Where(game => game.Played == played).ToArray();
            var ids = new List<string>();
            for (var offset = 0; offset <= expected.Length; offset++)
            {
                var page = await Invoke("games.list", new { search = prefix, played, sort = "title-asc", limit = 1, offset });
                Assert.True(page.Ok, page.Error?.Message);
                Assert.Equal(expected.Length, page.Data.GetProperty("total").GetInt32());
                foreach (var item in page.Data.GetProperty("items").EnumerateArray())
                {
                    Assert.Equal(played, item.GetProperty("played").GetBoolean());
                    ids.Add(item.GetProperty("gameId").GetString()!);
                }
            }
            Assert.Equal(expected.Select(game => game.GameId), ids);
            var combined = await Invoke("games.list", new { search = prefix, played, favorite = true, hasCover = true, tagId });
            Assert.True(combined.Ok, combined.Error?.Message);
            Assert.Equal(1, combined.Data.GetProperty("total").GetInt32());
            Assert.Equal(cards[played ? 1 : 0].GameId,
                Assert.Single(combined.Data.GetProperty("items").EnumerateArray()).GetProperty("gameId").GetString());
        }
        foreach (var parameters in new object[] { new { search = prefix }, new { search = prefix, played = (bool?)null } })
        {
            var all = await Invoke("games.list", parameters);
            Assert.True(all.Ok, all.Error?.Message);
            Assert.Equal(5, all.Data.GetProperty("total").GetInt32());
        }
        var before = store.TryGetGame(cards[0].GameId)!;
        foreach (var played in new[] { true, false })
        {
            var game = store.TryGetGame(before.GameId)!;
            var updated = await Invoke("games.update", new { gameId = game.GameId, played,
                expectedRevision = game.Revision, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.True(updated.Ok, updated.Error?.Message);
            var destination = await Invoke("games.list", new { search = prefix + "-0", played });
            Assert.Equal(before.GameId, Assert.Single(destination.Data.GetProperty("items").EnumerateArray()).GetProperty("gameId").GetString());
            var other = await Invoke("games.list", new { search = prefix + "-0", played = !played });
            Assert.Equal(0, other.Data.GetProperty("total").GetInt32());
            var saved = store.TryGetGame(before.GameId)!;
            Assert.Equal(before.Favorite, saved.Favorite);
            Assert.Equal(before.Rating, saved.Rating);
            Assert.Equal(before.AcceptedUtc, saved.AcceptedUtc);
            Assert.Single(store.ListGameTags(saved.GameId));
            Assert.Single(store.ListAssets(saved.GameId));
        }
    }

    [Fact]
    public async Task PlayedLibrary_RejectsNonBooleanInputs()
    {
        foreach (var raw in new[] { "\"false\"", "0", "[]" })
        {
            var rejected = await Invoke("games.list", new { played = JsonSerializer.Deserialize<JsonElement>(raw) });
            Assert.False(rejected.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, rejected.Error!.Code);
        }
    }

    [Fact]
    public async Task UnknownBackfill_PreservesRecognizedEngine_AndRespectsSuppressedAutomaticTags()
    {
        var id = await CreateUnityGame(false);
        var store = fixture.State.Library.Store!;
        var game = store.TryGetGame(id)!;
        Assert.False(store.SetUnknownGameEngine(id, "unreal", game.Revision, DateTime.UtcNow));
        Assert.Equal("unity", store.TryGetGame(id)!.Engine);
        var unknownId = "unknown-" + Guid.NewGuid().ToString("N");
        store.InsertGame(game with { GameId = unknownId, Engine = null });
        store.SetTagOverride(unknownId, "engine", "unity", "suppress", DateTime.UtcNow);
        var recognition = new GameEngineRecognition(() => store, fixture.State.Roots, fixture.State.Events);
        Assert.Contains(unknownId, recognition.Reconcile());
        Assert.Equal("unity", store.TryGetGame(unknownId)!.Engine);
        Assert.DoesNotContain(store.ListGameTags(unknownId), tag => tag.Kind == "engine");
        Assert.Empty(recognition.Reconcile());
    }
}
