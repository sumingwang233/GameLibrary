using System.Security.Cryptography;
using GameLibrary.Domain.Identity;

namespace GameLibrary.Infrastructure.Scanning;

/// <summary>
/// 匹配指纹计算器（ADR-0001 第四键的 I/O 执行器）：枚举游戏根、应用 <see cref="FingerprintPolicy"/>
/// 选型、SHA-256（≤1 MiB 全量 / 入口首尾各 64 KiB）、预算封顶、缺失记 MissingReason、
/// 整根不可读返回 null（指纹是线索不是身份，照常 accept/relink）。与 DirectoryWalker 同层；
/// 必须在 store 锁外调用（handler 内），禁止在持锁状态做文件 I/O。
/// </summary>
public static class MatchFingerprintCalculator
{
    private const int HeadTailChunkBytes = 64 * 1024;
    private const int MaxEnumeratedFiles = 20_000;

    /// <summary>
    /// 计算指纹。rootPhysicalPath 可以是目录或独立文件（fileGame：单文件即入口）；
    /// entryPhysicalPath 为绝对路径或 null（无入口）；engine 为库内引擎串。
    /// </summary>
    public static MatchFingerprint? Calculate(
        string rootPhysicalPath,
        string? entryPhysicalPath,
        string? engine)
    {
        try
        {
            if (File.Exists(rootPhysicalPath) && !Directory.Exists(rootPhysicalPath))
            {
                return CalculateSingleFile(rootPhysicalPath);
            }

            if (!Directory.Exists(rootPhysicalPath))
            {
                return null;
            }

            var files = EnumerateFiles(rootPhysicalPath);
            var entryKey = TryMakeEntryKey(rootPhysicalPath, entryPhysicalPath);
            var selections = FingerprintPolicy.Select(engine, entryKey, files);
            return BuildEntries(rootPhysicalPath, selections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>入口绝对路径 → 相对根的 '/' 分隔键；不在根下时退化为文件名（改名/移动拷贝的自然入口候选）。</summary>
    public static string? TryMakeEntryKey(string rootPhysicalPath, string? entryPhysicalPath)
    {
        if (string.IsNullOrWhiteSpace(entryPhysicalPath))
        {
            return null;
        }

        string relative;
        if (string.Equals(entryPhysicalPath, rootPhysicalPath, StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetFileName(entryPhysicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        }
        else
        {
            var rootFull = Path.GetFullPath(rootPhysicalPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entryFull = Path.GetFullPath(entryPhysicalPath);
            if (!entryFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(entryFull);
                return string.IsNullOrEmpty(fileName) ? null : fileName.Replace('\\', '/');
            }

            relative = Path.GetRelativePath(
                rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                entryFull);
        }

        var normalized = relative.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? null : normalized;
    }

    private static MatchFingerprint? CalculateSingleFile(string filePath)
    {
        long size;
        try
        {
            size = new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var key = Path.GetFileName(filePath);
        var mode = size <= FingerprintPolicy.SmallFileLimitBytes
            ? FingerprintHashMode.Full
            : FingerprintHashMode.HeadTail64KiB;
        var sha = mode == FingerprintHashMode.Full ? TryHashFull(filePath) : TryHashHeadTail(filePath);
        return new MatchFingerprint
        {
            StrategyVersion = FingerprintPolicy.StrategyVersion,
            Entries =
            [
                new MatchFingerprint.FingerprintEntry
                {
                    RelativeKey = key,
                    SizeBytes = size,
                    Sha256 = sha,
                    MissingReason = sha is null ? "unreadable" : null,
                },
            ],
        };
    }

    private static MatchFingerprint BuildEntries(
        string rootPhysicalPath,
        IReadOnlyList<FingerprintSelection> selections)
    {
        var entries = new List<MatchFingerprint.FingerprintEntry>(selections.Count);
        foreach (var selection in selections)
        {
            if (selection.SizeBytes is null)
            {
                // 策略选中但清单中不存在（入口缺失）：记 MissingReason，不参与相似度。
                entries.Add(new MatchFingerprint.FingerprintEntry
                {
                    RelativeKey = selection.RelativePath,
                    SizeBytes = null,
                    Sha256 = null,
                    MissingReason = "missing",
                });
                continue;
            }

            var fullPath = Path.Combine(
                rootPhysicalPath,
                selection.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? sha = null;
            if (selection.Mode == FingerprintHashMode.Full)
            {
                sha = TryHashFull(fullPath);
            }
            else if (selection.Mode == FingerprintHashMode.HeadTail64KiB)
            {
                sha = TryHashHeadTail(fullPath);
            }

            entries.Add(new MatchFingerprint.FingerprintEntry
            {
                RelativeKey = selection.RelativePath,
                SizeBytes = selection.SizeBytes,
                Sha256 = sha,
                MissingReason = sha is null && selection.Mode != FingerprintHashMode.SizeOnly ? "unreadable" : null,
            });
        }

        return new MatchFingerprint
        {
            StrategyVersion = FingerprintPolicy.StrategyVersion,
            Entries = entries,
        };
    }

    /// <summary>枚举游戏根（深度 ≤ FingerprintPolicy.MaxRelativeDepth，跳过重解析点；逐文件大小异常跳过）。</summary>
    private static List<FingerprintFile> EnumerateFiles(string rootPhysicalPath)
    {
        var result = new List<FingerprintFile>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPhysicalPath, 0));
        while (queue.Count > 0 && result.Count < MaxEnumeratedFiles)
        {
            var (directory, depth) = queue.Dequeue();
            IReadOnlyList<string> fileNames;
            try
            {
                fileNames = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in fileNames)
            {
                if (result.Count >= MaxEnumeratedFiles)
                {
                    break;
                }

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(rootPhysicalPath, file).Replace('\\', '/');
                result.Add(new FingerprintFile(relative, size));
            }

            if (depth >= FingerprintPolicy.MaxRelativeDepth)
            {
                continue;
            }

            IReadOnlyList<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                try
                {
                    if ((new DirectoryInfo(subDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                queue.Enqueue((subDirectory, depth + 1));
            }
        }

        return result;
    }

    private static string? TryHashFull(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.ReadWrite);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>SHA-256(head 64 KiB || tail 64 KiB)；仅用于 &gt;1 MiB 的入口（首尾不重叠）。</summary>
    private static string? TryHashHeadTail(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.ReadWrite);
            using var sha = SHA256.Create();
            var buffer = new byte[HeadTailChunkBytes];

            // 增量式：先喂头 64 KiB，再定位到尾部喂尾 64 KiB。
            var headRead = ReadExactly(stream, buffer, 0, HeadTailChunkBytes);
            sha.TransformBlock(buffer, 0, headRead, null, 0);
            var tailLength = (int)Math.Min(HeadTailChunkBytes, stream.Length);
            stream.Seek(-tailLength, SeekOrigin.End);
            var tailBuffer = new byte[tailLength];
            var tailRead = ReadExactly(stream, tailBuffer, 0, tailLength);
            sha.TransformFinalBlock(tailBuffer, 0, tailRead);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int ReadExactly(FileStream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
