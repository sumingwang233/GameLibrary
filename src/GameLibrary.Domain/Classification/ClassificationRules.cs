using System.Text.RegularExpressions;

namespace GameLibrary.Domain.Classification;

/// <summary>分类标签类型（策划案 2.2 分类语义表）。</summary>
public enum FolderTagKind
{
    /// <summary>厂商/作者分类（标注"来自文件夹"，不声称独立核验过厂商）。</summary>
    Publisher,

    /// <summary>引擎/格式线索；引擎事实仍需文件证据。</summary>
    Engine,

    /// <summary>类型/个人分类。</summary>
    Category,

    /// <summary>合集分类。</summary>
    Collection,

    /// <summary>收集批次；不推断游戏发行日期。</summary>
    Batch,
}

/// <summary>一个祖先目录段贡献的分类标签；多祖先同类型保留每条来源。</summary>
public sealed record FolderTag(
    FolderTagKind Kind,
    string CanonicalValue,
    string SourceSegment)
{
    public string KindValue() => Kind switch
    {
        FolderTagKind.Publisher => "publisher",
        FolderTagKind.Engine => "engine",
        FolderTagKind.Category => "category",
        FolderTagKind.Collection => "collection",
        FolderTagKind.Batch => "batch",
        _ => "unknown",
    };
}

/// <summary>
/// 祖先路径分类继承（策划案 2.2，规则版本化）：
/// 完整方括号段 ^\[(.+)\]$ 才是容器；`[LunaSoft] 某游戏 V1.0` 不是。
/// 不修改路径中的 []，不做全角半角替换。
/// </summary>
public sealed class ClassificationRules
{
    public const int CurrentVersion = 1;

    private static readonly Regex BracketSegment = new(@"^\[(.+)\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BatchPattern = new(@"^\d{1,4}\.\d{1,2}\.\d{1,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 用户明确的厂商分类 + 厂商/作者分类建议（策划案 2.2 已知名单）。
    private static readonly HashSet<string> KnownPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANIM", "LunaSoft", "NTRMAN", "Clockup", "Uncomplicated",
    };

    private static readonly HashSet<string> KnownEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unity", "KRKR", "FLASH",
    };

    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "RPG",
    };

    private static readonly HashSet<string> KnownCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "13个时停小游戏", "对魔忍", "催眠校园", "MTool_Android", "AAA 已验证",
    };

    /// <summary>[toolNeed]：translationRequirement=Required 的祖先策略标记。</summary>
    public const string ToolNeedSegment = "toolNeed";

    public int Version { get; } = CurrentVersion;

    /// <summary>从祖先段（根→叶顺序）推断全部标签与翻译需求。</summary>
    public FolderClassification Classify(IEnumerable<string> ancestorSegments)
    {
        var tags = new List<FolderTag>();
        var toolNeedFrom = (string?)null;

        foreach (var segment in ancestorSegments)
        {
            var match = BracketSegment.Match(segment);
            if (!match.Success)
            {
                continue;
            }

            var inner = match.Groups[1].Value;
            if (string.Equals(inner, ToolNeedSegment, StringComparison.OrdinalIgnoreCase))
            {
                toolNeedFrom ??= segment;
                continue;
            }

            if (KnownPublishers.Contains(inner))
            {
                tags.Add(new FolderTag(FolderTagKind.Publisher, inner, segment));
                continue;
            }

            if (KnownEngines.Contains(inner))
            {
                tags.Add(new FolderTag(FolderTagKind.Engine, inner, segment));
                continue;
            }

            if (KnownCollections.Contains(inner))
            {
                tags.Add(new FolderTag(FolderTagKind.Collection, inner, segment));
                continue;
            }

            if (KnownCategories.Contains(inner))
            {
                tags.Add(new FolderTag(FolderTagKind.Category, inner, segment));
                continue;
            }

            if (BatchPattern.IsMatch(inner))
            {
                tags.Add(new FolderTag(FolderTagKind.Batch, inner, segment));
                continue;
            }

            // 未知 [xxx]：自定义分类，保留完整来源，不依名称数字推断游戏数。
            tags.Add(new FolderTag(FolderTagKind.Category, inner, segment));
        }

        return new FolderClassification(
            tags,
            RequiredByToolNeed: toolNeedFrom is not null,
            ToolNeedSourceSegment: toolNeedFrom,
            RulesVersion: Version);
    }
}

/// <summary>祖先分类结果；翻译需求只对最终 GameRoot/FileGame/NestedCandidate 生效。</summary>
public sealed record FolderClassification(
    IReadOnlyList<FolderTag> Tags,
    bool RequiredByToolNeed,
    string? ToolNeedSourceSegment,
    int RulesVersion);
