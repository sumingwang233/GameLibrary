namespace GameLibrary.Infrastructure.Persistence;

// 运行态与验证域转发（第 10 片拆分）：库根/Profile/启动历史/作业记录（v13+ 运行态）
// + 工具验证记录（T08），经主文件的私有 Execute 锁助手串行访问当前库连接。

public sealed partial class SqliteLibraryStore
{
    // 运行态持久化转发（v13+：库根/Profile/启动历史/作业记录）。

    public IReadOnlyList<PersistedRoot> ReadRoots()
        => Execute((c, _) => RuntimeStateStore.ReadRoots(c));

    public void UpsertRoot(PersistedRoot root, DateTime utcNow)
        => Execute((c, _) => RuntimeStateStore.UpsertRoot(c, root, utcNow));

    public void DeleteRoot(string rootId)
        => Execute((c, _) => RuntimeStateStore.DeleteRoot(c, rootId));

    public int RemoveRootGames(string rootId, string rootPath, DateTime utcNow)
        => Execute((c, _) => RuntimeStateStore.RemoveRootGames(c, rootId, rootPath, utcNow));

    public IReadOnlyList<PersistedProfile> ReadProfiles()
        => Execute((c, _) => RuntimeStateStore.ReadProfiles(c));

    public void UpsertProfile(PersistedProfile profile, DateTime utcNow)
        => Execute((c, _) => RuntimeStateStore.UpsertProfile(c, profile, utcNow));

    public void DeleteProfile(string profileId)
        => Execute((c, _) => RuntimeStateStore.DeleteProfile(c, profileId));

    public IReadOnlyList<PersistedLaunchAttempt> ReadLaunchAttempts()
        => Execute((c, _) => RuntimeStateStore.ReadAttempts(c));

    public void UpsertLaunchAttempt(PersistedLaunchAttempt attempt)
        => Execute((c, _) => RuntimeStateStore.UpsertAttempt(c, attempt));

    public IReadOnlyList<PersistedJobRecord> ReadJobRecords()
        => Execute((c, _) => RuntimeStateStore.ReadJobs(c));

    public void UpsertJobRecord(PersistedJobRecord job)
        => Execute((c, _) => RuntimeStateStore.UpsertJob(c, job));

    public int MarkInterruptedJobs(DateTime utcNow)
        => Execute((c, _) => RuntimeStateStore.MarkInterruptedJobs(c, utcNow));

    // T08 验证记录转发。

    public void InsertVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record)
        => Execute((c, _) => VerificationStore.Insert(c, record));

    public GameLibrary.Domain.Tools.ToolVerificationRecord? TryGetVerification(string recordId)
        => Execute((c, _) => VerificationStore.TryGet(c, recordId));

    public IReadOnlyList<GameLibrary.Domain.Tools.ToolVerificationRecord> ListVerifications(string? toolId)
        => Execute((c, _) => VerificationStore.List(c, toolId));

    public void UpdateVerification(GameLibrary.Domain.Tools.ToolVerificationRecord record)
        => Execute((c, _) => VerificationStore.Update(c, record));
}
