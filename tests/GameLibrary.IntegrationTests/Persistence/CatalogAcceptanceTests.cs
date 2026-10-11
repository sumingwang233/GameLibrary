using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Domain.Catalog;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

public sealed class CatalogAcceptanceTests
{
    private static SqliteLibraryStoreOptions Options(int version = 28) => new()
    { AppVersion = "test", ApiVersion = "1", Migrations = DatabaseMigrations.All.Where(m => m.Version <= version).ToArray() };

    private static GameCard Game(string id, string directory, DateTime added, string membership = "active") => new()
    {
        GameId = id, Title = id, RootPath = Path.Combine(directory, id), Kind = "directoryGame", Engine = "unity",
        Membership = membership, Favorite = true, TranslationOverride = "Required", AcceptedUtc = added, UpdatedUtc = added,
    };

    private static void Insert(SqliteLibraryStore store, GameCard game)
    {
        // InsertGame intentionally creates active rows without user overrides. Seed real
        // removed/overridden state through the production mutation methods instead.
        store.InsertGame(game);
        var revision = store.SetTranslationOverride(game.GameId, game.TranslationOverride, 1, game.UpdatedUtc)!.Value;
        if (game.Membership == "removed") Assert.NotNull(store.RemoveGame(game.GameId, revision,
            new() { IgnoreId = "ignore-" + game.GameId, Scope = "ExactPath", Path = game.RootPath, GameId = game.GameId,
                Reason = "removed by user", CreatedUtc = game.UpdatedUtc }, game.UpdatedUtc));
    }

    [Fact]
    public async Task ReacceptRemovedGame_UpdatesAdditionTime_PreservesMetadata_AndReplayDoesNotReorder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-acceptance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opened = await SqliteLibraryStore.InitializeAsync(directory, Options(), CancellationToken.None);
            await using var store = opened.Store!;
            var now = DateTime.UtcNow;
            var old = Game("restored", directory, now.AddDays(-20), "removed");
            Insert(store, old);
            Insert(store, Game("previous", directory, now.AddDays(-1)));
            store.UpsertCandidate(new() { CandidateId = "candidate", Kind = old.Kind, RelativePath = "restored", PhysicalPath = old.RootPath,
                PayloadJson = "{}", ReviewState = "pendingReview", ObservedUtc = now, UpdatedUtc = now });
            var outcome = store.AcceptCandidate("candidate", 1, old with { GameId = "replacement", Membership = "active" }, "unity", now);
            Assert.Equal(old.GameId, outcome.GameId);
            var restored = store.TryGetGame(old.GameId)!;
            Assert.Equal(now, restored.AcceptedUtc);
            Assert.Equal(old.Title, restored.Title);
            Assert.Equal(old.Favorite, restored.Favorite);
            Assert.Equal(old.TranslationOverride, restored.TranslationOverride);
            Assert.Equal("active", restored.Membership);
            Assert.Equal(old.GameId, store.QueryGames(null, false, null, "accepted-desc", 50, 0).Items.First().GameId);
            var replay = store.AcceptCandidate("candidate", outcome.Candidate.Revision, old, "unity", now.AddDays(1));
            Assert.Equal("alreadyAccepted", replay.Status);
            Assert.Equal(restored, store.TryGetGame(old.GameId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Upgrade28_RepairsOnlyProvenCurrentEpochAccepts_AndDoesNotUseEditsOrRescans()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-accept-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opened = await SqliteLibraryStore.InitializeAsync(directory, Options(27), CancellationToken.None);
            var added = DateTime.UtcNow.AddDays(-20);
            var accepted = DateTime.UtcNow.AddHours(-1);
            string epoch;
            await using (var store = opened.Store!)
            {
                epoch = store.Info.DataEpoch;
                foreach (var id in new[] { "accepted", "edited", "old-epoch", "removed", "manual" })
                    Insert(store, Game(id, directory, added, id == "removed" ? "removed" : "active"));
                store.WriteExclusive((connection, _) =>
                {
                    long sequence = 0;
                    foreach (var id in new[] { "accepted", "edited", "old-epoch", "removed", "manual" })
                        EventRecordStore.Append(connection, new(++sequence, id == "old-epoch" ? "different-epoch" : epoch,
                            id == "edited" ? "game.updated" : "game.created", "game:" + id,
                            JsonSerializer.Serialize(new { gameId = id, fromCandidate = id == "manual" ? null : "candidate" }, ContractJson.Options), accepted));
                });
            }
            var upgraded = await SqliteLibraryStore.TryOpenAsync(directory, Options(), CancellationToken.None);
            await using (var store = upgraded.Store!)
            {
                Assert.True(upgraded.IsOpened, upgraded.Detail);
                Assert.Equal(28, store.Info.SchemaVersion);
                Assert.Equal(epoch, store.Info.DataEpoch);
                Assert.Equal(accepted, store.TryGetGame("accepted")!.AcceptedUtc);
                Assert.Equal(added, store.TryGetGame("accepted")!.UpdatedUtc);
                foreach (var id in new[] { "edited", "old-epoch", "removed", "manual" }) Assert.Equal(added, store.TryGetGame(id)!.AcceptedUtc);
            }
            var reopened = await SqliteLibraryStore.TryOpenAsync(directory, Options(), CancellationToken.None);
            await using (var store = reopened.Store!) Assert.Equal(accepted, store.TryGetGame("accepted")!.AcceptedUtc);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "pre-migration-v27-to-v28-*.db"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
