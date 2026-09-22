namespace GameLibrary.Infrastructure.Persistence;

// 标签域转发（第 10 片拆分，T-collections v18）：TagStore 全部转发，
// 经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    // T-collections 标签转发（v18）。

    public IReadOnlyList<PersistedTag> ListTags()
        => Execute((c, _) => TagStore.ListTags(c));

    public PersistedTag? TryGetTag(string tagId)
        => Execute((c, _) => TagStore.TryGetTag(c, tagId));

    public PersistedTag? TryGetTagByName(string kind, string name)
        => Execute((c, _) => TagStore.TryGetTagByName(c, kind, name));

    public void CreateTag(PersistedTag tag)
        => Execute((c, _) => TagStore.CreateTag(c, tag));

    public int? UpdateTag(
        string tagId,
        string? name,
        string? color,
        string? category,
        int? sortOrder,
        int? starred,
        string? displayName,
        bool clearDisplayName,
        int expectedRevision,
        DateTime utcNow)
        => Execute((c, _) => TagStore.UpdateTag(c, tagId, name, color, category, sortOrder, starred, displayName, clearDisplayName, expectedRevision, utcNow));

    public IReadOnlyList<string> RemoveTag(string tagId)
        => Execute((c, _) => TagStore.RemoveTag(c, tagId));

    public bool AssignTag(string gameId, string tagId, DateTime utcNow)
        => Execute((c, _) => TagStore.AssignTag(c, gameId, tagId, utcNow));

    public string? UnassignTag(string gameId, string tagId)
        => Execute((c, _) => TagStore.UnassignTag(c, gameId, tagId));

    public void SetTagOverride(string gameId, string tagKind, string tagName, string action, DateTime utcNow)
        => Execute((c, _) => TagStore.SetOverride(c, gameId, tagKind, tagName, action, utcNow));

    public bool ClearTagOverride(string gameId, string tagKind, string tagName, DateTime utcNow)
        => Execute((c, _) => TagStore.ClearOverride(c, gameId, tagKind, tagName, utcNow));

    public IReadOnlyList<(string Kind, string Name)> ListGameTags(string gameId)
        => Execute((c, _) => TagStore.ListGameTags(c, gameId));

    public bool EnsureEngineTagAssigned(string gameId, string engine, DateTime utcNow)
        => Execute((c, _) => TagStore.EnsureEngineTagAssigned(c, gameId, engine, utcNow));
}
