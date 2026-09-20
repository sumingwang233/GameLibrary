namespace GameLibrary.Domain.Identity;

/// <summary>哈希模式：小文件全量 / 入口首尾各 64 KiB / 只记大小不哈希（&gt;1 MiB 的检测器标记内容文件）。</summary>
public enum FingerprintHashMode
{
    /// <summary>≤1 MiB：整文件 SHA-256。</summary>
    Full,

    /// <summary>&gt;1 MiB 的入口可执行：SHA-256(head 64 KiB || tail 64 KiB)。</summary>
    HeadTail64KiB,

    /// <summary>&gt;1 MiB 的检测器标记内容文件（如大 xp3/globalgamemanagers）：只记 SizeBytes，Sha256=null。</summary>
    SizeOnly,
}

/// <summary>策略选中的条目（有序：入口在前，内容按选择顺序）。</summary>
public sealed record FingerprintSelection(
    string RelativePath,
    long? SizeBytes,
    FingerprintHashMode Mode);

/// <summary>
/// 指纹条目选型策略（ADR-0001 第四键）：只选「游戏自有内容」（跨游戏几乎必异），
/// 排除引擎运行时/壳（renpy/、UnityPlayer.dll、*_Data/Managed|Plugins/、rpg_core.js 系、nw.exe）
/// ——这是 matched≥2 门控下控制假阳性的根基。与 ClassificationRules 同为 Domain 纯逻辑：
/// 输入内存文件清单 + 引擎 + 入口相对路径，输出有序选中条目及哈希模式；真实 I/O 由扫描层执行器承担。
/// 硬性预算：条目总数 ≤5（入口 1 + 内容 ≤3）；内容文件全量哈希总计 ≤4 MiB（≤3 个 × ≤1 MiB，结构性满足）。
/// </summary>
public static class FingerprintPolicy
{
    /// <summary>指纹策略版本；选型规则演进必须递增（旧指纹自动不可比）。</summary>
    public const int StrategyVersion = 1;

    /// <summary>小文件上限：≤1 MiB 全量哈希。</summary>
    public const long SmallFileLimitBytes = 1024 * 1024;

    /// <summary>内容文件条数上限（入口之外）。</summary>
    public const int MaxContentEntries = 3;

    /// <summary>内容文件全量哈希总字节预算。</summary>
    public const long ContentHashBudgetBytes = 4L * 1024 * 1024;

    /// <summary>枚举深度上限：root=0；RPGMV www/img 与 Unity *_Data/子目录 都在 2 层内，3 层留余量。</summary>
    public const int MaxRelativeDepth = 3;

