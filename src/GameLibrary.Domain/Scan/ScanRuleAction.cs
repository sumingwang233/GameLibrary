namespace GameLibrary.Domain.Scan;

/// <summary>规则动作；Continue 表示默认继续扫描（无规则命中）。</summary>
public enum ScanRuleAction
{
    /// <summary>排除该路径（记录 winningRuleId，可解释）。</summary>
    Exclude,

    /// <summary>包含：覆盖 SystemDefault 的排除/低优先级（不能绕过 SystemMandatory 排除）。</summary>
    Include,

    /// <summary>继续扫描但降低优先级（影响提示/排序，不改变覆盖事实）。</summary>
    LowPriority,

    /// <summary>扫描该分支但抑制候选展示；不能谎称该分支没有扫描。</summary>
    HideCandidate,
}

/// <summary>规则来源；SystemMandatory 的排除不可被任何 Include 绕过。</summary>
public enum ScanRuleSource
{
    SystemMandatory,

    SystemDefault,

    User,
}
