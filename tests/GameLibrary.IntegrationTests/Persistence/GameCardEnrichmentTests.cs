using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// R41 games.list 批量充实与单游戏路径的等价性：字段 user 优先、封面取
/// (imported_utc, asset_id) 序首条 current、标签 (kind, name NOCASE) 排序、
/// 无数据时回退（title=卡片值/summary=空，source=auto）。
/// </summary>
public sealed class GameCardEnrichmentTests
{
    private static string FreshDataDir()
    {
        var path = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs", $"enrich-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<SqliteLibraryStore> OpenStoreAsync(string dataDir)
    {
        var init = await SqliteLibraryStore.InitializeAsync(dataDir, new SqliteLibraryStoreOptions
        {
            AppVersion = "1.1.8-test",
            ApiVersion = "1",
        }, CancellationToken.None);
        Assert.True(init.IsOpened, init.Detail);
        return init.Store!;
    }

    private static GameCard Game(string id, string title) => new()
    {
        GameId = id,
        Title = title,
        RootPath = $@"C:\enrich\{id}",
        Kind = "gameRoot",
        Membership = "active",
        AcceptedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task EnrichGameCards_MatchesPerGameQueries()
    {
        var dataDir = FreshDataDir();
        try
        {
            var store = await OpenStoreAsync(dataDir);
            await using var _ = store;
            var utcNow = DateTime.UtcNow;

            store.InsertGame(Game("game-a", "自动标题A"));
            store.InsertGame(Game("game-b", "标题B"));
            store.InsertGame(Game("game-c", "标题C"));

            // game-a：auto 与 user 字段共存（user 必须胜出）；两个标签。
            store.WriteAutoField("game-a", "title", "自动标题A", utcNow);
            Assert.NotNull(store.SetGameField("game-a", "title", "自定义标题A", "user", 1, utcNow));
            store.WriteAutoField("game-a", "summary", "自动简介", utcNow);
            var tagUser = new PersistedTag("tag-u", "user", "我的收藏", null, 1, 0, utcNow, utcNow);
            var tagEngine = new PersistedTag("tag-e", "engine", "kirikiri", null, 1, 0, utcNow, utcNow);
            store.CreateTag(tagUser);
            store.CreateTag(tagEngine);
            store.AssignTag("game-a", "tag-u", utcNow);
            store.AssignTag("game-a", "tag-e", utcNow);

            // game-a：两份封面，选择较早导入的为 current（批量应取它而非最新导入的）。
            var coverOld = Path.Combine(dataDir, "cover-old.png");
            var coverNew = Path.Combine(dataDir, "cover-new.png");
            await File.WriteAllTextAsync(coverOld, "old");
            await File.WriteAllTextAsync(coverNew, "new");
            var oldAsset = store.ImportAsset("game-a", coverOld, utcNow.AddMinutes(-5));
            var newAsset = store.ImportAsset("game-a", coverNew, utcNow);
            store.ChooseAsset("game-a", oldAsset.AssetId);

            // game-b：仅一个用户标签。
            store.AssignTag("game-b", "tag-u", utcNow);

            var cards = new[] { Game("game-a", "自动标题A"), Game("game-b", "标题B"), Game("game-c", "标题C") };
            var enrichment = store.EnrichGameCards(cards);

            foreach (var card in cards)
            {
                var batch = enrichment[card.GameId];
                var (title, titleSource) = store.EffectiveField(card.GameId, "title", card.Title);
                var (summary, summarySource) = store.EffectiveField(card.GameId, "summary", "");
                var cover = store.ListAssets(card.GameId).FirstOrDefault(a => a.IsCurrent)?.AssetId;
                var tags = store.ListGameTags(card.GameId);

                Assert.Equal(title, batch.Title);
                Assert.Equal(titleSource, batch.TitleSource);
                Assert.Equal(summary, batch.Summary);
                Assert.Equal(summarySource, batch.SummarySource);
                Assert.Equal(cover, batch.CoverAssetId);
                Assert.Equal(tags, batch.Tags);
            }

            // 定向断言防「双双同错」：user 覆盖生效、封面取选中的旧资产、game-c 全回退。
            Assert.Equal("自定义标题A", enrichment["game-a"].Title);
            Assert.Equal("user", enrichment["game-a"].TitleSource);
            Assert.Equal("自动简介", enrichment["game-a"].Summary);
            Assert.Equal(oldAsset.AssetId, enrichment["game-a"].CoverAssetId);
            Assert.NotEqual(newAsset.AssetId, enrichment["game-a"].CoverAssetId);
            Assert.Equal(2, enrichment["game-a"].Tags.Count);
            Assert.Equal(("engine", "kirikiri"), enrichment["game-a"].Tags[0]);
            Assert.Equal("标题C", enrichment["game-c"].Title);
            Assert.Equal("auto", enrichment["game-c"].TitleSource);
            Assert.Equal("", enrichment["game-c"].Summary);
            Assert.Null(enrichment["game-c"].CoverAssetId);
            Assert.Empty(enrichment["game-c"].Tags);
        }
        finally
        {
            TryCleanup(dataDir);
        }
    }

    [Fact]
    public async Task EnrichGameCards_EmptyInput_ReturnsEmpty()
    {
        var dataDir = FreshDataDir();
        try
        {
            var store = await OpenStoreAsync(dataDir);
            await using var _ = store;
            Assert.Empty(store.EnrichGameCards([]));
        }
        finally
        {
            TryCleanup(dataDir);
        }
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
