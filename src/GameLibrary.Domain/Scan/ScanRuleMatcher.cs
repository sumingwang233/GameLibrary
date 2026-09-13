using GameLibrary.Domain.Paths;

namespace GameLibrary.Domain.Scan;

/// <summary>
/// 受限 matcher（补充规格 2.1）：仅精确路径、分段子树、精确 basename、扩展名、受限 glob
/// （段内 *、? 单字符、** 跨段、[] 字面量）。不引入正则/表达式执行。
/// 全部按 Windows 路径语义（OrdinalIgnoreCase）比较。
/// </summary>
public abstract record ScanRuleMatcher
{
    public abstract bool Matches(GamePath path);
}

/// <summary>精确路径相等。</summary>
public sealed record ExactPathMatcher(string Path) : ScanRuleMatcher
{
    public override bool Matches(GamePath path) =>
        string.Equals(path.ComparisonKey, Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>分段子树：路径等于子树根或位于其下（分段边界，F:\Games 不包含 F:\Games2）。</summary>
public sealed record SubtreeMatcher(string SubtreeRoot) : ScanRuleMatcher
{
    public override bool Matches(GamePath path)
    {
        var root = GamePath.Create(SubtreeRoot);
        return path.IsUnderOrEqualTo(root);
    }
}

/// <summary>任意目录层的精确 basename（文件或目录名）。</summary>
public sealed record BasenameMatcher(string Basename) : ScanRuleMatcher
{
    public override bool Matches(GamePath path) =>
        path.Segments.Count > 0
        && string.Equals(path.Segments[^1], Basename, StringComparison.OrdinalIgnoreCase);
}

/// <summary>扩展名匹配（不含点，如 "xp3"）；对目录同样按名字扩展判断。</summary>
public sealed record ExtensionMatcher(string Extension) : ScanRuleMatcher
{
    public override bool Matches(GamePath path)
    {
        if (path.Segments.Count == 0)
        {
            return false;
        }

        var name = path.Segments[^1];
        var dot = name.LastIndexOf('.');
        return dot >= 0
            && string.Equals(name[(dot + 1)..], Extension, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 受限 glob：'/' 或 '\' 分段；段内 * 任意串、? 单字符；整段 ** 匹配任意层数；
/// [ 与 ] 按字面量匹配以适配用户分类目录（不做字符集语义）。
/// </summary>
public sealed record GlobMatcher(string Pattern) : ScanRuleMatcher
{
    public override bool Matches(GamePath path)
    {
        var patternSegments = SplitSegments(Pattern);
        return GlobMatch(patternSegments, path.Segments);
    }

    private static string[] SplitSegments(string pattern) =>
        pattern.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static bool GlobMatch(string[] pattern, IReadOnlyList<string> segments)
    {
        return MatchFrom(pattern, 0, segments, 0);
    }

    private static bool MatchFrom(string[] pattern, int pi, IReadOnlyList<string> segments, int si)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == "**")
            {
                // ** 匹配零个或多个段：优先贪心回溯。
                for (var skip = segments.Count - si; skip >= 0; skip--)
                {
                    if (MatchFrom(pattern, pi + 1, segments, si + skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (si >= segments.Count)
            {
                return false;
            }

            if (!SegmentMatches(pattern[pi], segments[si]))
            {
                return false;
            }

            pi++;
            si++;
        }

        return si == segments.Count;
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        // 双指针通配匹配；'*' 段内任意串，'?' 单字符，其余（含 []）字面量。
        int p = 0, s = 0, starP = -1, starS = 0;
        while (s < segment.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], segment[s])))
            {
                p++;
                s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starS = s;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                s = ++starS;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b) =>
        char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
