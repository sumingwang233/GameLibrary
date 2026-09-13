namespace GameLibrary.Domain.Paths;

/// <summary>
/// 一个本地 Windows 绝对路径的值对象（ADR-0001）。
/// PhysicalPath 原样保留内容字符（中文/日文/方括号/百分号/空格），仅统一分隔符为 '\'；
/// ComparisonKey 是唯一用于相等/去重/唯一约束的规范形态。
/// 本类型不做任何文件系统访问；长路径不在本层加 260 限制。
/// </summary>
public sealed class GamePath : IEquatable<GamePath>
{
    private const char PrimarySeparator = '\\';
    private const char AlternateSeparator = '/';
    private static readonly char[] IllegalChars = ['"', '<', '>', '|', '?', '*'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private GamePath(string physicalPath, string comparisonKey, string[] segments)
    {
        PhysicalPath = physicalPath;
        ComparisonKey = comparisonKey;
        Segments = segments;
    }

    /// <summary>规范化后的物理路径：内容字符原样，分隔符统一为 '\'，无尾分隔符（盘根除外）。</summary>
    public string PhysicalPath { get; }

    /// <summary>规范比较键：解析 . 与 ..、InvariantCulture 大写、单一分隔符。仅用于比较，不用于展示。</summary>
    public string ComparisonKey { get; }

    /// <summary>相对盘根的段列表（不含盘符），盘根为空数组。</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>大写盘符 + '\'，如 "F:\"。</summary>
    public string DriveRoot => PhysicalPath[..3];

    public bool IsDriveRoot => Segments.Count == 0;

    /// <summary>ComparisonKey 生成策略版本；规则演进时递增并重建存量键（ADR-0001 迁移影响）。</summary>
    public const int ComparisonKeyStrategyVersion = 1;

    public static PathValidationResult TryCreate(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return PathValidationResult.Reject(PathRejectReason.NullOrEmpty);
        }

        if (input.Contains('\0'))
        {
            return PathValidationResult.Reject(PathRejectReason.NullByte);
        }

        if (input.StartsWith(@"\\?\") || input.StartsWith(@"\\.\"))
        {
            return PathValidationResult.Reject(PathRejectReason.DeviceNamespace);
        }

        if (input.StartsWith(@"\\"))
        {
            return PathValidationResult.Reject(PathRejectReason.UncPath);
        }

        if (input.Length < 2 || !IsAsciiLetter(input[0]) || input[1] != ':')
        {
            return PathValidationResult.Reject(PathRejectReason.NotRootedLocalDrive);
        }

        if (input.Length == 2 || input[2] != PrimarySeparator && input[2] != AlternateSeparator)
        {
            return PathValidationResult.Reject(PathRejectReason.DriveRelativePath);
        }

        foreach (var ch in input)
        {
            if (IllegalChars.Contains(ch) || char.IsControl(ch))
            {
                return PathValidationResult.Reject(PathRejectReason.IllegalCharacters);
            }
        }

        var rawSegments = input[3..]
            .Replace(AlternateSeparator, PrimarySeparator)
            .Split(PrimarySeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in rawSegments)
        {
            if (segment is "." or "..")
            {
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return PathValidationResult.Reject(PathRejectReason.TrailingDotOrSpaceSegment);
            }

            if (segment.Contains(':'))
            {
                return PathValidationResult.Reject(PathRejectReason.InvalidColonUse);
            }

            if (ReservedNames.Contains(SegmentBaseName(segment)))
            {
                return PathValidationResult.Reject(PathRejectReason.ReservedDeviceName);
            }
        }

        var resolved = new List<string>(rawSegments.Length);
        foreach (var segment in rawSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    return PathValidationResult.Reject(PathRejectReason.EscapesRoot);
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
        }

        var segments = resolved.ToArray();
        var physical = segments.Length == 0
            ? char.ToUpperInvariant(input[0]) + @":\"
            : char.ToUpperInvariant(input[0]) + @":\" + string.Join(PrimarySeparator, segments);
        var key = BuildComparisonKey(char.ToUpperInvariant(input[0]), segments);

        return PathValidationResult.Valid(new GamePath(physical, key, segments));
    }

    /// <summary>已通过 <see cref="TryCreate"/> 校验的输入直接构造；否则抛 <see cref="ArgumentException"/>。</summary>
    public static GamePath Create(string input)
    {
        var result = TryCreate(input);
        if (!result.IsValid)
        {
            throw new ArgumentException($"非法或不受支持的路径输入：{input}（{result.Reason}）", nameof(input));
        }

        return result.Path!;
    }

    /// <summary>当前路径是否是 <paramref name="ancestor"/> 的子路径或相等；按分段边界比较（F:\Games 不包含 F:\Games2）。</summary>
    public bool IsUnderOrEqualTo(GamePath ancestor)
    {
        if (DriveRoot != ancestor.DriveRoot)
        {
            return false;
        }

        if (ancestor.Segments.Count > Segments.Count)
        {
            return false;
        }

        for (var i = 0; i < ancestor.Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], ancestor.Segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>返回相对 <paramref name="ancestor"/> 的段切片；不是祖先关系时返回 false。</summary>
    public bool TryGetRelativeSegments(GamePath ancestor, out IReadOnlyList<string> relative)
    {
        if (!IsUnderOrEqualTo(ancestor) || ancestor.Segments.Count > Segments.Count)
        {
            relative = [];
            return false;
        }

        relative = Segments.Skip(ancestor.Segments.Count).ToArray();
        return true;
    }

    public override string ToString() => PhysicalPath;

    public bool Equals(GamePath? other) =>
        other is not null
        && string.Equals(ComparisonKey, other.ComparisonKey, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as GamePath);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(ComparisonKey);

    private static string BuildComparisonKey(char driveUpper, string[] segments) =>
        segments.Length == 0
            ? driveUpper + @":\"
            : (driveUpper + @":\" + string.Join(PrimarySeparator, segments)).ToUpperInvariant();

    private static string SegmentBaseName(string segment)
    {
        var dot = segment.IndexOf('.');
        return dot < 0 ? segment : segment[..dot];
    }

    private static bool IsAsciiLetter(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}
