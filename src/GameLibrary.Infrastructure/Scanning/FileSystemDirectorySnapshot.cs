using System.Text;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Paths;

namespace GameLibrary.Infrastructure.Scanning;

/// <summary>
/// 真实文件系统的只读快照实现：所有访问只读；读取受限字节数并区分不可读。
/// 相对路径以 '/' 分隔，磁盘路径由快照根拼接。
/// </summary>
public sealed class FileSystemDirectorySnapshot : IDirectorySnapshot
{
    private readonly string _root;

    public FileSystemDirectorySnapshot(GamePath root)
    {
        _root = root.PhysicalPath;
    }

    public string RootPhysicalPath => _root;

    public bool DirectoryExists(string relativePath) => Directory.Exists(Resolve(relativePath));

    public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

    public long? FileSize(string relativePath)
    {
        var info = new FileInfo(Resolve(relativePath));
        return info.Exists ? info.Length : null;
    }

    public IReadOnlyList<string> ListDirectories(string relativePath)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(Resolve(relativePath)).Select(p => Path.GetFileName(p) ?? "").OrderBy(n => n, StringComparer.Ordinal)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> ListFiles(string relativePath)
    {
        try
        {
            return [.. Directory.EnumerateFiles(Resolve(relativePath)).Select(p => Path.GetFileName(p) ?? "").OrderBy(n => n, StringComparer.Ordinal)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public FileProbe TryReadFirstBytes(string relativePath, int count)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath))
        {
            return FileProbe.NotFound();
        }

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.ReadWrite,
                count,
                FileOptions.Asynchronous);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            return read == count ? FileProbe.Ok(buffer) : FileProbe.Ok(buffer[..read]);
        }
        catch (IOException)
        {
            return FileProbe.Unreadable();
        }
        catch (UnauthorizedAccessException)
        {
            return FileProbe.Unreadable();
        }
    }

    private string Resolve(string relativePath) =>
        relativePath.Length == 0
            ? _root
            : Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
