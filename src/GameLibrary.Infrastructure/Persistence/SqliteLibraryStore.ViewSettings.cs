namespace GameLibrary.Infrastructure.Persistence;

// 设置与视图域转发（第 10 片拆分）：settings 键值存储 + T15-C 自定义视图，
// 经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    // settings 转发。

    public AppSettingsSnapshot ReadSettings()
        => Execute((c, _) => SettingsStore.Read(c));

    public int WriteSettingsKeys(IEnumerable<(string Key, string? Value)> keys, DateTime utcNow)
        => Execute((c, _) => SettingsStore.WriteKeys(c, keys, utcNow));

    public int ResetSettings(DateTime utcNow)
        => Execute((c, _) => SettingsStore.ResetAll(c, utcNow));

    // T15-C 自定义视图转发。

    public void InsertView(LibraryView view)
        => Execute((c, _) => LibraryViewStore.InsertView(c, view));

    public LibraryView? TryGetView(string viewId)
        => Execute((c, _) => LibraryViewStore.TryGetView(c, viewId));

    public IReadOnlyList<LibraryView> ListViews()
        => Execute((c, _) => LibraryViewStore.ListViews(c));

    public int? UpdateView(
        string viewId, string? name, string? search, bool? favoriteOnly, string? sort, int expectedRevision, DateTime utcNow)
        => Execute((c, _) => LibraryViewStore.UpdateView(c, viewId, name, search, favoriteOnly, sort, expectedRevision, utcNow));

    public bool DeleteView(string viewId)
        => Execute((c, _) => LibraryViewStore.DeleteView(c, viewId));
}
