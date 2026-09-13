namespace GameLibrary.Contracts;

/// <summary>结构化错误对象。可选字段缺省输出 null，不把修复信息塞进 message。</summary>
public sealed class RequestError
{
    public string Code { get; init; } = "";

    public string Message { get; init; } = "";

    public bool? Retryable { get; init; }

    public IReadOnlyDictionary<string, string>? FieldErrors { get; init; }

    public long? CurrentRevision { get; init; }

    /// <summary>建议的可执行修复操作 operationId（例如 backups.create）。</summary>
    public string? RecoveryOperation { get; init; }
}
