using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLibrary.IntegrationTests.Launching;

public sealed class LaunchSuggestionHostTests
{
    [Fact]
    public async Task BackgroundDiscovery_WakesPromptlyForGameEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameLibrary-suggestion-event-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        var opened = await SqliteLibraryStore.InitializeAsync(data, new SqliteLibraryStoreOptions { AppVersion = "test", ApiVersion = "1" }, CancellationToken.None);
        await using (var store = opened.Store!)
        {
            store.UpsertRoot(new PersistedRoot("root", root, 1, DateTime.UtcNow), DateTime.UtcNow);
            store.CreateTag(new("tag-trigger", "user", "trigger", null, 1, 0, DateTime.UtcNow, DateTime.UtcNow));
        }
        await using var host = await HostRuntime.StartAsync(data, NullLoggerFactory.Instance);
        await using var client = await HostConnection.ConnectAsync(data, "suggestion-events", CancellationToken.None);
        await Task.Delay(100); // Initial empty pass has no filesystem work.
        var gameDirectory = Path.Combine(root, "new-game");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "Game.exe"), "Never execute");
        host.Library.Store!.InsertGame(new GameCard { GameId = "game", Title = "New", RootPath = gameDirectory, Kind = "folderGame", Membership = "active", AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        var updated = await client.InvokeAsync(new IpcRequest
        {
            RequestId = "change",
            OperationId = "tags.assign",
            Parameters = JsonSerializer.SerializeToElement(new { gameId = "game", tagId = "tag-trigger", expectedRevision = 1, idempotencyKey = "change" })
        }, CancellationToken.None);
        Assert.True(updated.Ok, updated.Error?.Message);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (host.Library.Store.ReadProfiles().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Single(host.Library.Store.ReadProfiles());
        Assert.Empty(host.Library.Store.ReadLaunchAttempts());
    }

    [Fact]
    public async Task DiscoveryJobAndDeletedSuppressionSurviveHostRestart()
    {
        var root = Path.Combine("D:/Official/GameLibrary/artifacts/test-runs", "suggest-host-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var gameDirectory = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "game.exe"), "Discovery never executes this file");
        File.WriteAllText(Path.Combine(gameDirectory, "other.exe"), "Another registered game; never execute");
        var initial = await SqliteLibraryStore.InitializeAsync(data, new SqliteLibraryStoreOptions { AppVersion = "1.6.1", ApiVersion = "1" }, CancellationToken.None);
        await using (var store = initial.Store!)
        {
            store.InsertGame(new GameCard { GameId = "game", Title = "Test", RootPath = gameDirectory, EntryPath = Path.Combine(gameDirectory, "game.exe"), Kind = "folderGame", Membership = "active", AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            store.InsertGame(new GameCard { GameId = "other", Title = "Other", RootPath = Path.Combine(gameDirectory, "other.exe"), EntryPath = Path.Combine(gameDirectory, "other.exe"), Kind = "manualFile", Membership = "active", AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            store.UpsertRoot(new PersistedRoot("root", root, 1, DateTime.UtcNow), DateTime.UtcNow);
        }
        string? removedId = null;
        for (var run = 0; run < 2; run++)
        {
            await using var host = await HostRuntime.StartAsync(data, NullLoggerFactory.Instance);
            await using var client = await HostConnection.ConnectAsync(data, "suggest-host-test", CancellationToken.None);
            async Task<Envelope<JsonElement>> Invoke(string operation, object parameters)
                => await client.InvokeAsync(new IpcRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    OperationId = operation,
                    Parameters = JsonSerializer.SerializeToElement(parameters)
                }, CancellationToken.None);
            var accepted = await Invoke("profiles.discover", new { gameId = "game", idempotencyKey = "discover-" + run });
            Assert.True(accepted.Ok, accepted.Error?.Message);
            Assert.Equal(OperationStatus.Accepted, accepted.Status);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var job = await Invoke("jobs.get", new { jobId = accepted.JobId });
                Assert.True(job.Ok, job.Error?.Message);
                var state = job.Data.GetProperty("state").GetString();
                if (state == "succeeded") break;
                Assert.DoesNotContain(state, new[] { "failed", "cancelled" });
                Assert.True(DateTime.UtcNow < deadline);
                await Task.Delay(50);
            }
            var list = await Invoke("profiles.list", new { gameId = "game" });
            var items = list.Data.GetProperty("items");
            if (run == 0)
            {
                Assert.Equal(1, items.GetArrayLength());
                var profile = items[0];
                removedId = profile.GetProperty("profileId").GetString();
                Assert.Equal(removedId, list.Data.GetProperty("recommendedProfileId").GetString());
                Assert.Equal("automatic", profile.GetProperty("source").GetString());
                var removed = await Invoke("profiles.remove", new { profileId = removedId, expectedRevision = profile.GetProperty("revision").GetInt32(), idempotencyKey = "remove" });
                Assert.True(removed.Ok, removed.Error?.Message);
                Assert.Equal("deleted", Assert.Single(host.Library.Store!.ReadProfiles(), profile => profile.GameId == "game").ValidationStatus);
            }
            else
            {
                Assert.Equal(0, items.GetArrayLength());
                Assert.Equal(JsonValueKind.Null, list.Data.GetProperty("recommendedProfileId").ValueKind);
                var hidden = await Invoke("profiles.get", new { profileId = removedId });
                Assert.False(hidden.Ok);
                Assert.Equal("NotFound", hidden.Error!.Code);
            }
        }
    }
}
