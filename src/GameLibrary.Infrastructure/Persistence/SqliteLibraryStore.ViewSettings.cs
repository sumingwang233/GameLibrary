namespace GameLibrary.Infrastructure.Persistence;

// 设置与视图域转发（第 10 片拆分）：settings 键值存储 + T15-C 自定义视图，
// 经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    // settings 转发。

    public AppSettingsSnapshot ReadSettings()
        => ReadExclusive((c, _) => SettingsStore.Read(c));

    public int WriteSettingsKeys(IEnumerable<(string Key, string? Value)> keys, DateTime utcNow)
        => Execute((c, _) => SettingsStore.WriteKeys(c, keys, utcNow, _writeTransaction));

    public int ResetSettings(DateTime utcNow)
        => Execute((c, _) => SettingsStore.ResetAll(c, utcNow, _writeTransaction));

    // T15-C 自定义视图转发。

    public void InsertView(LibraryView view)
        => Execute((c, _) => LibraryViewStore.InsertView(c, view));

    public LibraryView? TryGetView(string viewId)
        => ReadExclusive((c, _) => LibraryViewStore.TryGetView(c, viewId));

    public IReadOnlyList<LibraryView> ListViews()
        => ReadExclusive((c, _) => LibraryViewStore.ListViews(c));

    public int? UpdateView(
        string viewId, string? name, string? search, bool? favoriteOnly, string? tagId, string? sort,
        int expectedRevision, DateTime utcNow)
        => Execute((c, _) => LibraryViewStore.UpdateView(
            c, viewId, name, search, favoriteOnly, tagId, sort, expectedRevision, utcNow, _writeTransaction));

    public bool DeleteView(string viewId)
        => Execute((c, _) => LibraryViewStore.DeleteView(c, viewId));
}
