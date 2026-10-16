using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

public sealed class GameExperienceMigrationTests
{
    [Fact]
    public async Task Upgrade29_DerivesPlayedOnlyFromSuccessfulHistory_PreservesFavoritesAndAdditionTime_AndReopenKeepsRating()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameLibrary-experience-migration-" + Guid.NewGuid().ToString("N"));
        var old = new SqliteLibraryStoreOptions { AppVersion = "old", ApiVersion = "1", Migrations = DatabaseMigrations.All.Where(m => m.Version <= 28).ToArray() };
        var current = new SqliteLibraryStoreOptions { AppVersion = "test", ApiVersion = "1" };
        var now = DateTime.UtcNow;
        try
        {
            var opened = await SqliteLibraryStore.InitializeAsync(root, old, CancellationToken.None);
            await using (var store = opened.Store!)
            {
                foreach (var id in new[] { "success", "crash", "favorite" })
                {
                    store.WriteExclusive((connection, _) =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = "INSERT INTO games (game_id,title,root_path,kind,membership,favorite,revision,accepted_utc,updated_utc) VALUES ($id,$id,$id,'manualFile','active',1,1,$now,$now)";
                        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", now.ToString("O")); command.ExecuteNonQuery();
                    });
                    if (id != "favorite") store.UpsertLaunchAttempt(new(id, id, id, "profile", null, "exited", "Never execute", [], root, 1, now.AddMinutes(-1), id == "success" ? 0 : 1, now, null, now.AddMinutes(-1)));
                }
            }
            var upgraded = await SqliteLibraryStore.TryOpenAsync(root, current, CancellationToken.None);
            await using (var store = upgraded.Store!)
            {
                Assert.True(upgraded.IsOpened, upgraded.Detail); Assert.Equal(29, store.Info.SchemaVersion);
                Assert.True(store.TryGetGame("success")!.Played);
                Assert.False(store.TryGetGame("crash")!.Played); Assert.False(store.TryGetGame("favorite")!.Played);
                Assert.All(store.ListGames(), game => { Assert.True(game.Favorite); Assert.Equal(now, game.AcceptedUtc); Assert.Equal(0, game.Rating); });
                Assert.NotNull(store.SetGamePreferences("success", null, null, 5, 1, now));
            }
            var reopened = await SqliteLibraryStore.TryOpenAsync(root, current, CancellationToken.None);
            await using (var store = reopened.Store!) { Assert.True(store.TryGetGame("success")!.Played); Assert.Equal(5, store.TryGetGame("success")!.Rating); }
            Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "pre-migration-v28-to-v29-*.db"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
