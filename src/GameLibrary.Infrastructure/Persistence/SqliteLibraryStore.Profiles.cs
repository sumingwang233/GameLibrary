namespace GameLibrary.Infrastructure.Persistence;

// 资料与封面域转发（第 10 片拆分，T14）：GameProfileStore 全部转发
// （字段改写/有效值计算/资产导入与选择），经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    // T14 资料与封面转发。

    public int? SetGameField(string gameId, string fieldKey, string? value, string source, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => GameProfileStore.SetGameField(c, gameId, fieldKey, value, source, expectedRevision, utcNow));

    public int? ResetGameField(string gameId, string fieldKey, string autoValue, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => GameProfileStore.ResetGameField(c, gameId, fieldKey, autoValue, expectedRevision, utcNow));

    public (string? Value, string Source) EffectiveField(string gameId, string fieldKey, string fallback)
        => Execute((c, _) => GameProfileStore.EffectiveField(c, gameId, fieldKey, fallback));

    public GameAsset ImportAsset(string gameId, string importedFilePath, DateTime utcNow)
        => Execute((c, _) => GameProfileStore.ImportAsset(c, gameId, importedFilePath, utcNow));

    public IReadOnlyList<GameAsset> ListAssets(string gameId)
        => Execute((c, _) => GameProfileStore.ListAssets(c, gameId));

    public GameAsset? TryGetAsset(string assetId)
        => Execute((c, _) => GameProfileStore.TryGetAsset(c, assetId));

    public void ChooseAsset(string gameId, string assetId)
        => Execute((c, _) => GameProfileStore.ChooseAsset(c, gameId, assetId));

    public string? ResetCover(string gameId)
        => Execute((c, _) => GameProfileStore.ResetCover(c, gameId));

    public string? RemoveAsset(string assetId)
        => Execute((c, _) => GameProfileStore.RemoveAsset(c, assetId));

    public void WriteAutoField(string gameId, string fieldKey, string value, DateTime utcNow)
        => Execute((c, _) => GameProfileStore.WriteAutoField(c, gameId, fieldKey, value, utcNow));
}
