namespace GameLibrary.Domain.Paths;

/// <summary>路径输入被拒绝的原因。映射到 ErrorCodes.InvalidPath / UnsupportedPath。</summary>
public enum PathRejectReason
{
    /// <summary>空或 null 输入。</summary>
    NullOrEmpty,

    /// <summary>包含 NUL 字节。</summary>
    NullByte,

    /// <summary>UNC 路径（\\server\share…）；v1 只支持本地盘，返回 UnsupportedPath。</summary>
    UncPath,

    /// <summary>设备命名空间前缀（\\?\ 或 \\.\）。</summary>
    DeviceNamespace,

    /// <summary>盘符相对路径（如 D:foo），语义依赖每进程 CWD，拒绝。</summary>
    DriveRelativePath,

    /// <summary>不是本地盘绝对路径。</summary>
    NotRootedLocalDrive,

    /// <summary>含 Windows 文件名非法字符（" &lt; &gt; | ? * 或控制字符）。</summary>
    IllegalCharacters,

    /// <summary>盘符之后出现冒号（ADS 语法 / 非法流名）。</summary>
    InvalidColonUse,

    /// <summary>段尾含空格或点（Windows 不支持该别名，明确拒绝而非静默截断）。</summary>
    TrailingDotOrSpaceSegment,

    /// <summary>保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9，含带扩展名形式）。</summary>
    ReservedDeviceName,

    /// <summary>.. 解析越过盘根。</summary>
    EscapesRoot,
}
