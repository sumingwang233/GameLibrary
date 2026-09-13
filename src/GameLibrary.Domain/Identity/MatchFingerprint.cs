namespace GameLibrary.Domain.Identity;

/// <summary>
/// 匹配指纹（ADR-0001 第四键）：用于提出“重新关联/相似副本”建议的证据组合。
/// 只是线索：可能碰撞、可能因工具/游戏更新失效；绝不作为身份。
/// 摘要计算由扫描层（T02/T03）填充，本类型只承载与比较。
/// </summary>
public sealed record MatchFingerprint
{
    /// <summary>指纹策略版本；指纹规则演进后旧指纹不可比较。</summary>
    public required int StrategyVersion { get; init; }

    public required IReadOnlyList<FingerprintEntry> Entries { get; init; }

    public sealed record FingerprintEntry
    {
        /// <summary>相对安装根的路径（比较键形式）。</summary>
        public required string RelativeKey { get; init; }

        public long? SizeBytes { get; init; }

        /// <summary>小文件全量或入口首尾各 64 KiB 的 SHA-256（十六进制小写）；未读取为 null。</summary>
        public string? Sha256 { get; init; }

        /// <summary>文件缺失等导致无法取摘要时为 null 的说明。</summary>
        public string? MissingReason { get; init; }
    }

    public bool IsComparableWith(MatchFingerprint other) =>
        StrategyVersion == other.StrategyVersion;

    /// <summary>深度比较（List 是引用相等，record 默认相等性不适用于条目集合）。</summary>
    public bool Equals(MatchFingerprint? other) =>
        other is not null
        && StrategyVersion == other.StrategyVersion
        && Entries.Count == other.Entries.Count
        && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() =>
        HashCode.Combine(StrategyVersion, Entries.Count);

    public double SimilarityWith(MatchFingerprint other)
    {
        if (!IsComparableWith(other))
        {
            return 0;
        }

        var mine = Entries.Where(e => e.Sha256 is not null)
            .ToDictionary(e => e.RelativeKey, e => e.Sha256, StringComparer.OrdinalIgnoreCase);
        var theirs = other.Entries.Where(e => e.Sha256 is not null)
            .ToDictionary(e => e.RelativeKey, e => e.Sha256, StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0 || theirs.Count == 0)
        {
            return 0;
        }

        var matched = mine.Count(pair => theirs.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        return (double)matched / Math.Max(mine.Count, theirs.Count);
    }
}
