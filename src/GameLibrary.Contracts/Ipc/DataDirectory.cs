using System.Security.Cryptography;
using System.Text;

namespace GameLibrary.Contracts.Ipc;

/// <summary>
/// 数据目录引导解析（契约 2.1）：仅做词法规范化，不访问文件系统；
/// library.init 创建目录后需重新解析。内部协议助手，可随 IPC 版本升级。
/// </summary>
public static class DataDirectory
{
    public static DataDirectoryResult Resolve(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return DataDirectoryResult.Fail("DataDirectoryEmpty");
        }

        if (input.Contains('\0'))
        {
            return DataDirectoryResult.Fail("DataDirectoryNullByte");
        }

        if (input.StartsWith(@"\\?\") || input.StartsWith(@"\\.\"))
        {
            return DataDirectoryResult.Fail("DataDirectoryDeviceNamespace");
        }

        if (input.StartsWith(@"\\"))
        {
            return DataDirectoryResult.Fail("DataDirectoryUnc");
        }

        if (input.Length < 3
            || !IsAsciiLetter(input[0])
            || input[1] != ':'
            || input[2] is not ('\\' or '/'))
        {
            return DataDirectoryResult.Fail("DataDirectoryNotAbsoluteLocal");
        }

        foreach (var ch in input)
        {
            if (ch is '"' or '<' or '>' or '|' or '?' or '*' || char.IsControl(ch))
            {
                return DataDirectoryResult.Fail("DataDirectoryIllegalCharacters");
            }
        }

        var segments = input[3..]
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var resolved = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    return DataDirectoryResult.Fail("DataDirectoryEscapesRoot");
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return DataDirectoryResult.Fail("DataDirectoryTrailingDotOrSpace");
            }

            if (segment.Contains(':'))
            {
                return DataDirectoryResult.Fail("DataDirectoryIllegalColon");
            }

            resolved.Add(segment);
        }

        var drive = char.ToUpperInvariant(input[0]) + @":\";
        var canonical = resolved.Count == 0
            ? drive
            : drive + string.Join('\\', resolved);

        return DataDirectoryResult.Ok(canonical, canonical.ToUpperInvariant());
    }

    private static bool IsAsciiLetter(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}

public sealed record DataDirectoryResult
{
    public string? CanonicalPath { get; private init; }

    /// <summary>比较形态（InvariantCulture 大写）；用于派生管道/互斥名，避免大小写与分隔符别名造成双宿主。</summary>
    public string? ComparisonKey { get; private init; }

    public string? Error { get; private init; }

    public bool IsValid => CanonicalPath is not null;

    public static DataDirectoryResult Ok(string canonical, string comparisonKey) =>
        new() { CanonicalPath = canonical, ComparisonKey = comparisonKey };

    public static DataDirectoryResult Fail(string error) => new() { Error = error };
}

/// <summary>由规范化数据目录派生同用户管道名与宿主互斥名；Host 与所有客户端必须使用同一公式。</summary>
public static class ChannelNames
{
    private const string Domain = "GameLibrary|v1|";

    public static string PipeName(string comparisonKey) =>
        "GameLibrary." + Digest(comparisonKey)[..16];

    public static string MutexName(string comparisonKey) =>
        @"Local\GameLibrary.Host." + Digest(comparisonKey);

    public static string LockFileName => "host.lock";

    private static string Digest(string comparisonKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + comparisonKey));
        return Convert.ToHexStringLower(hash);
    }
}
