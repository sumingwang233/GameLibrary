using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

/// <summary>
/// 把持久化 ignore 规则映射为遍历层排除规则（R36 接线，补充规格 2.1）：
/// ExactPath → 精确路径排除；Subtree → 分段子树排除（含子树根自身）；
/// ConfirmedIdentity 按 gameId 绑定候选身份，遍历层无 gameId 可求值，仍由落库抑制处理。
/// OrderIndex 沿用存储顺序（created_utc, ignore_id）保证同优先级结果确定。
/// </summary>
public static class ScanIgnoreRuleSet
{
    /// <summary>库未初始化时无规则可加载，等价空规则集（保持扫描可用）。</summary>
    public static ScanRuleSet FromStore(SqliteLibraryStore? store) =>
        store is null ? new ScanRuleSet([]) : FromIgnoreRules(store.ListIgnoreRules());

    public static ScanRuleSet FromIgnoreRules(IReadOnlyList<IgnoreRule> rules)
    {
        var order = 0;
        var scanRules = new List<ScanRule>();
        foreach (var rule in rules)
        {
            if (rule.Path is null || rule.Scope is not ("ExactPath" or "Subtree"))
            {
                continue;
            }

            // 创建入口已校验路径位于注册根内；此处仍走规范化，使尾分隔符、
            // 相对段与遍历产生的 ComparisonKey 一致。不可求值的规则跳过不阻断扫描。
            var validation = GamePath.TryCreate(rule.Path);
            if (!validation.IsValid)
            {
                continue;
            }

            scanRules.Add(new ScanRule
            {
                RuleId = rule.IgnoreId,
                Source = ScanRuleSource.User,
                Action = ScanRuleAction.Exclude,
                Matcher = rule.Scope == "Subtree"
                    ? new SubtreeMatcher(validation.Path!.PhysicalPath)
                    : new ExactPathMatcher(validation.Path!.ComparisonKey),
                Reason = rule.Reason ?? "",
                OrderIndex = order++,
                Revision = rule.Revision,
            });
        }

        return new ScanRuleSet(scanRules);
    }
}
