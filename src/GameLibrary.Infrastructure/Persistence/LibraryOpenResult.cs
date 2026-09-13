namespace GameLibrary.Infrastructure.Persistence;

/// <summary>打开库时的判定结果；损坏绝不静默重建空库（ADR-0004）。</summary>
public enum LibraryOpenStatus
{
    /// <summary>连接成功，Info 有效。</summary>
    Opened,

    /// <summary>library.db 不存在，需要显式 library.init。</summary>
    NeedsInitialization,

    /// <summary>InitializeAsync 时库已存在（幂等保护，交给上层收据处理）。</summary>
    AlreadyInitialized,

    /// <summary>库版本高于程序支持的最大版本；拒绝写入，不自动降级。</summary>
    SchemaTooNew,

    /// <summary>结构损坏或无法解释；进入恢复模式，仅开放维护能力。</summary>
    RecoveryRequired,

    /// <summary>迁移失败；旧结构保留在事务前状态，可用迁移前快照恢复。</summary>
    MigrationFailed,

    /// <summary>文件系统 I/O 错误；不是数据库损坏，不误报 RecoveryRequired。</summary>
    IoError,
}

public sealed record LibraryOpenResult
{
    public LibraryOpenStatus Status { get; private init; }

    public SqliteLibraryStore? Store { get; private init; }

    public string? Detail { get; private init; }

    public bool IsOpened => Status == LibraryOpenStatus.Opened && Store is not null;

    public static LibraryOpenResult Opened(SqliteLibraryStore store) =>
        new() { Status = LibraryOpenStatus.Opened, Store = store };

    public static LibraryOpenResult Fail(LibraryOpenStatus status, string detail) =>
        new() { Status = status, Detail = detail };
}

/// <summary>库身份与版本信息；dataEpoch 恢复后更换，使旧 Revision/游标/计划失效。</summary>
public sealed record LibraryDatabaseInfo(
    int SchemaVersion,
    string AppVersion,
    string ApiVersion,
    string LibraryInstanceId,
    string DataEpoch,
    DateTime CreatedUtc);
