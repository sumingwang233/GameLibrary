using System.Collections.Concurrent;
using GameLibrary.Contracts;
using GameLibrary.Domain.Paths;

namespace GameLibrary.Host.Scanning;

/// <summary>已注册库根：扫描与启动的路径包含边界（v13+ 持久化，重启恢复）。</summary>
public sealed record LibraryRoot
{
    public required string RootId { get; init; }

    public required GamePath Path { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// 根类型（v23，bug-5）：library=常规扫描根；manual=手动添加游戏时为通过路径包含
    /// 校验而注册的边界根——参与 <see cref="RootRegistry.Contains"/> 判定，但被扫描
    /// 枚举（<see cref="RootRegistry.ListScannable"/>）与候选发现排除。
    /// </summary>
    public string Kind { get; init; } = "library";

    /// <summary>乐观修订（roots.remove 校验用；当前根注册后不可变，恒为 1）。</summary>
    public int Revision { get; init; } = 1;

    public object ToDto() => new
    {
        rootId = RootId,
        path = Path.PhysicalPath,
        kind = Kind,
        revision = Revision,
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

    /// <summary>
    /// 注册库根；同一规范化路径幂等返回既有根。路径必须存在且不是重解析点。
    /// kind（v23，bug-5）：library（默认，常规扫描根）或 manual（手动添加游戏的
    /// 路径包含边界根，不被扫描枚举与候选发现）；其他值拒绝。
    /// </summary>
    public LibraryRoot Add(string physicalPath, string kind = "library")
    {
        if (kind is not ("library" or "manual"))
        {
            throw new RootRegistryException(ErrorCodes.InvalidArgument, $"根类型只支持 library/manual：{kind}");
        }

        var validation = GamePath.TryCreate(NormalizeInput(physicalPath));
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
            Kind = kind,
        };
        _byRootId[libraryRoot.RootId] = libraryRoot;
        _rootIdByComparisonKey[root.ComparisonKey] = libraryRoot.RootId;
        return libraryRoot;
    }

    /// <summary>
    /// 输入规范化：去掉多余尾分隔符，但保留盘根（"F:\" 的尾分隔符是路径本体——
    /// 先 TrimEnd 会把它削成 "F:"（盘符相对路径）而被 GamePath 拒绝）。
    /// "X:\" / "X:\\\\" / "X:///" 统一归一为 "X:\"；"X:"（无分隔符）保持原样，
    /// 由 GamePath 按盘符相对路径拒绝。
    /// </summary>
    private static string NormalizeInput(string physicalPath)
    {
        var trimmed = physicalPath.Trim();
        var inner = trimmed.TrimEnd('\\', '/');
        if (inner.Length == 2 && inner[1] == ':')
        {
            return trimmed.Length > inner.Length ? inner + "\\" : trimmed;
        }

        return trimmed.Length > inner.Length ? inner : trimmed;
    }

    /// <summary>
    /// 启动恢复：把持久层的库根直接注册回内存（不要求目录当前在线——离线根由
    /// 扫描/核对按 RootOffline 分级处理，注册表不能因目录暂时离线而丢配置）。
    /// 同一规范化路径已注册时幂等返回既有根。
    /// </summary>
    public LibraryRoot AddExisting(string rootId, string physicalPath, DateTime createdUtc, string kind = "library")
    {
        var validation = GamePath.TryCreate(NormalizeInput(physicalPath));
        if (!validation.IsValid)
        {
            throw new RootRegistryException(ErrorCodes.InvalidPath, $"持久化根路径非法（{validation.Reason}）：{physicalPath}");
        }

        var root = validation.Path!;
        if (_rootIdByComparisonKey.TryGetValue(root.ComparisonKey, out var existingId))
        {
            return _byRootId[existingId];
        }

        var libraryRoot = new LibraryRoot
        {
            RootId = rootId,
            Path = root,
            CreatedUtc = createdUtc,
            Kind = kind,
        };
        _byRootId[libraryRoot.RootId] = libraryRoot;
        _rootIdByComparisonKey[root.ComparisonKey] = libraryRoot.RootId;
        return libraryRoot;
    }

    /// <summary>按 ID 移除库根（roots.remove：仅解除监控边界，不触碰游戏数据）。</summary>
    public LibraryRoot? Remove(string rootId) =>
        _byRootId.TryRemove(rootId, out var removed)
            && _rootIdByComparisonKey.TryRemove(removed.Path.ComparisonKey, out _)
            ? removed
            : null;

    public IReadOnlyList<LibraryRoot> List() =>
        _byRootId.Values.OrderBy(r => r.RootId, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 可枚举扫描根（v23，bug-5）：仅 kind='library'。周期核对/候选发现从这里取根，
    /// manual 根只作路径包含边界，绝不进入扫描枚举。
    /// </summary>
    public IReadOnlyList<LibraryRoot> ListScannable() =>
        _byRootId.Values
            .Where(r => !string.Equals(r.Kind, "manual", StringComparison.Ordinal))
            .OrderBy(r => r.RootId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>路径包含判定：candidate 必须等于或位于某个已注册根之下（规范化物理路径前缀比较）。
    /// 盘根的 PhysicalPath 自带尾分隔符（"F:\"），比较前统一剥掉再补齐，避免 "F:\\" 双写失配。</summary>
    public bool Contains(string physicalPath)
    {
        var validation = GamePath.TryCreate(NormalizeInput(physicalPath));
        if (!validation.IsValid)
        {
            return false;
        }

        var candidate = validation.Path!.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in _byRootId.Values)
        {
            var rootPrefix = root.Path.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(candidate, rootPrefix, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(rootPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
