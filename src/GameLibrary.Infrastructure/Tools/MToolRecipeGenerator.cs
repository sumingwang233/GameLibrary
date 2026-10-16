using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using GameLibrary.Domain.Tools;

namespace GameLibrary.Infrastructure.Tools;

public sealed record MToolPreparation(string State, string Reason, string? ScriptPath = null, string? BackupPath = null);

/// <summary>生成已确认结构的配方，不执行 MTool、游戏或 BAT。复杂缓存转换仍由 MTool 完成。</summary>
public static class MToolRecipeGenerator
{
    public static string ValidateToolRoot(string path)
    {
        var root = Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(path)! : path);
        SafePath(root);
        if (!File.Exists(Path.Combine(root, "MTool.exe")) || !File.Exists(Path.Combine(root, "loaders", "inject.exe")))
            throw new InvalidDataException("请选择包含 MTool.exe 和 loaders/inject.exe 的 MTool 目录");
        return root;
    }

    public static MToolPreparation Prepare(string gameRoot, string executablePath, string toolRoot)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        executablePath = Path.GetFullPath(executablePath);
        SafePath(gameRoot);
        SafePath(executablePath);
        if (!File.Exists(executablePath) || !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MTool 自动配方需要有效的 EXE 游戏入口");
        var scriptPath = Path.Combine(gameRoot, MToolAdapter.RecipeFileName);
        SafePath(scriptPath);
        var adapter = new MToolAdapter();
        var existing = adapter.Discover(gameRoot);
        if (existing.UnsupportedReason is not null) throw new InvalidDataException("现有配方包含不支持的语法，保留原文件，请在 MTool 中重新生成");
        if (existing.Recipe is { IsBroken: false })
            return new("ready", "MTool 配方已就绪；尚未验证游戏内翻译效果", scriptPath);
        toolRoot = ValidateToolRoot(toolRoot);
        MToolRecipe recipe;
        if (existing.Recipe is { } previous)
        {
            recipe = adapter.RemapToToolInstallation(previous, toolRoot);
            if (recipe.IsBroken)
                throw new InvalidDataException("配方中的游戏入口或专属缓存已缺失，请在 MTool 中重新选择游戏生成缓存；不会改用原文入口");
        }
        else
        {
            var directory = Path.GetDirectoryName(executablePath)!;
            // 不用目录名称或已存 engine 猜测 DLL；未知引擎也须有实际文件证据。
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(executablePath) + "_Data")))
                throw new InvalidDataException("Unity 游戏请使用 Unity 翻译插件配置");
            var loader = SelectLoader(executablePath);
            if (loader is null)
                throw new InvalidDataException("无法确认适用的 MTool 加载器；请先在 MTool 中选择游戏生成专属配方");
            var launcher = Path.Combine(toolRoot, "loaders", loader == BatRecipeParser.BakinLauncherFileName ? loader : "inject.exe");
            var arguments = loader == BatRecipeParser.BakinLauncherFileName
                ? new[] { executablePath } : new[] { executablePath, Path.Combine(toolRoot, "loaders", loader) };
            var steps = new RecipeProcessStep[]
            {
                new() { Sequence = 0, ExecutablePath = launcher, Arguments = arguments, WorkingDirectory = directory, WaitForExit = true },
                new() { Sequence = 1, ExecutablePath = Path.Combine(toolRoot, "MTool.exe"), Arguments = [toolRoot], WorkingDirectory = directory, WaitForExit = false },
            };
            recipe = BatRecipeParser.Parse(gameRoot, Serialize(steps)).Recipe!;
            if (recipe.ReferencedFiles.Any(path => !File.Exists(path)))
                throw new InvalidDataException("MTool 安装缺少对应加载器，请更新或重新选择 MTool 目录");
            if (arguments.Length == 2 && Machine(executablePath) != Machine(arguments[1]))
                throw new InvalidDataException("MTool 加载器与游戏架构不匹配，请在 MTool 中生成专属配方");
        }
        foreach (var step in recipe.Steps)
        {
            SafePath(step.WorkingDirectory);
            SafePath(step.ExecutablePath);
            foreach (var argument in step.Arguments) SafePath(argument);
            if (!Directory.Exists(step.WorkingDirectory)) throw new InvalidDataException("配方工作目录已缺失，请在 MTool 中重新生成");
        }
        var text = Serialize(recipe.Steps);
        if (BatRecipeParser.Parse(gameRoot, text).Recipe is not { } parsed || parsed.ReferencedFiles.Any(path => !File.Exists(path)))
            throw new InvalidDataException("生成的配方未通过安全解析，未写入游戏目录");
        var temporary = scriptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = existing.Recipe is null ? null : scriptPath + "." + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(text));
                file.Flush(flushToDisk: true);
            }
            SafePath(scriptPath);
            if (existing.Recipe is { } original)
            {
                // Host 串行门协调应用内写入；摘要检查防止覆写外部编辑，旧文件原子备份。
                if (adapter.Discover(gameRoot).Recipe?.ScriptSha256 != original.ScriptSha256)
                    throw new InvalidDataException("配方已被修改，请重新检查后重试");
                File.Replace(temporary, scriptPath, backup);
            }
            else File.Move(temporary, scriptPath); // 不覆盖同时出现的用户文件。
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new("ready", backup is null ? "已生成 MTool 启动配方；尚未验证游戏内翻译效果" : "已备份并修复 MTool 工具路径；尚未验证游戏内翻译效果", scriptPath, backup);
    }

    private static string? SelectLoader(string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        if (File.Exists(Path.Combine(directory, "nw.dll"))
            && new[] { "www", "" }.Any(prefix => File.Exists(Path.Combine(directory, prefix, "data", "System.json"))
                && new[] { "rpg_core.js", "rmmz_core.js" }.Any(core => File.Exists(Path.Combine(directory, prefix, "js", core)))))
        {
            var machine = Machine(executable);
            if (machine != Machine(Path.Combine(directory, "nw.dll"))) return null;
            return machine switch { System.Reflection.PortableExecutable.Machine.Amd64 => "mzHook.dll", System.Reflection.PortableExecutable.Machine.I386 => "mzHook32.dll", _ => null };
        }
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.FileDescription == "SRPG Studio Game Engine" && version.CompanyName == "SapphireSoft"
            && File.Exists(Path.Combine(directory, "data.dts")) && File.Exists(Path.Combine(directory, "system.dat"))) return "SRPGHook.dll";
        if (version.FileDescription == "Pixel Game Maker MV player" && version.CompanyName == "KADOKAWA CORPORATION") return "AgtkHook.dll";
        if (version.ProductName == "RPG Developer Bakin" && version.OriginalFilename == "bakinplayer_launcher.exe") return BatRecipeParser.BakinLauncherFileName;
        return null;
    }

    private static Machine Machine(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        return pe.PEHeaders.CoffHeader.Machine;
    }

    private static string Serialize(IEnumerable<RecipeProcessStep> steps) => "@chcp 65001\r\n@echo off\r\n" + string.Join("\r\n", steps.OrderBy(step => step.Sequence).Select(step =>
        $"@cd /d \"{step.WorkingDirectory}\"\r\n{(step.WaitForExit ? "" : "start \"\" ")}\"{step.ExecutablePath}\" " + string.Join(" ", step.Arguments.Select(argument => $"\"{argument}\"")))) + "\r\n";

    private static void SafePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(character => char.IsControl(character) || "\"%!^&|<>".Contains(character)))
            throw new InvalidDataException("路径包含 BAT 不支持的特殊字符，无法安全生成 MTool 配方");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MTool 配方路径不能经过符号链接或目录联接");
    }
}
