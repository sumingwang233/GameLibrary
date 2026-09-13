namespace GameLibrary.Contracts;

/// <summary>结构化后续步骤；用于 NeedsUserAction/部分完成等场景，不充当授权凭证。</summary>
public sealed class NextAction
{
    /// <summary>建议调用的 operationId（例如 tools.register、verification.start）。</summary>
    public string OperationId { get; init; } = "";

    /// <summary>操作计划/待处理动作 ID；无则 null。</summary>
    public string? ActionId { get; init; }

    /// <summary>为什么需要该步骤。</summary>
    public string Reason { get; init; } = "";
}
