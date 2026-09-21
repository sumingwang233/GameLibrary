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
    string? UnavailableReason,
    bool SatisfiedByEmbeddedPlugin = false);

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

        // 显式配置的外部工具优先，不受目录中已禁用插件的影响。
        if (!string.IsNullOrWhiteSpace(profile.ToolId))
        {
            return new(true, true, null, null);
        }

        // 注入式 Unity 插件随原始 EXE 加载，并不需要外部翻译启动器。
        var embedded = InspectEmbeddedTranslator(profile.ExecutablePath);
        if (embedded.Available)
        {
            return new(true, false, null, null, SatisfiedByEmbeddedPlugin: true);
        }
        if (embedded.Problem is not null)
        {
            return new(true, false, null, embedded.Problem);
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

    private static (bool Available, string? Problem) InspectEmbeddedTranslator(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null || !File.Exists(executablePath)) return (false, null);
        var bepinex = Path.Combine(directory, "BepInEx");
        var plugins = Path.Combine(bepinex, "plugins");
        if (!Directory.Exists(plugins)
            || !(File.Exists(Path.Combine(bepinex, "core", "BepInEx.dll"))
                || File.Exists(Path.Combine(bepinex, "core", "BepInEx.Core.dll")))
            || !(File.Exists(Path.Combine(directory, "winhttp.dll"))
                || File.Exists(Path.Combine(directory, "version.dll")))) return (false, null);
        try
        {
            var installed = Directory.EnumerateFiles(plugins, "XUnity.AutoTranslator.Plugin.BepIn*.dll", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).Any(path => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "XUnity.AutoTranslator.Plugin.Core.dll")));
            if (!installed) return (false, null);
            var config = Path.Combine(directory, "doorstop_config.ini");
            if (File.Exists(config))
            {
                if (new FileInfo(config).Length > 64 * 1024)
                    return (false, "doorstop_config.ini 过大，无法安全检查内置翻译加载器");
                var section = "";
                foreach (var line in File.ReadLines(config))
                {
                    var text = line.Trim();
                    if (text.StartsWith('[')) { section = text.Trim('[', ']').ToLowerInvariant(); continue; }
                    if (section is not ("general" or "unitydoorstop")) continue;
                    var pair = text.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || !pair[0].Equals("enabled", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = pair[1].Split(['#', ';'], 2)[0].Trim();
                    if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
                        return (false, "检测到 XUnity.AutoTranslator，但 doorstop_config.ini 中 enabled=false，BepInEx 加载器已禁用。请确认插件兼容后启用加载器，或选择不需要翻译");
                }
            }
            return (true, null);
        }
        catch (IOException) { return (false, "无法读取内置翻译插件，请检查游戏目录权限"); }
        catch (UnauthorizedAccessException) { return (false, "无法读取内置翻译插件，请检查游戏目录权限"); }
    }

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
