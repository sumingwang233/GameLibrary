using System.IO;

namespace GameLibrary.Host.Tools;

/// <summary>
/// 只清理库自有缓存树里的普通文件。目录链接、文件链接与 junction 均不跟随；
/// 用户原图、游戏库目录从不传入此清理器。
/// </summary>
internal static class CacheDirectoryCleaner
{
    internal sealed record Result(long RemovedFiles, long RemovedBytes, long SkippedLinks, long SkippedErrors);

    public static Result Clear(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return new Result(0, 0, 0, 0);
        }

        // cache/ 自身若被替换为 junction，拒绝整次操作，不能把外部目录当成本库缓存。
        if (IsReparsePoint(cacheDirectory))
        {
            throw new InvalidOperationException("缓存目录是链接或 junction，已拒绝清理");
        }

        long files = 0;
        long bytes = 0;
        long links = 0;
        long errors = 0;
        var pending = new Stack<string>();
        pending.Push(cacheDirectory);
        while (pending.TryPop(out var current))
        {
            try
            {
                if (IsReparsePoint(current))
                {
                    links++;
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            links++;
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                            continue;
                        }

                        var length = new FileInfo(entry).Length;
                        File.Delete(entry);
                        files++;
                        bytes += length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
            }
        }

        return new Result(files, bytes, links, errors);
    }

    public static long CountRegularFiles(string directory)
    {
        if (!Directory.Exists(directory) || IsReparsePoint(directory))
        {
            return 0;
        }

        long files = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            try
            {
                if (IsReparsePoint(current))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                        }
                        else
                        {
                            files++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 诊断计数不是清理的前提；不可读项不影响其他文件。
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 同上，不追踪无法读取的子目录。
            }
        }

        return files;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
