using System.Security.Cryptography;
using System.Text;

namespace GameLibrary.Domain.Tools;

/// <summary>
/// 最小 BAT 配方解析（策划案 9.3-A.2）：只支持本地 MTool 生成脚本的实际语法子集——
/// 可选 chcp/@echo off、`cd /d` 脚本目录或固定绝对目录、带引号的注入器+游戏+hook 或 BakinLauncher+游戏行、
/// 可选 `start ""` 启动 MTool.exe/nw.exe、尾部 echo。`%~dp0` 只解释为脚本目录。
/// 任何变量、CALL、FOR、IF、管道、重定向、复合命令、下载、文件写入 → Unsupported；
/// 不实现 BAT 解释器，也不直接执行原脚本。
/// </summary>
public static class BatRecipeParser
{
    public sealed record ParseResult
    {
        public MToolRecipe? Recipe { get; init; }

        public string? UnsupportedReason { get; init; }

        public bool IsSupported => Recipe is not null;
    }

    /// <summary>可识别的注入器文件名（策划案 9.2：Tool/loaders/inject.exe）。</summary>
    private const string InjectorMarker = "inject.exe";

    public const string BakinLauncherFileName = "BakinLauncher.exe";

    /// <summary>工具主程序文件名（不同脚本用 MTool.exe 或 nw.exe）。</summary>
    private static readonly string[] ToolMarkers = ["MTool.exe", "nw.exe"];

    /// <summary>hook DLL 特征（SRPGHook.dll、mzHook.dll 等）。</summary>
    private const string HookMarker = "hook";

    public static ParseResult Parse(string scriptDirectory, string scriptText)
    {
        var scriptRoot = NormalizeDir(scriptDirectory);
        var workingDirectory = scriptRoot;
        var steps = new List<RecipeProcessStep>();
        var referenced = new List<string>();
        var sequence = 0;

        var lines = scriptText.Replace("\r\n", "\n").Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            // MTool 生成器会给 chcp/cd/echo 加 BAT 的静默前缀；去掉前缀后仍走同一白名单。
            if (line.StartsWith('@')) line = line[1..].TrimStart();
            if (line.Length == 0 || IsEchoOff(line))
            {
                continue;
            }

            // 可接受的无副作用行：chcp、@echo off、echo、cd /d "%~dp0"。
            if (line.StartsWith("chcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("echo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("cd", StringComparison.OrdinalIgnoreCase))
            {
                // %~dp0 始终表示脚本目录，不受前面的绝对工作目录切换影响。
                if (line.Replace(" ", "").Equals(@"cd/d""%~dp0""", StringComparison.OrdinalIgnoreCase))
                {
                    workingDirectory = scriptRoot;
                    continue;
                }

                // MTool 的缓存配方会切到绝对工作目录；仅提取目录，不执行 cd 或其他 BAT 命令。
                if (line.StartsWith("cd /d ", StringComparison.OrdinalIgnoreCase))
                {
                    var target = line["cd /d ".Length..].Trim();
                    if (target.Length > 2 && target[0] == '"' && target[^1] == '"'
                        && target.Count(character => character == '"') == 2
                        && !target.Contains('%') && Path.IsPathFullyQualified(target[1..^1]))
                    {
                        workingDirectory = NormalizeDir(target[1..^1]);
                        continue;
                    }
                }

                return Unsupported($"不支持的 cd 语句：{line}");
            }

            // start 行只接受 `start "" "MTool.exe/nw.exe" args`（观察到的真实结构）；
            // 去掉 start 与空标题后按普通行继续解析。
            if (line.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsStartOfToolLine(line))
                {
                    return Unsupported($"start 行不符合工具主程序启动模式：{line}");
                }

                line = line["start ".Length..].TrimStart();
                if (line.StartsWith("\"\"", StringComparison.Ordinal))
                {
                    line = line[2..].TrimStart();
                }
            }

            // 复合命令/重定向/管道/控制流一律拒绝。
            if (line.Contains('|') || line.Contains('&') || line.Contains('>') || line.Contains('<')
                || line.StartsWith("call ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("set", StringComparison.OrdinalIgnoreCase))
            {
                return Unsupported($"不支持的脚本语法：{line}");
            }

            var expanded = ExpandDP0(line, scriptRoot);
            var quoted = ExtractQuotedPaths(expanded);
            if (quoted.Count == 0)
            {
                return Unsupported($"无法识别的语句：{line}");
            }

            var first = quoted[0];
            var fileName = Path.GetFileName(first.Trim('"').Trim());

            if (fileName.Equals(InjectorMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (quoted.Count != 3)
                {
                    return Unsupported($"注入器行参数数量不符（期望 游戏.exe + hook.dll）：{line}");
                }

                var gameExe = quoted[1];
                var hook = quoted[2];
                referenced.AddRange([first, gameExe, hook]);
                steps.Add(new RecipeProcessStep
                {
                    Sequence = sequence++,
                    ExecutablePath = first,
                    Arguments = [gameExe, hook],
                    WorkingDirectory = workingDirectory,
                    WaitForExit = true,
                });
                continue;
            }

            if (fileName.Equals(BakinLauncherFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (quoted.Count != 2)
                {
                    return Unsupported($"BakinLauncher 行参数数量不符（期望 游戏.exe）：{line}");
                }

                referenced.AddRange([first, quoted[1]]);
                steps.Add(new RecipeProcessStep
                {
                    Sequence = sequence++,
                    ExecutablePath = first,
                    Arguments = [quoted[1]],
                    WorkingDirectory = workingDirectory,
                    WaitForExit = true,
                });
                continue;
            }

            if (ToolMarkers.Any(m => fileName.Equals(m, StringComparison.OrdinalIgnoreCase)))
            {
                referenced.Add(first);
                // 参数 1 = Tool 目录的绝对路径（策划案 9.2 步骤 2）。
                var toolArgs = quoted.Count > 1 ? new[] { quoted[1] } : Array.Empty<string>();
                steps.Add(new RecipeProcessStep
                {
                    Sequence = sequence++,
                    ExecutablePath = first,
                    Arguments = toolArgs,
                    WorkingDirectory = workingDirectory,
                    WaitForExit = false,
                });
                continue;
            }

            return Unsupported($"未识别的可执行行（不是注入器或工具主程序）：{line}");
        }

        if (steps.Count == 0)
        {
            return Unsupported("脚本中没有可识别的注入器/工具启动步骤");
        }

        var sourcePath = Path.Combine(scriptRoot, "与工具一同启动.bat");
        return new ParseResult
        {
            Recipe = new MToolRecipe
            {
                SourcePath = sourcePath,
                ScriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptText))),
                Steps = steps,
                ReferencedFiles = referenced,
                // Domain 层不做 IO；断链判定由 Infrastructure 适配器按存在性回填。
                BrokenPaths = [],
            },
        };
    }

