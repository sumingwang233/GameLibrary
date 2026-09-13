namespace GameLibrary.Domain.Paths;

/// <summary>
/// GamePath 工厂的校验结果；非法输入返回原因，不构造“近似合法”路径。
/// UNC/设备命名空间属于“本版本不支持”，其余属于“非法输入”，由上层分别映射
/// UnsupportedPath / InvalidPath。
/// </summary>
public sealed record PathValidationResult
{
    public GamePath? Path { get; }

    public PathRejectReason? Reason { get; }

    private PathValidationResult(GamePath? path, PathRejectReason? reason)
    {
        Path = path;
        Reason = reason;
    }

    public bool IsValid => Path is not null;

    /// <summary>true 表示路径语法合法但 v1 不支持该形态（UNC、设备命名空间）。</summary>
    public bool IsUnsupported =>
        Reason is PathRejectReason.UncPath or PathRejectReason.DeviceNamespace;

    public static PathValidationResult Valid(GamePath path) => new(path, null);

    public static PathValidationResult Reject(PathRejectReason reason) => new(null, reason);
}