    /// <summary>文件名形式的引擎运行时/壳排除清单（任意深度命中即排除；仅作用于内容选择，入口豁免）。</summary>
    private static readonly HashSet<string> RuntimeFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll",
        "nw.exe",
        "rpg_core.js",
        "rmmz_core.js",
    };

    /// <summary>
    /// 选型主入口。engine 为库内引擎串（unity/rpgMakerMvMz/renpy/kirikiri/flash，大小写不敏感；
    /// 其余含 null 走未知引擎回退）；entryRelativeKey 以 '/' 分隔、无前导分隔，null 表示无入口。
    /// 文件缺失的入口仍会产出条目（SizeBytes=null，由执行器记 MissingReason）。
    /// </summary>
    public static IReadOnlyList<FingerprintSelection> Select(
        string? engine,
        string? entryRelativeKey,
        IReadOnlyList<FingerprintFile> files)
    {
        var byPath = new Dictionary<string, FingerprintFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            // 清单理论上无重复；重复时保留首个，保证确定性。
            if (!byPath.ContainsKey(file.RelativePath))
            {
                byPath[file.RelativePath] = file;
            }
        }

        var selections = new List<FingerprintSelection>();
        var entryKey = Normalize(entryRelativeKey);
        if (entryKey is not null)
        {
            selections.Add(byPath.TryGetValue(entryKey, out var entry)
                ? new FingerprintSelection(
                    entryKey,
                    entry.SizeBytes,
                    entry.SizeBytes <= SmallFileLimitBytes ? FingerprintHashMode.Full : FingerprintHashMode.HeadTail64KiB)
                : new FingerprintSelection(entryKey, null, FingerprintHashMode.SizeOnly));
        }

        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entryKey is not null)
        {
            chosen.Add(entryKey);
        }

        switch (NormalizeEngine(engine))
        {
            case "unity":
                SelectUnity(byPath, selections, chosen);
                break;
            case "rpgmakermvmz":
                SelectRpgMakerMvMz(byPath, selections, chosen);
                break;
            case "renpy":
                SelectSmallestUnderPrefix(byPath, selections, chosen, ["game/"],
                    1 + MaxContentEntries - selections.Count);
                break;
            case "kirikiri":
                SelectKirikiri(byPath, selections, chosen);
                break;
            case "flash":
                // .swf 即入口，单条 hashed；v1 明确不出内容条目。
                break;
            default:
                SelectSmallestAtRoot(byPath, selections, chosen, requireNonExe: false,
                    count: 1 + MaxContentEntries - selections.Count);
                break;
        }

        return selections;
    }

    /// <summary>Unity：*_Data/globalgamemanagers（判定标记）+ *_Data/（含一级子目录，排除 Managed/、Plugins/）下最小 2 个 ≤1 MiB 文件。</summary>
    private static void SelectUnity(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen)
    {
        var ggm = byPath.Keys
            .Where(path => path.Split('/').Length == 2
                && path.EndsWith("/globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                && path[..path.LastIndexOf('/')].EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (ggm is not null)
        {
            TakeContent(byPath, selections, chosen, ggm);
        }

        var dataPrefix = ggm is not null
            ? ggm[..(ggm.LastIndexOf('/') + 1)]
            : byPath.Keys
                .Where(IsUnityDataPath)
                .Select(path => path[..(path.IndexOf('/') + 1)])
                .OrderBy(prefix => prefix, StringComparer.Ordinal)
                .FirstOrDefault();

        var fill = byPath.Values
            .Where(file => dataPrefix is not null
                && file.RelativePath.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)
                && DepthOf(file.RelativePath) <= 2
                && !IsUnderManagedOrPlugins(file.RelativePath)
                && !IsExcludedRuntime(file.RelativePath)
                && file.SizeBytes <= SmallFileLimitBytes)
            .OrderBy(file => file.SizeBytes)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in fill)
        {
            if (selections.Count >= 1 + MaxContentEntries)
            {
                break;
            }

            TakeContent(byPath, selections, chosen, file.RelativePath);
        }
    }

    /// <summary>RPGMV/MZ：System.json（www/data/ 或 data/）+ package.json（根或 www/），
    /// 不足 3 个内容时从 www/img/ 或 img/ 下取最小 ≤1 MiB 文件补齐；不选 core-js（引擎运行时）。</summary>
    private static void SelectRpgMakerMvMz(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen)
    {
        foreach (var key in new[] { "www/data/System.json", "data/System.json", "package.json", "www/package.json" })
        {
            if (selections.Count >= 1 + MaxContentEntries)
            {
                break;
            }

            if (byPath.ContainsKey(key))
            {
                TakeContent(byPath, selections, chosen, key);
            }
        }

        // 不足 3 个内容时从 www/img/ 或 img/ 下取最小 ≤1 MiB 文件补齐。
        SelectSmallestUnderPrefix(
            byPath, selections, chosen, ["www/img/", "img/"], 1 + MaxContentEntries - selections.Count);
    }

    /// <summary>Kirikiri：最小 1 个根级 .xp3（&gt;1 MiB 只记 SizeBytes）+ 根/一级子目录下最小 2 个 ≤1 MiB 其他文件（排除 .exe）。</summary>
    private static void SelectKirikiri(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen)
    {
        var xp3 = byPath.Values
            .Where(file => DepthOf(file.RelativePath) == 0
                && file.RelativePath.EndsWith(".xp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.SizeBytes)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => file.RelativePath)
            .FirstOrDefault();
        if (xp3 is not null)
        {
            TakeContent(byPath, selections, chosen, xp3);
        }

        SelectSmallestAtRoot(byPath, selections, chosen, requireNonExe: true,
            count: 1 + MaxContentEntries - selections.Count);
    }

    /// <summary>未知引擎回退：根目录最小 count 个 ≤1 MiB 文件（排除运行时清单）。</summary>
    private static void SelectSmallestAtRoot(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen,
        bool requireNonExe,
        int count)
    {
        var candidates = byPath.Values
            .Where(file => DepthOf(file.RelativePath) == 0
                && file.SizeBytes <= SmallFileLimitBytes
                && !IsExcludedRuntime(file.RelativePath)
                && (!requireNonExe || !file.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.SizeBytes)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            if (count <= 0 || selections.Count >= 1 + MaxContentEntries)
            {
                break;
            }

            if (TakeContent(byPath, selections, chosen, file.RelativePath))
            {
                count--;
            }
        }
    }

    /// <summary>前缀池（如 game/、www/img/、img/）下最小 ≤1 MiB 文件补齐至内容上限。</summary>
    private static void SelectSmallestUnderPrefix(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen,
        string[] prefixes,
        int count)
    {
        if (count <= 0)
        {
            return;
        }

        var candidates = byPath.Values
            .Where(file => prefixes.Any(prefix =>
                    file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && DepthOf(file.RelativePath) <= 2
                && file.SizeBytes <= SmallFileLimitBytes
                && !IsExcludedRuntime(file.RelativePath))
            .OrderBy(file => file.SizeBytes)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            if (count <= 0 || selections.Count >= 1 + MaxContentEntries)
            {
                break;
            }

            if (TakeContent(byPath, selections, chosen, file.RelativePath))
            {
                count--;
            }
        }
    }

    /// <summary>追加一条内容条目（≤1 MiB 全量，&gt;1 MiB 只记大小）；已选/超限返回 false。</summary>
    private static bool TakeContent(
        IReadOnlyDictionary<string, FingerprintFile> byPath,
        List<FingerprintSelection> selections,
        HashSet<string> chosen,
        string relativePath)
    {
        if (selections.Count >= 1 + MaxContentEntries || chosen.Contains(relativePath))
        {
            return false;
        }

        if (!byPath.TryGetValue(relativePath, out var file))
        {
            return false;
        }

        chosen.Add(relativePath);
        selections.Add(new FingerprintSelection(
            relativePath,
            file.SizeBytes,
            file.SizeBytes <= SmallFileLimitBytes ? FingerprintHashMode.Full : FingerprintHashMode.SizeOnly));
        return true;
    }

    /// <summary>引擎运行时/壳排除（内容选择专用）：renpy/ 前缀、运行时文件名、*_Data/Managed|Plugins/ 段。</summary>
    private static bool IsExcludedRuntime(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (RuntimeFileNames.Contains(segments[^1]))
        {
            return true;
        }

        if (segments[0].Equals("renpy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsUnderManagedOrPlugins(relativePath);
    }

    /// <summary>路径中任一 *_Data 目录之下的 Managed/ 或 Plugins/ 子目录（Unity 引擎运行时）。</summary>
    private static bool IsUnderManagedOrPlugins(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if ((segments[i].Equals("Managed", StringComparison.OrdinalIgnoreCase)
                    || segments[i].Equals("Plugins", StringComparison.OrdinalIgnoreCase))
                && segments[i - 1].EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnityDataPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length >= 1 && segments[0].EndsWith("_Data", StringComparison.OrdinalIgnoreCase);
    }

    private static int DepthOf(string relativePath)
    {
        var depth = 0;
        foreach (var ch in relativePath)
        {
            if (ch == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    private static string? Normalize(string? relativeKey)
    {
        if (string.IsNullOrWhiteSpace(relativeKey))
        {
            return null;
        }

        var normalized = relativeKey.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeEngine(string? engine) =>
        string.IsNullOrWhiteSpace(engine) ? null : engine.Trim().ToLowerInvariant();
}

/// <summary>执行器枚举出的文件清单条目（相对路径以 '/' 分隔 + 大小）。</summary>
public sealed record FingerprintFile(string RelativePath, long SizeBytes);
