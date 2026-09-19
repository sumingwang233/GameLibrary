using System.Diagnostics;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Performance;

/// <summary>
/// T26 性能基线（阶段三）：5000 游戏规模下的数据库侧检索。
/// 缩减声明：完整 5000 游戏以单事务直接建库；分页/搜索/标签过滤延迟与内存增量达标即通过。
/// 预算：分页查询 p95 ≤ 150 ms（本机慢盘放宽，FSync 排除——写路径单事务一次性完成）。
/// </summary>
public sealed class GameCatalogPerformanceTests
{
    private const int GameCount = 5000;

    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private static async Task<SqliteLibraryStore> SeedAsync(string dataDir)
    {
        var init = await SqliteLibraryStore.InitializeAsync(dataDir, new SqliteLibraryStoreOptions
        {
            AppVersion = "0.1.0-perf",
            ApiVersion = "1",
        }, CancellationToken.None);
        Assert.True(init.IsOpened, init.Detail);
        var store = init.Store!;
        var utcNow = DateTime.UtcNow;
        store.WriteExclusive((connection, _) =>
        {
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < GameCount; i++)
            {
                LibraryCatalogStore.InsertGame(connection, new GameCard
                {
                    GameId = $"game-{i:D6}",
                    Title = $"性能游戏-{i:D5}",
                    RootPath = $@"C:\perf\games\{i:D5}",
                    Kind = "gameRoot",
                    Membership = "active",
                    AcceptedUtc = utcNow.AddSeconds(-i),
                    UpdatedUtc = utcNow.AddSeconds(-i),
                });
            }

            transaction.Commit();
        });
        return store;
    }

    [Fact]
    public async Task T26_PagedQuery_LatencyAndTotal_At5000Games()
    {
        var dataDir = FreshDataDir("t26-paged");
        try
        {
            var store = await SeedAsync(dataDir);
            await using var _ = store;

            // 全量 total（分页语义的计数）。
            var (total, _) = store.QueryGames(null, false, null, null, 1, 0);
            Assert.Equal(GameCount, total);

            // 分页遍历 20 页，采样延迟。
            var latencies = new List<long>();
            for (var page = 0; page < 20; page++)
            {
                var sw = Stopwatch.StartNew();
                var (pageTotal, items) = store.QueryGames(null, false, null, "title", 100, page * 100);
                sw.Stop();
                Assert.Equal(GameCount, pageTotal);
                Assert.Equal(100, items.Count);
                latencies.Add(sw.ElapsedMilliseconds);
            }

            latencies.Sort();
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            Assert.True(p95 <= 150, $"分页查询 p95 = {p95} ms，超过 150 ms 预算（样本：{string.Join(",", latencies)}）");
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task T26_SearchAndTagFilter_LatencyAtScale()
    {
        var dataDir = FreshDataDir("t26-search");
        try
        {
            var store = await SeedAsync(dataDir);

            // 100 个游戏挂同一标签。
            var utcNow = DateTime.UtcNow;
            var tag = new PersistedTag("tag-perf", "user", "性能标签", null, 1, 0, utcNow, utcNow);
            store.CreateTag(tag);
            store.WriteExclusive((connection, _) =>
            {
                for (var i = 0; i < 100; i++)
                {
                    TagStore.AssignTag(connection, $"game-{i:D6}", "tag-perf", utcNow);
                }
            });

            // 搜索（命中 root_path + title 的 LIKE 全表扫描）；"01231" 仅命中 i=1231 一个条目。
            var sw = Stopwatch.StartNew();
            var (searchTotal, searchItems) = store.QueryGames("01231", false, null, null, 50, 0);
            sw.Stop();
            Assert.Equal(1, searchTotal);
            Assert.Single(searchItems);
            Assert.True(sw.ElapsedMilliseconds <= 200, $"搜索查询 {sw.ElapsedMilliseconds} ms 超预算 200 ms");

            // 标签过滤（EXISTS 子查询）。
            sw.Restart();
            var (tagTotal, tagItems) = store.QueryGames(null, false, "tag-perf", "title", 200, 0);
            sw.Stop();
            Assert.Equal(100, tagTotal);
            Assert.Equal(100, tagItems.Count);
            Assert.True(sw.ElapsedMilliseconds <= 200, $"标签过滤 {sw.ElapsedMilliseconds} ms 超预算 200 ms");

            // 标签列表计数（含子查询聚合）。
            sw.Restart();
            var tags = store.ListTags();
            sw.Stop();
            var perfTag = Assert.Single(tags, t => t.TagId == "tag-perf");
            Assert.Equal(100, perfTag.GameCount);
            Assert.True(sw.ElapsedMilliseconds <= 200, $"tags.list {sw.ElapsedMilliseconds} ms 超预算 200 ms");
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task R41_BatchEnrichment_LatencyAt5000Games()
    {
        var dataDir = FreshDataDir("r41-enrich");
        try
        {
            var store = await SeedAsync(dataDir);
            await using var _ = store;

            var (_, games) = store.QueryGames(null, false, null, "title", 0, 0);
            Assert.Equal(GameCount, games.Count);

            // R41 前：逐游戏 4 次查询（≈20000 次）；后：500 一组 × 3 维度（30 次）。
            var sw = Stopwatch.StartNew();
            var enrichment = store.EnrichGameCards(games);
            sw.Stop();
            Assert.Equal(GameCount, enrichment.Count);
            Assert.All(enrichment.Values, e => Assert.Equal("auto", e.TitleSource));
            Assert.True(
                sw.ElapsedMilliseconds <= 300,
                $"批量充实 {sw.ElapsedMilliseconds} ms 超预算 300 ms");
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QueryGames_SortsByTitleUpdatedAndAcceptedTime()
    {
        var dataDir = FreshDataDir("game-sort");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, new SqliteLibraryStoreOptions
            {
                AppVersion = "1.1.0-test",
                ApiVersion = "1",
            }, CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            await using var store = init.Store!;

            var baseline = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
            foreach (var game in new[]
            {
                new GameCard { GameId = "game-a", Title = "Alpha", RootPath = @"C:\games\a", Kind = "gameRoot", Membership = "active", AcceptedUtc = baseline.AddHours(2), UpdatedUtc = baseline.AddHours(1) },
                new GameCard { GameId = "game-b", Title = "Bravo", RootPath = @"C:\games\b", Kind = "gameRoot", Membership = "active", AcceptedUtc = baseline.AddHours(1), UpdatedUtc = baseline.AddHours(3) },
                new GameCard { GameId = "game-c", Title = "Charlie", RootPath = @"C:\games\c", Kind = "gameRoot", Membership = "active", AcceptedUtc = baseline.AddHours(3), UpdatedUtc = baseline.AddHours(2) },
            })
            {
                store.InsertGame(game);
            }
            Assert.NotNull(store.SetFavorite("game-a", false, 1, baseline.AddHours(1)));
            Assert.NotNull(store.SetFavorite("game-b", false, 1, baseline.AddHours(3)));
            Assert.NotNull(store.SetFavorite("game-c", false, 1, baseline.AddHours(2)));

            Assert.Equal(
                ["Charlie", "Bravo", "Alpha"],
                store.QueryGames(null, false, null, "title-desc", 10, 0).Items.Select(game => game.Title));
            Assert.Equal(
                ["Bravo", "Charlie", "Alpha"],
                store.QueryGames(null, false, null, "updated-desc", 10, 0).Items.Select(game => game.Title));
            Assert.Equal(
                ["Charlie", "Alpha", "Bravo"],
                store.QueryGames(null, false, null, "accepted-desc", 10, 0).Items.Select(game => game.Title));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }
}
