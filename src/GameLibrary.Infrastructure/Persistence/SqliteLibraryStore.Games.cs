namespace GameLibrary.Infrastructure.Persistence;

// 游戏卡域转发（第 10 片拆分）：游戏卡 CRUD/检索/批量充实（T11）+ 收藏/翻译策略（T13）
// + 可用性核对与重关联（T17）+ 忽略规则，全部经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    public void InsertGame(GameCard game)
        => Execute((c, _) => LibraryCatalogStore.InsertGame(c, game));

    public int? RemoveGame(string gameId, int expectedRevision, IgnoreRule ignore, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.RemoveGame(c, gameId, expectedRevision, ignore, utcNow));

    public GameCard? TryGetGame(string gameId)
        => Execute((c, _) => LibraryCatalogStore.TryGetGame(c, gameId));

    public IReadOnlyList<GameCard> ListGames()
        => Execute((c, _) => LibraryCatalogStore.ListGames(c));

    /// <summary>数据库侧检索（阶段三）：搜索/收藏/标签过滤 + 排序 + 分页；limit &lt;= 0 全量。</summary>
    public (int Total, IReadOnlyList<GameCard> Items) QueryGames(
        string? search, bool favoriteOnly, string? tagId, string? sort, int limit, int offset)
        => Execute((c, _) => LibraryCatalogStore.QueryGames(c, search, favoriteOnly, tagId, sort, limit, offset));

    /// <summary>列表页批量充实（R41）：单锁一次取回字段/封面/标签，取代逐游戏 4 次查询。</summary>
    public IReadOnlyDictionary<string, GameCardEnrichment> EnrichGameCards(
        IReadOnlyList<GameCard> games)
        => Execute((c, _) => LibraryCatalogStore.EnrichGameCards(c, games));

    public GameCard? TryGetGameByRootPath(string rootPath)
        => Execute((c, _) => LibraryCatalogStore.TryGetGameByRootPath(c, rootPath));

    public void InsertIgnoreRule(IgnoreRule rule)
        => Execute((c, _) => LibraryCatalogStore.InsertIgnoreRule(c, rule));

    // T13 收藏与翻译策略转发。

    public int? SetFavorite(string gameId, bool favorite, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.SetFavorite(c, gameId, favorite, expectedRevision, utcNow));

    public int? SetTranslationOverride(string gameId, string? overrideValue, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.SetTranslationOverride(c, gameId, overrideValue, expectedRevision, utcNow));

    // T17 可用性核对与重关联转发。

    public bool UpdateAvailability(string gameId, string availability, DateTime? missingSinceUtc, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.UpdateAvailability(c, gameId, availability, missingSinceUtc, utcNow));

    public int? RelinkGame(string gameId, string newRootPath, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => LibraryCatalogStore.RelinkGame(c, gameId, newRootPath, expectedRevision, utcNow));

    // 匹配指纹域转发（v20 game_fingerprints）：写入仅 accept（R43 事务内）/relink/create 三处。

    public void UpsertGameFingerprint(string gameId, GameFingerprintData fingerprint)
        => Execute((c, _) => LibraryCatalogStore.UpsertGameFingerprint(c, gameId, fingerprint));

    public GameFingerprintRow? TryGetGameFingerprint(string gameId)
        => Execute((c, _) => LibraryCatalogStore.TryGetGameFingerprint(c, gameId));

    /// <summary>相似建议对比集合（active + 当前策略版本）；excludeGameId 防自身恒占 similarTo[0]。</summary>
    public IReadOnlyList<GameFingerprintRow> ListActiveGameFingerprints(string? excludeGameId, int strategyVersion)
        => Execute((c, _) => LibraryCatalogStore.ListActiveGameFingerprints(c, excludeGameId, strategyVersion));

    public IReadOnlyList<IgnoreRule> ListIgnoreRules()
        => Execute((c, _) => LibraryCatalogStore.ListIgnoreRules(c));

    public IReadOnlyList<string> RemoveIgnoreRule(string ignoreId)
        => Execute((c, _) => LibraryCatalogStore.RemoveIgnoreRule(c, ignoreId));

    public bool IsSuppressedByIgnoreRule(string physicalPath, string? boundGameId)
        => Execute((c, _) => LibraryCatalogStore.IsSuppressedByIgnoreRule(c, physicalPath, boundGameId));
}
