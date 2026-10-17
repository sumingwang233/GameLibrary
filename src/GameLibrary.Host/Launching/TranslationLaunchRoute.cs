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
    public static TranslationRouteResolution Resolve(GameCard game, LaunchProfile profile, bool untranslated = false)
    {
        var required = RequiresTranslation(game, untranslated);

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
            var reason = string.Equals(game.Engine, "unity", StringComparison.OrdinalIgnoreCase)
                ? "Unity 翻译插件尚未就绪；请在游戏详情重试配置并查看失败原因，配置完成后再启动游戏"
                : discovery.UnsupportedReason ?? discovery.Notice ?? "游戏目录中未发现可用 MTool 配方";
            return new(true, false, null, reason);
        }

        var batchProfile = Path.GetExtension(profile.ExecutablePath).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        if (batchProfile && (!string.Equals(Path.GetFullPath(profile.ExecutablePath), Path.GetFullPath(discovery.Recipe.SourcePath), StringComparison.OrdinalIgnoreCase)
            || profile.Arguments.Count > 0 || string.IsNullOrWhiteSpace(game.EntryPath) || !File.Exists(game.EntryPath)))
            return new(true, false, null, "BAT 翻译启动方式需要选择已生成的 MTool 配方，并先确认原始游戏入口");
        var gameEntry = batchProfile ? game.EntryPath! : profile.ExecutablePath;
        var steps = discovery.Recipe.Steps
            .OrderBy(step => step.Sequence)
            .Select(step => batchProfile ? step : RewriteGameEntry(step, gameEntry))
            .ToArray();
        var problem = ValidateSteps(steps, gameEntry);
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

    public static bool RequiresTranslation(GameCard game, bool untranslated = false)
    {
        var inherited = game.TranslationInherited || untranslated
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = ParseOverride(game.TranslationOverride);
        return (userOverride != TranslationRequirement.Auto ? userOverride : inherited)
            == TranslationRequirement.Required;
    }

    private static TranslationRequirement ParseOverride(string? value) =>
        value is not null && Enum.TryParse<TranslationRequirement>(value, ignoreCase: false, out var parsed)
            ? parsed
            : TranslationRequirement.Auto;

    private static (bool Available, string? Problem) InspectEmbeddedTranslator(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null || !File.Exists(executablePath)) return (false, null);
        try
        {
            var layout = Hosting.UnityTranslationInspection.Inspect(executablePath);
            if (layout.Reason is null && layout.Loader is "rei" or "bepinex") return (true, null);
            if (layout.Loader == "conflict") return (false, layout.Reason);
            if (layout.Reason is not null && Directory.Exists(layout.Data)) return (false, layout.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return (false, "无法安全验证 Unity 内置翻译加载器，请检查游戏目录"); }
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
            var loaderEnabled = false;
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
                    loaderEnabled = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
                }
            }
            if (!loaderEnabled) return (false, "无法确认 BepInEx 加载器已启用，请检查 doorstop_config.ini");
            return (true, null);
        }
        catch (IOException) { return (false, "无法读取内置翻译插件，请检查游戏目录权限"); }
        catch (UnauthorizedAccessException) { return (false, "无法读取内置翻译插件，请检查游戏目录权限"); }
    }

    private static RecipeProcessStep RewriteGameEntry(RecipeProcessStep step, string executablePath)
    {
        var fileName = Path.GetFileName(step.ExecutablePath);
        if (!(fileName.Equals("inject.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(BatRecipeParser.BakinLauncherFileName, StringComparison.OrdinalIgnoreCase))
            || step.Arguments.Count == 0)
        {
            return step;
        }

        // MTool 生成的入口可能是临时目录中的包装程序，不能换回未经处理的原始 EXE。
        if (IsGeneratedGameEntry(step)) return step;

        var arguments = step.Arguments.ToArray();
        arguments[0] = executablePath;
        return step with { Arguments = arguments };
    }

    private static bool IsGeneratedGameEntry(RecipeProcessStep step) =>
        Path.GetFileName(step.Arguments[0]).Equals("MTool_Game.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(step.WorkingDirectory))),
            "MTool_Game_Tmp", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateSteps(IReadOnlyList<RecipeProcessStep> steps, string gameExecutablePath)
    {
        if (steps.Count == 0)
        {
            return "翻译配方没有可执行步骤";
        }

        var hasGameLauncher = false;
        var hasTool = false;
        foreach (var step in steps)
        {
            var fileName = Path.GetFileName(step.ExecutablePath);
            var isInjector = fileName.Equals("inject.exe", StringComparison.OrdinalIgnoreCase);
            var isBakin = fileName.Equals(BatRecipeParser.BakinLauncherFileName, StringComparison.OrdinalIgnoreCase);
            var isTool = fileName.Equals("MTool.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("nw.exe", StringComparison.OrdinalIgnoreCase);
            if (!isInjector && !isBakin && !isTool)
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

            if (isInjector || isBakin)
            {
                hasGameLauncher = true;
                if (step.Arguments.Count != (isInjector ? 2 : 1))
                {
                    return "翻译配方的游戏加载器参数数量不正确";
                }
                if (!File.Exists(step.Arguments[0]))
                {
                    return $"MTool 配方中的游戏入口不存在：{step.Arguments[0]}；请在 MTool 中重新生成启动配方";
                }
                if (!(string.Equals(step.Arguments[0], gameExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || (IsGeneratedGameEntry(step) && Path.GetExtension(step.Arguments[0]).Equals(".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(
                            Path.GetDirectoryName(Path.GetFullPath(step.Arguments[0])),
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(step.WorkingDirectory)),
                            StringComparison.OrdinalIgnoreCase)))
                    || (isInjector && !File.Exists(step.Arguments[1])))
                {
                    return isInjector ? "翻译注入器步骤未指向当前游戏入口或 Hook 文件缺失"
                        : "BakinLauncher 步骤未指向当前游戏入口";
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

        return !hasGameLauncher
            ? "翻译配方缺少注入器或受支持的游戏加载器步骤"
            : !hasTool
                ? "翻译配方缺少常驻翻译工具步骤"
                : null;
    }
}
