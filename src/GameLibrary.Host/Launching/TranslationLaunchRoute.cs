using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Tools;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Tools;

namespace GameLibrary.Host.Launching;

/// <summary>自动翻译启动路由：先执行注入器，再启动常驻翻译工具。</summary>
public sealed record TranslationLaunchRoute(
    string ToolId,
    string SourcePath,
    IReadOnlyList<RecipeProcessStep> Steps);

public sealed record TranslationRouteResolution(
    bool IsRequired,
    bool SatisfiedByProfileBinding,
    TranslationLaunchRoute? Route,
    string? UnavailableReason);

/// <summary>
/// 从游戏目录中的受支持配方解析自动翻译路由。
/// 不执行 BAT；只执行解析器确认过的固定程序名和具体文件路径。
/// </summary>
public static class TranslationLaunchRouteResolver
{
    public static TranslationRouteResolution Resolve(GameCard game, LaunchProfile profile)
    {
        var inherited = game.TranslationInherited
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = ParseOverride(game.TranslationOverride);
        var required = (userOverride != TranslationRequirement.Auto ? userOverride : inherited)
            == TranslationRequirement.Required;

        if (!required)
        {
            return new(false, false, null, null);
        }

        // 保留旧契约：显式绑定的 Profile 由调用方负责提供工具语义。
        if (!string.IsNullOrWhiteSpace(profile.ToolId))
        {
            return new(true, true, null, null);
        }

        var gameRoot = Directory.Exists(game.RootPath)
            ? game.RootPath
            : Path.GetDirectoryName(game.RootPath);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return new(true, false, null, "游戏目录不存在，无法发现翻译配方");
        }

        var discovery = new MToolAdapter().Discover(gameRoot);
        if (discovery.Recipe is null)
        {
            return new(true, false, null, discovery.UnsupportedReason ?? discovery.Notice ?? "游戏目录中未发现可用 MTool 配方");
        }

        var steps = discovery.Recipe.Steps
            .OrderBy(step => step.Sequence)
            .Select(step => RewriteGameEntry(step, profile.ExecutablePath))
            .ToArray();
        var problem = ValidateSteps(steps, profile.ExecutablePath);
        if (problem is not null)
        {
            return new(true, false, null, problem);
        }

        return new(
            true,
            false,
            new TranslationLaunchRoute(MToolDiscovery.ToolId, discovery.Recipe.SourcePath, steps),
            null);
    }

    private static TranslationRequirement ParseOverride(string? value) =>
        value is not null && Enum.TryParse<TranslationRequirement>(value, ignoreCase: false, out var parsed)
            ? parsed
            : TranslationRequirement.Auto;

    private static RecipeProcessStep RewriteGameEntry(RecipeProcessStep step, string executablePath)
    {
        if (!Path.GetFileName(step.ExecutablePath).Equals("inject.exe", StringComparison.OrdinalIgnoreCase)
            || step.Arguments.Count == 0)
        {
            return step;
        }

        var arguments = step.Arguments.ToArray();
        arguments[0] = executablePath;
        return step with { Arguments = arguments };
    }

    private static string? ValidateSteps(IReadOnlyList<RecipeProcessStep> steps, string gameExecutablePath)
    {
        if (steps.Count == 0)
        {
            return "翻译配方没有可执行步骤";
        }

        var hasInjector = false;
        var hasTool = false;
        foreach (var step in steps)
        {
            var fileName = Path.GetFileName(step.ExecutablePath);
            var isInjector = fileName.Equals("inject.exe", StringComparison.OrdinalIgnoreCase);
            var isTool = fileName.Equals("MTool.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("nw.exe", StringComparison.OrdinalIgnoreCase);
            if (!isInjector && !isTool)
            {
                return $"翻译配方包含不受支持的程序：{fileName}";
            }

            if (!File.Exists(step.ExecutablePath))
            {
                return $"翻译程序不存在：{step.ExecutablePath}";
            }

            if (!Directory.Exists(step.WorkingDirectory))
            {
                return $"翻译程序工作目录不存在：{step.WorkingDirectory}";
            }

            if (isInjector)
            {
                hasInjector = true;
                if (step.Arguments.Count < 2
                    || !string.Equals(step.Arguments[0], gameExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(step.Arguments[0])
                    || !File.Exists(step.Arguments[1]))
                {
                    return "翻译注入器步骤未指向当前游戏入口或 Hook 文件缺失";
                }
            }
            else
            {
                hasTool = true;
                foreach (var argument in step.Arguments.Where(Path.IsPathFullyQualified))
                {
                    if (!File.Exists(argument) && !Directory.Exists(argument))
                    {
                        return $"翻译工具参数路径不存在：{argument}";
                    }
                }
            }
        }

        return !hasInjector
            ? "翻译配方缺少注入器步骤"
            : !hasTool
                ? "翻译配方缺少常驻翻译工具步骤"
                : null;
    }
}
