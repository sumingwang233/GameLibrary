using GameLibrary.Domain.Paths;

namespace GameLibrary.Domain.Scan;

public enum ScanRuleOutcome
{
    /// <summary>无规则命中，默认继续扫描。</summary>
    Continue,

    Exclude,

    Include,

    LowPriority,

    HideCandidate,
}

/// <summary>规则求值结果：winningRuleId 可解释“为什么没入库”；全部命中可在 inspect 中查询。</summary>
public sealed record ScanRuleDecision
{
    public required ScanRuleOutcome Outcome { get; init; }

    public string? WinningRuleId { get; init; }

    public required IReadOnlyList<string> MatchedRuleIds { get; init; }

    /// <summary>深度比较（IReadOnlyList 是引用相等，record 默认相等性不适用于命中列表）。</summary>
    public bool Equals(ScanRuleDecision? other) =>
        other is not null
        && Outcome == other.Outcome
        && string.Equals(WinningRuleId, other.WinningRuleId, StringComparison.Ordinal)
        && MatchedRuleIds.Count == other.MatchedRuleIds.Count
        && MatchedRuleIds.SequenceEqual(other.MatchedRuleIds, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Outcome, WinningRuleId ?? string.Empty, MatchedRuleIds.Count);
}

/// <summary>
/// 规则集求值（ADR-0002）：SystemMandatory 排除不可被 Include 绕过 →
/// 启用的 User 规则按 priority 降序、同优先级按 OrderIndex 升序 → SystemDefault → 默认继续。
/// 求值与规则注册顺序无关（检测器顺序变化不得改变同一快照结果）。
/// </summary>
public sealed class ScanRuleSet
{
    private readonly IReadOnlyList<ScanRule> _userRules;

    public ScanRuleSet(IEnumerable<ScanRule> rules)
    {
        var all = rules.ToArray();
        RuleSetRevision = all.Length == 0 ? 0 : all.Max(r => r.Revision);
        Mandatory = all
            .Where(r => r.Source == ScanRuleSource.SystemMandatory && r.Enabled)
            .ToArray();
        _userRules = all
            .Where(r => r.Source == ScanRuleSource.User && r.Enabled)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.OrderIndex)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToArray();
        Defaults = all
            .Where(r => r.Source == ScanRuleSource.SystemDefault && r.Enabled)
            .OrderBy(r => r.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<ScanRule> Mandatory { get; }

    public IReadOnlyList<ScanRule> Defaults { get; }

    public int RuleSetRevision { get; }

    public ScanRuleDecision Decide(GamePath path)
    {
        List<string> matched = [];

        foreach (var rule in Mandatory)
        {
            if (rule.Matcher.Matches(path))
            {
                matched.Add(rule.RuleId);
            }
        }

        // SystemMandatory 排除优先且不可绕过。
        var mandatoryExclude = Mandatory.FirstOrDefault(r => r.Action == ScanRuleAction.Exclude && matched.Contains(r.RuleId));
        if (mandatoryExclude is not null)
        {
            return new ScanRuleDecision
            {
                Outcome = ScanRuleOutcome.Exclude,
                WinningRuleId = mandatoryExclude.RuleId,
                MatchedRuleIds = matched,
            };
        }

        foreach (var rule in _userRules)
        {
            if (rule.Matcher.Matches(path))
            {
                matched.Add(rule.RuleId);
                return Decision(rule, matched);
            }
        }

        foreach (var rule in Defaults)
        {
            if (rule.Matcher.Matches(path))
            {
                matched.Add(rule.RuleId);
                return Decision(rule, matched);
            }
        }

        return new ScanRuleDecision
        {
            Outcome = ScanRuleOutcome.Continue,
            WinningRuleId = null,
            MatchedRuleIds = matched,
        };
    }

    private static ScanRuleDecision Decision(ScanRule rule, List<string> matched) =>
        new()
        {
            Outcome = rule.Action switch
            {
                ScanRuleAction.Exclude => ScanRuleOutcome.Exclude,
                ScanRuleAction.Include => ScanRuleOutcome.Include,
                ScanRuleAction.LowPriority => ScanRuleOutcome.LowPriority,
                ScanRuleAction.HideCandidate => ScanRuleOutcome.HideCandidate,
                _ => ScanRuleOutcome.Continue,
            },
            WinningRuleId = rule.RuleId,
            MatchedRuleIds = matched,
        };
}