    private static bool IsEchoOff(string rest) =>
        rest.TrimStart().StartsWith("echo off", StringComparison.OrdinalIgnoreCase)
        || rest.TrimStart().StartsWith("echo on", StringComparison.OrdinalIgnoreCase);

    /// <summary>`start "" "tool.exe" args` 形式（首参空标题）属于工具主程序启动模式。</summary>
    private static bool IsStartOfToolLine(string line)
    {
        var rest = line["start ".Length..].TrimStart();
        var quoted = ExtractQuotedPaths(rest);
        if (quoted.Count >= 2)
        {
            var fileName = Path.GetFileName(quoted[1].Trim('"').Trim());
            return ToolMarkers.Any(m => fileName.Equals(m, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string ExpandDP0(string line, string scriptDirectory)
    {
        // %~dp0 带尾随反斜杠（bat 语义）；组合出路径时归一。
        var dp0 = scriptDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return line.Replace("%~dp0", dp0, StringComparison.OrdinalIgnoreCase)
            .Replace(@"\\", @"\");
    }

    private static List<string> ExtractQuotedPaths(string line)
    {
        var result = new List<string>();
        var index = 0;
        while (index < line.Length)
        {
            var start = line.IndexOf('"', index);
            if (start < 0)
            {
                break;
            }

            var end = line.IndexOf('"', start + 1);
            if (end < 0)
            {
                break;
            }

            result.Add(line[(start + 1)..end]);
            index = end + 1;
        }

        return result;
    }

    private static string NormalizeDir(string directory) =>
        Path.TrimEndingDirectorySeparator(directory);

    private static ParseResult Unsupported(string reason) => new() { UnsupportedReason = reason };
}
