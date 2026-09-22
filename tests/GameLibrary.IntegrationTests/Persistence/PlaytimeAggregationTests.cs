using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// feat-1 游玩统计聚合口径（LibraryCatalogStore.QueryPlaytimeStats）：
/// 只统计同时有 process_started_utc 与 finished_utc 的 attempt（无 finished 不计入、
/// 无 started 不计入）；同一 attempt（UPSERT 同行）只计一次；分钟数整除向下取整；
/// lastPlayedUtc 取有起止时间 attempt 的最大 finished_utc；从未玩过为 null/缺省 0。
/// </summary>
public sealed class PlaytimeAggregationTests
{
    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<SqliteLibraryStore> OpenStoreAsync(string dataDir)
    {
        var init = await SqliteLibraryStore.InitializeAsync(dataDir, new SqliteLibraryStoreOptions
        {
            AppVersion = "1.5.0-test",
            ApiVersion = "1",
        }, CancellationToken.None);
        Assert.True(init.IsOpened, init.Detail);
        return init.Store!;
    }

    private static GameCard Game(string id) => new()
    {
        GameId = id,
        Title = id,
        RootPath = $@"C:\playtime\{id}",
        Kind = "manualDirectory",
        Membership = "active",
        AcceptedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static PersistedLaunchAttempt Attempt(
        string attemptId, string gameId, DateTime? started, DateTime? finished, string state = "exited") => new(
        attemptId, $"key-{attemptId}", gameId, "profile-x", null, state,
        @"C:\game\game.exe", [], @"C:\game", 4242, started, finished is null ? null : 0, finished, null,
        started ?? finished ?? DateTime.UtcNow);

    [Fact]
    public async Task QueryPlaytimeStats_SumsOnlyAttemptsWithBothTimestamps()
    {
        var dataDir = FreshDataDir("playtime-agg");
        try
        {
            var store = await OpenStoreAsync(dataDir);
            await using var _ = store;
            store.InsertGame(Game("game-p1"));

            // attempt-1：10 分 30 秒（630s）。
            store.UpsertLaunchAttempt(Attempt(
                "attempt-p1", "game-p1",
                new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 1, 10, 10, 30, DateTimeKind.Utc)));
            // attempt-2：仍在进行（无 finished）——不计入。
            store.UpsertLaunchAttempt(Attempt(
                "attempt-p2", "game-p1",
                new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc),
                finished: null, state: "processCreated"));
            // attempt-3：启动失败（无 started）——不计入。
            store.UpsertLaunchAttempt(Attempt(
                "attempt-p3", "game-p1",
                started: null,
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), state: "processStartFailed"));
            // attempt-4：45 秒。
            store.UpsertLaunchAttempt(Attempt(
                "attempt-p4", "game-p1",
                new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 1, 13, 0, 45, DateTimeKind.Utc)));

            var stats = store.QueryPlaytimeStats(["game-p1"]);
            var p1 = stats["game-p1"];
            // 630s + 45s = 675s → 11 分钟（整分钟向下取整，一次聚合不拆零头）。
            Assert.Equal(11, p1.PlaytimeMinutes);
            // lastPlayedUtc 是有起止时间 attempt 的最大 finished（13:00:45）。
            Assert.Equal(new DateTime(2026, 9, 1, 13, 0, 45, DateTimeKind.Utc), p1.LastPlayedUtc);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QueryPlaytimeStats_SameAttemptUpsertedTwice_CountedOnce()
    {
        var dataDir = FreshDataDir("playtime-once");
        try
        {
            var store = await OpenStoreAsync(dataDir);
            await using var _ = store;
            store.InsertGame(Game("game-p2"));

            // 真实时序：processCreated 先落库，退出后 UPSERT 同一行补 finished——一行只能计一次。
            var started = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
            var finished = new DateTime(2026, 9, 2, 8, 30, 0, DateTimeKind.Utc);
            store.UpsertLaunchAttempt(Attempt("attempt-p5", "game-p2", started, null, "processCreated"));
            store.UpsertLaunchAttempt(Attempt("attempt-p5", "game-p2", started, finished));

            var stats = store.QueryPlaytimeStats(["game-p2"]);
            Assert.Equal(30, stats["game-p2"].PlaytimeMinutes);
            Assert.Equal(finished, stats["game-p2"].LastPlayedUtc);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QueryPlaytimeStats_NoAttempts_ZeroMinutesNullLastPlayed()
    {
        var dataDir = FreshDataDir("playtime-empty");
        try
        {
            var store = await OpenStoreAsync(dataDir);
            await using var _ = store;
            store.InsertGame(Game("game-p3"));
            store.InsertGame(Game("game-p4"));

            // 无 attempt 与只有不完整 attempt 都回退同一形状（0/null），批量查询缺键不炸。
            store.UpsertLaunchAttempt(Attempt(
                "attempt-p6", "game-p4",
                new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc), finished: null, state: "processCreated"));

            var stats = store.QueryPlaytimeStats(["game-p3", "game-p4"]);
            Assert.False(stats.ContainsKey("game-p3"));
            Assert.False(stats.ContainsKey("game-p4"));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    private static void Cleanup(string dataDir)
    {
        try
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
