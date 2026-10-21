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
    private readonly Dictionary<string, IReadOnlyList<string>> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExecutableIdentity?> _identities = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemDirectorySnapshot(GamePath root)
    {
        _root = root.PhysicalPath;
    }

    public FileSystemDirectorySnapshot(
        GamePath root,
        IReadOnlyList<string> rootDirectories,
        IReadOnlyList<string> rootFiles)
        : this(root)
    {
        _directories[""] = NormalizeNames(rootDirectories);
        _files[""] = NormalizeNames(rootFiles);
    }

    public string RootPhysicalPath => _root;

    public bool DirectoryExists(string relativePath) =>
        TryFindInCachedParent(relativePath, _directories, out var exists)
            ? exists
            : Directory.Exists(Resolve(relativePath));

    public bool FileExists(string relativePath) =>
        TryFindInCachedParent(relativePath, _files, out var exists)
            ? exists
            : File.Exists(Resolve(relativePath));

    public long? FileSize(string relativePath)
    {
        var info = new FileInfo(Resolve(relativePath));
        return info.Exists ? info.Length : null;
    }

    public IReadOnlyList<string> ListDirectories(string relativePath)
    {
        if (_directories.TryGetValue(NormalizeRelativePath(relativePath), out var cached))
        {
            return cached;
        }

        try
        {
            var result = NormalizeNames(Directory.EnumerateDirectories(Resolve(relativePath)).ToArray());
            _directories[NormalizeRelativePath(relativePath)] = result;
            return result;
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
        if (_files.TryGetValue(NormalizeRelativePath(relativePath), out var cached))
        {
            return cached;
        }

        try
        {
            var result = NormalizeNames(Directory.EnumerateFiles(Resolve(relativePath)).ToArray());
            _files[NormalizeRelativePath(relativePath)] = result;
            return result;
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
            if (count is < 1 or > 1024 * 1024 || !IsSafeProbe(fullPath)) return FileProbe.Unreadable();
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

    public FileProbe TryReadBytesAt(string relativePath, long offset, int count)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath))
        {
            return FileProbe.NotFound();
        }

        try
        {
            if (offset < 0 || count is < 1 or > 1024 * 1024 || !IsSafeProbe(fullPath)) return FileProbe.Unreadable();
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.ReadWrite,
                Math.Min(count, 4096),
                FileOptions.Asynchronous);
            stream.Seek(offset, SeekOrigin.Begin);
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

            return FileProbe.Ok(buffer[..read]);
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

    public ExecutableIdentity? ReadExecutableIdentity(string relativePath)
    {
        if (_identities.TryGetValue(relativePath, out var cached)) return cached;
        ExecutableIdentity? identity = null;
        try
        {
            var path = Resolve(relativePath);
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && IsSafeProbe(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                identity = new(info.FileDescription, info.CompanyName, info.ProductName, info.OriginalFilename);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        _identities[relativePath] = identity;
        return identity;
    }

    private bool IsSafeProbe(string path)
    {
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (current.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private string Resolve(string relativePath) =>
        relativePath.Length == 0
            ? _root
            : Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<string> NormalizeNames(IEnumerable<string> paths) =>
        paths.Select(path => Path.GetFileName(path) ?? "")
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    private static bool TryFindInCachedParent(
        string relativePath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> cache,
        out bool exists)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var separator = normalized.LastIndexOf('/');
        var parent = separator >= 0 ? normalized[..separator] : "";
        var name = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        if (!cache.TryGetValue(parent, out var entries))
        {
            exists = false;
            return false;
        }

        exists = entries.Contains(name, StringComparer.OrdinalIgnoreCase);
        return true;
    }
}
