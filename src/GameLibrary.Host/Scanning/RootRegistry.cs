using System.Collections.Concurrent;
using GameLibrary.Contracts;
using GameLibrary.Domain.Paths;

namespace GameLibrary.Host.Scanning;

/// <summary>已注册库根：扫描与启动的路径包含边界（宿主内存态；持久化随 T16）。</summary>
public sealed record LibraryRoot
{
    public required string RootId { get; init; }

    public required GamePath Path { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public object ToDto() => new
    {
        rootId = RootId,
        path = Path.PhysicalPath,
        createdUtc = CreatedUtc.ToString("O"),
    };
}

/// <summary>
/// 库根白名单：scan.start/scan.inspect/profiles.* 的调用方路径必须落在某个已注册根内
/// （L3 审计修复：CWE-22 路径包含边界在 dispatcher 收口，不依赖下游错误分类）。
/// 注册是显式授权动作（roots.add，权限 scan.manage）；重解析点不允许作为根。
/// </summary>
public sealed class RootRegistry
{
    private readonly ConcurrentDictionary<string, LibraryRoot> _byRootId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rootIdByComparisonKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册库根；同一规范化路径幂等返回既有根。路径必须存在且不是重解析点。</summary>
    public LibraryRoot Add(string physicalPath)
    {
        physicalPath = physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var validation = GamePath.TryCreate(physicalPath);
        if (!validation.IsValid)
        {
            throw new RootRegistryException(ErrorCodes.InvalidPath, $"根路径非法（{validation.Reason}）：{physicalPath}");
        }

        var root = validation.Path!;
        if (!Directory.Exists(root.PhysicalPath))
        {
            throw new RootRegistryException(ErrorCodes.RootOffline, $"根路径不存在或离线：{root.PhysicalPath}");
        }

        if ((new DirectoryInfo(root.PhysicalPath).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new RootRegistryException(ErrorCodes.UnsupportedPath, $"重解析点不允许作为库根：{root.PhysicalPath}");
        }

        if (_rootIdByComparisonKey.TryGetValue(root.ComparisonKey, out var existingId))
        {
            return _byRootId[existingId];
        }

        var libraryRoot = new LibraryRoot
        {
            RootId = $"root-{Guid.NewGuid():N}",
            Path = root,
            CreatedUtc = DateTime.UtcNow,
        };
        _byRootId[libraryRoot.RootId] = libraryRoot;
        _rootIdByComparisonKey[root.ComparisonKey] = libraryRoot.RootId;
        return libraryRoot;
    }

    public IReadOnlyList<LibraryRoot> List() =>
        _byRootId.Values.OrderBy(r => r.RootId, StringComparer.Ordinal).ToArray();

    /// <summary>路径包含判定：candidate 必须等于或位于某个已注册根之下（规范化物理路径前缀比较）。</summary>
    public bool Contains(string physicalPath)
    {
        var validation = GamePath.TryCreate(physicalPath);
        if (!validation.IsValid)
        {
            return false;
        }

        var candidate = validation.Path!.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in _byRootId.Values)
        {
            var rootPath = root.Path.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(candidate, rootPath, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>库根注册错误：携带公开错误码。</summary>
public sealed class RootRegistryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
