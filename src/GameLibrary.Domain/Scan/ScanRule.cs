namespace GameLibrary.Domain.Scan;

/// <summary>扫描规则（补充规格 2.1）。OrderIndex 用于同优先级用户规则的确定排序。</summary>
public sealed record ScanRule
{
    public required string RuleId { get; init; }

    public required ScanRuleSource Source { get; init; }

    public required ScanRuleAction Action { get; init; }

    public required ScanRuleMatcher Matcher { get; init; }

    /// <summary>数值越大优先级越高；仅 User 规则参与该排序。</summary>
    public int Priority { get; init; }

    /// <summary>同优先级用户规则的声明顺序（小者胜）。</summary>
    public int OrderIndex { get; init; }

    public string Reason { get; init; } = "";

    public bool Enabled { get; init; } = true;

    public int Revision { get; init; }
}
