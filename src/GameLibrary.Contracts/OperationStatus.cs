namespace GameLibrary.Contracts;

/// <summary>
/// 操作结果状态。枚举值是公开契约，序列化为固定 camelCase 英文值；
/// 已发布值不得无版本静默重命名。
/// </summary>
public enum OperationStatus
{
    /// <summary>操作已完成；查询类操作与已结束的执行都使用该值。</summary>
    Completed,

    /// <summary>作业已受理并入队，不等于目标业务完成。</summary>
    Accepted,

    /// <summary>批次/覆盖存在缺口，ok=false；逐项结果与覆盖必须提供。</summary>
    Partial,

    /// <summary>需要用户或外部工具完成显式步骤后才能继续。</summary>
    NeedsUserAction,

    /// <summary>业务失败。</summary>
    Failed,

    /// <summary>结果不确定（例如进程已创建但收据未落盘即崩溃）。</summary>
    UnknownOutcome,
}
