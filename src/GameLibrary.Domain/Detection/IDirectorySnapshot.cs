namespace GameLibrary.Domain.Detection;

/// <summary>
/// 只读目录快照端口（策划案 8.1）：检测器只经由本端口观察文件系统，不直接 IO。
/// 相对路径以 '/' 分隔、无前导分隔；"" 表示快照根。
/// </summary>
public interface IDirectorySnapshot
{
    string RootPhysicalPath { get; }

    bool DirectoryExists(string relativePath);

    bool FileExists(string relativePath);

    long? FileSize(string relativePath);

    /// <summary>直属子目录名（不含路径）。</summary>
    IReadOnlyList<string> ListDirectories(string relativePath);

    /// <summary>直属文件名（不含路径）。</summary>
    IReadOnlyList<string> ListFiles(string relativePath);

    /// <summary>读取文件前 count 字节；区分不存在/成功/不可读三态。</summary>
    FileProbe TryReadFirstBytes(string relativePath, int count);
}

/// <summary>文件探测三态：NotFound（不存在）/ Ok（读到字节）/ Unreadable（存在但读取失败）。</summary>
public readonly record struct FileProbe
{
    public FileProbeKind Kind { get; private init; }

    public byte[]? Bytes { get; private init; }

    public static FileProbe NotFound() => new() { Kind = FileProbeKind.NotFound };

    public static FileProbe Ok(byte[] bytes) => new() { Kind = FileProbeKind.Ok, Bytes = bytes };

    public static FileProbe Unreadable() => new() { Kind = FileProbeKind.Unreadable };
}

public enum FileProbeKind
{
    NotFound,

    Ok,

    Unreadable,
}

/// <summary>引擎检测器端口：纯函数，对同一快照输出确定（补充规格 2.3）。</summary>
public interface IEngineDetector
{
    EngineId Engine { get; }

    /// <summary>检测器规则版本；规则演进递增使旧验证/建议失效。</summary>
    int DetectorVersion { get; }

    /// <summary>无任何相关证据时返回 null。</summary>
    DetectionResult? Detect(IDirectorySnapshot snapshot);
}
