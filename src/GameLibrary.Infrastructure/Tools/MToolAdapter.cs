using GameLibrary.Domain.Tools;

namespace GameLibrary.Infrastructure.Tools;

/// <summary>
/// MTool 适配（任务书 T07，策划案 9）：只读发现与解析，不启动任何进程。
/// 证据分级如实：读到生成脚本 = Generated（未验证）；CLI 候选协议 = Cli（本地未验证）；
/// Measured 只能来自授权验证记录。旧路径断链 = BrokenRecipe，可重映射但验证归零。
/// 副作用按 MToolCapability 声明，无沙箱假承诺。
/// </summary>
public sealed class MToolAdapter
{
    /// <summary>策划案 9.2 的固定脚本名；只读取用户选定游戏根下的这个文件。</summary>
    public const string RecipeFileName = "与工具一同启动.bat";

    /// <summary>CLI 候选协议（官方论坛讨论）：必须逐本地版本实测后才可开放 VerifiedAutomatic。</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> CliCandidateArgumentForms =
    [
        ["{gameExe}"],
        ["--dp={gameExe}"],
    ];

    /// <summary>发现游戏根内的 MTool 生成配方；无脚本或语法超出支持范围时如实返回。</summary>
    public MToolDiscovery Discover(string gameRootPath)
    {
        var scriptPath = Path.Combine(gameRootPath, RecipeFileName);
        if (!File.Exists(scriptPath))
        {
            return MToolDiscovery.NoRecipe(MToolCapability.Describe());
        }

        string scriptText;
        try
        {
            scriptText = File.ReadAllText(scriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MToolDiscovery.Unsupported($"配方脚本不可读：{LogSanitize(ex.Message)}", MToolCapability.Describe());
        }

        var parse = BatRecipeParser.Parse(gameRootPath, scriptText);
        if (!parse.IsSupported || parse.Recipe is null)
        {
            return MToolDiscovery.Unsupported(parse.UnsupportedReason ?? "脚本语法不支持", MToolCapability.Describe());
        }

        var recipe = parse.Recipe with
        {
            SourcePath = scriptPath,
            BrokenPaths = [.. parse.Recipe.ReferencedFiles.Where(p => !File.Exists(p))],
        };

        return new MToolDiscovery
        {
            EvidenceKind = nameof(ToolEvidenceKind.Generated),
            Capability = MToolCapability.Describe(),
            Recipe = recipe,
            Notice = recipe.IsBroken
                ? "配方引用的文件存在缺失（BrokenRecipe）；可重映射到已登记工具安装，重映射后必须重新验证"
                : "配方内容已确认（Generated）；运行验证（Measured）未执行，不标已验证兼容",
        };
    }

    /// <summary>
    /// 旧路径修复（策划案 9.3-A.5）：把断链的注入器/hook/工具路径重映射到选定工具安装根下
    /// 的同名相对位置；重映射后验证状态归零（needsRevalidation），不静默继承已验证。
    /// </summary>
    public MToolRecipe RemapToToolInstallation(MToolRecipe recipe, string toolInstallationRoot)
    {
        if (!recipe.IsBroken)
        {
            return recipe;
        }

        var root = toolInstallationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var broken = recipe.BrokenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        string RemapOne(string path)
        {
            if (!broken.Contains(path))
            {
                return path;
            }

            // 取原路径中 loadings/.. 之后的相对结构：优先 loaders 段，其次文件名。
            var fileName = Path.GetFileName(path);
            var directoryName = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            var candidate = directoryName.Equals("loaders", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, "loaders", fileName)
                : Path.Combine(root, fileName);
            return candidate;
        }

        var remappedSteps = recipe.Steps
            .Select(step => step with
            {
                ExecutablePath = RemapOne(step.ExecutablePath),
                Arguments = [.. step.Arguments.Select(RemapOne)],
            })
            .ToArray();

        var remappedReferenced = recipe.ReferencedFiles.Select(RemapOne).ToArray();
        return recipe with
        {
            Steps = remappedSteps,
            ReferencedFiles = remappedReferenced,
            BrokenPaths = [.. remappedReferenced.Where(p => !File.Exists(p))],
        };
    }

    private static string LogSanitize(string message) =>
        message.Replace(Environment.NewLine, " ");
}

/// <summary>tools.discover 的结果（T07）：证据分级 + 能力声明 + 配方/不支持原因。</summary>
public sealed record MToolDiscovery
{
    public const string ToolId = MToolToolId.Value;

    public required string EvidenceKind { get; init; }

    public required IReadOnlyDictionary<string, string> Capability { get; init; }

    public MToolRecipe? Recipe { get; init; }

    public string? UnsupportedReason { get; init; }

    public string? Notice { get; init; }

    public static MToolDiscovery NoRecipe(IReadOnlyDictionary<string, string> capability) => new()
    {
        EvidenceKind = nameof(ToolEvidenceKind.Static),
        Capability = capability,
        Notice = "游戏根内未发现 MTool 生成配方脚本",
    };

    public static MToolDiscovery Unsupported(string reason, IReadOnlyDictionary<string, string> capability) => new()
    {
        EvidenceKind = nameof(ToolEvidenceKind.Generated),
        Capability = capability,
        UnsupportedReason = reason,
        Notice = "配方脚本存在但语法超出最小解析范围；不实现 BAT 解释器，不直接执行原脚本",
    };
}

/// <summary>工具 ID 常量（避免与 Domain 循环引用）。</summary>
public static class MToolToolId
{
    public const string Value = "mtool";
}
