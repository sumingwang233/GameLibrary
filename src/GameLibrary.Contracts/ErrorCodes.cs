namespace GameLibrary.Contracts;

/// <summary>
/// 公开错误码。code 是稳定契约，中文 message 文案可以变化；
/// 可执行的修复入口放 <c>RecoveryOperation</c>，不埋进 message。
/// </summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "InvalidArgument";
    public const string InvalidPath = "InvalidPath";
    public const string UnsupportedPath = "UnsupportedPath";
    public const string NotFound = "NotFound";
    public const string UnsupportedOperation = "UnsupportedOperation";
    public const string InternalError = "InternalError";
    public const string RevisionConflict = "RevisionConflict";
    public const string IdempotencyConflict = "IdempotencyConflict";
    public const string PermissionDenied = "PermissionDenied";
    public const string NeedsAuthorization = "NeedsAuthorization";
    public const string HostUnavailable = "HostUnavailable";
    public const string HostVersionMismatch = "HostVersionMismatch";
    public const string DataDirectoryMismatch = "DataDirectoryMismatch";
    public const string CursorExpired = "CursorExpired";
    public const string PlanExpired = "PlanExpired";
    public const string PlanStale = "PlanStale";
    public const string UnknownOutcome = "UnknownOutcome";
    public const string ResourceTooLarge = "ResourceTooLarge";
    public const string MaintenanceMode = "MaintenanceMode";

    public const string RootOffline = "RootOffline";
    public const string RootIdentityChanged = "RootIdentityChanged";
    public const string EntryMissing = "EntryMissing";
    public const string AmbiguousEntry = "AmbiguousEntry";
    public const string ToolMissing = "ToolMissing";
    public const string ToolChanged = "ToolChanged";
    public const string ToolNeedsSetup = "ToolNeedsSetup";
    public const string TranslationRouteUnavailable = "TranslationRouteUnavailable";
    public const string AccessDenied = "AccessDenied";
    public const string ProcessStartFailed = "ProcessStartFailed";
    public const string LaunchObservationTimeout = "LaunchObservationTimeout";
    public const string ScanPartial = "ScanPartial";
    public const string DatabaseBusy = "DatabaseBusy";
    public const string SteamManifestMissing = "SteamManifestMissing";
    public const string MigrationFailed = "MigrationFailed";

    /// <summary>契约要求必须存在的全部错误码，供覆盖测试使用。</summary>
    public static readonly IReadOnlyList<string> RequiredCodes =
    [
        InvalidArgument,
        InvalidPath,
        UnsupportedPath,
        NotFound,
        UnsupportedOperation,
        InternalError,
        RevisionConflict,
        IdempotencyConflict,
        PermissionDenied,
        NeedsAuthorization,
        HostUnavailable,
        HostVersionMismatch,
        DataDirectoryMismatch,
        CursorExpired,
        PlanExpired,
        PlanStale,
        UnknownOutcome,
        ResourceTooLarge,
        MaintenanceMode,
        RootOffline,
        RootIdentityChanged,
        EntryMissing,
        AmbiguousEntry,
        ToolMissing,
        ToolChanged,
        ToolNeedsSetup,
        TranslationRouteUnavailable,
        AccessDenied,
        ProcessStartFailed,
        LaunchObservationTimeout,
        ScanPartial,
        DatabaseBusy,
        SteamManifestMissing,
        MigrationFailed,
    ];
}
