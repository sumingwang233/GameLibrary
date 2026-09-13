using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 宿主对数据目录中库的观察状态：Handshake 与 host.status 的单一事实来源。
/// Host 是唯一库连接持有者；未初始化/损坏/版本过新时仅状态可见，不阻塞诊断能力。
/// </summary>
public sealed class HostLibraryState
{
    public required LibraryOpenStatus Status { get; init; }

    public string? Detail { get; init; }

    /// <summary>打开成功时非空；由 HostRuntime 持有并在关停时释放。</summary>
    public SqliteLibraryStore? Store { get; init; }

    public bool Initialized => Status == LibraryOpenStatus.Opened && Store is not null;

    public string? LibraryInstanceId => Store?.Info.LibraryInstanceId;

    public string? DataEpoch => Store?.Info.DataEpoch;

    public int? SchemaVersion => Store?.Info.SchemaVersion;

    public static HostLibraryState NotInitialized(LibraryOpenStatus status, string? detail) =>
        new() { Status = status, Detail = detail };
}
