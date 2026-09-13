namespace GameLibrary.Contracts;

/// <summary>
/// 所有操作（CLI、MCP、IPC）的统一结果信封。字段名与空值语义是公开契约：
/// 可选字段缺省为 null，不省略键。
/// </summary>
public sealed class Envelope<T>
{
    public string ApiVersion { get; init; } = ApiConstants.ApiVersion;

    public string RequestId { get; init; } = "";

    /// <summary>握手返回的库实例 ID；未建库的引导操作为 null。</summary>
    public string? LibraryInstanceId { get; init; }

    /// <summary>数据纪元；备份恢复后更换，旧 epoch 的变更请求必须拒绝。</summary>
    public string? DataEpoch { get; init; }

    public bool Ok { get; init; }

    public OperationStatus Status { get; init; }

    public T? Data { get; init; }

    /// <summary>受理的长任务 ID；同步完成时为 null。</summary>
    public string? JobId { get; init; }

    public RequestError? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<NextAction> NextActions { get; init; } = [];
}
