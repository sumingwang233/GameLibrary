namespace GameLibrary.Domain.Tools;

/// <summary>
/// 适配证据分级（任务书 T07）：静态=只读检测到痕迹；生成物=用户盘上由工具生成的配方脚本；
/// CLI=官方候选协议（本地未验证）；实测=授权样本验证记录（IVerificationRunner 产出）。
/// </summary>
public enum ToolEvidenceKind
{
    Static,

    Generated,

    Cli,

    Measured,
}

/// <summary>
/// 适配器能力声明（补充规格 2.5）：每项能力 supported/unsupported/unknown，
/// 不能从 canLaunch 推出 canTranslate；无沙箱承诺——副作用如实声明。
/// </summary>
public enum ToolCapabilityState
{
    Supported,

    Unsupported,

    Unknown,
}

/// <summary>类型化进程步骤：由配方解析产出；执行语义与超时由验证记录确定，不凭空假设。</summary>
public sealed record RecipeProcessStep
{
    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>注入器等待退出；工具主程序驻留。等待语义必须经样本实测确认，解析只保留脚本结构。</summary>
    public required bool WaitForExit { get; init; }

    public required int Sequence { get; init; }
}

/// <summary>
/// MTool 本地生成配方（策划案 9.3-A）：来源脚本、摘要、类型化步骤、引用文件与断链。
/// 解析只确认文件内容，不代表运行验证（EvidenceKind=Generated，VerifiedAtUtc=null）。
/// </summary>
public sealed record MToolRecipe
{
    public required string SourcePath { get; init; }

    public required string ScriptSha256 { get; init; }

    public required IReadOnlyList<RecipeProcessStep> Steps { get; init; }

    /// <summary>配方引用的全部外部文件（注入器、hook、工具主程序）。</summary>
    public required IReadOnlyList<string> ReferencedFiles { get; init; }

    /// <summary>引用文件中不存在的路径：旧盘路径迁移/工具更新即 BrokenRecipe，可重映射但必须重新验证。</summary>
    public required IReadOnlyList<string> BrokenPaths { get; init; }

    public bool IsBroken => BrokenPaths.Count > 0;
}

/// <summary>MTool 适配能力声明（策划案 9）：固定如实，无沙箱假承诺。</summary>
public static class MToolCapability
{
    public const string ToolId = "mtool";

    public static IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["canLaunch"] = nameof(ToolCapabilityState.Supported),
        ["canRequestInjection"] = nameof(ToolCapabilityState.Supported),
        ["canDeploy"] = nameof(ToolCapabilityState.Unsupported),
        ["canRollback"] = nameof(ToolCapabilityState.Unsupported),
        ["mayUseNetwork"] = nameof(ToolCapabilityState.Unknown),
    };
}
