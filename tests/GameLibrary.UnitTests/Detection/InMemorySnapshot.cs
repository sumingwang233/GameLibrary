using GameLibrary.Domain.Detection;

namespace GameLibrary.UnitTests.Detection;

/// <summary>内存快照：路径用 '/' 分隔；可模拟不可读文件。</summary>
internal sealed class InMemorySnapshot : IDirectorySnapshot
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Size, byte[]? Head, bool Unreadable, byte[]? Content)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExecutableIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySnapshot Identity(string path, ExecutableIdentity identity) { _identities[path] = identity; return File(path); }

    public ExecutableIdentity? ReadExecutableIdentity(string relativePath) => _identities.GetValueOrDefault(relativePath);

    public string RootPhysicalPath => @"X:\in-memory-root";

    public InMemorySnapshot Directory(string relativePath)
    {
        _directories.Add(relativePath);
        return this;
    }

    public InMemorySnapshot File(string relativePath, byte[]? head = null, bool unreadable = false)
    {
        _files[relativePath] = (head?.Length ?? 10, head, unreadable, null);
        return this;
    }

    /// <summary>带完整内容的文件：支持按偏移读取，用于内嵌 PCK 等尾部证据。</summary>
    public InMemorySnapshot FileContent(string relativePath, byte[] content, bool unreadable = false)
    {
        _files[relativePath] = (content.Length, content, unreadable, content);
        return this;
    }

    public bool DirectoryExists(string relativePath) => _directories.Contains(relativePath);

    public bool FileExists(string relativePath) => _files.ContainsKey(relativePath);

    public long? FileSize(string relativePath) => _files.TryGetValue(relativePath, out var f) ? f.Size : null;

    public IReadOnlyList<string> ListDirectories(string relativePath) =>
        [.. _directories
            .Where(d => ParentOf(d) == relativePath)
            .Select(d => d[(d.LastIndexOf('/') + 1)..])
            .OrderBy(d => d, StringComparer.Ordinal)];

    public IReadOnlyList<string> ListFiles(string relativePath) =>
        [.. _files.Keys
            .Where(f => ParentOf(f) == relativePath)
            .Select(f => f[(f.LastIndexOf('/') + 1)..])
            .OrderBy(f => f, StringComparer.Ordinal)];

    public FileProbe TryReadFirstBytes(string relativePath, int count)
    {
        if (!_files.TryGetValue(relativePath, out var file))
        {
            return FileProbe.NotFound();
        }

        if (file.Unreadable)
        {
            return FileProbe.Unreadable();
        }

        return FileProbe.Ok(file.Head ?? []);
    }

    public FileProbe TryReadBytesAt(string relativePath, long offset, int count)
    {
        if (!_files.TryGetValue(relativePath, out var file))
        {
            return FileProbe.NotFound();
        }

        if (file.Unreadable)
        {
            return FileProbe.Unreadable();
        }

        var content = file.Content ?? file.Head ?? [];
        if (offset < 0 || offset >= content.LongLength)
        {
            return FileProbe.Ok([]);
        }

        var length = (int)Math.Min(count, content.LongLength - offset);
        return FileProbe.Ok(content.AsSpan((int)offset, length).ToArray());
    }

    private static string ParentOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? "" : relativePath[..index];
    }
}
