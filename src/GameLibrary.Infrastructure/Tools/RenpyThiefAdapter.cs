using System.Security.Cryptography;
using System.Text;
using GameLibrary.Domain.Tools;

namespace GameLibrary.Infrastructure.Tools;

/// <summary>
/// RenpyThief 适配（任务书 T08，策划案 10）：仅实现保底 Guided 行为——只读发现安装、
/// 指纹、启动计划（RenpyThief.exe，无参数，工作目录=安装目录）。不猜测 CLI 协议、
/// 不虚构 --game/--translate 参数；GeneratedLauncher/CLI Adapter 是待验证分支。
/// </summary>
public sealed class RenpyThiefAdapter
{
    /// <summary>主入口与辅助启动器的固定相对结构（策划案 10.1 已核实内容）。</summary>
    public const string MainExecutable = "RenpyThief.exe";

    public const string LauncherRelativePath = @"trans\launcher\RenpyThiefLauncher.exe";

    /// <summary>签名特征文件（教程说明文件名含固定字样）。</summary>
    public const string TutorialMarkerFragment = "视频教程";

    /// <summary>发现 RenpyThief 安装：主程序存在即成立；指纹绑定关键文件。</summary>
    public RenpyThiefDiscovery Discover(string installRoot)
    {
        var root = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mainExe = Path.Combine(root, MainExecutable);
        if (!File.Exists(mainExe))
        {
            return new RenpyThiefDiscovery
            {
                Found = false,
                Capability = RenpyThiefCapability.Describe(),
                Notice = "未发现 RenpyThief.exe；只读探测，不启动任何进程",
            };
        }

        var keyFiles = new List<string> { mainExe };
        var launcher = Path.Combine(root, LauncherRelativePath);
        if (File.Exists(launcher))
        {
            keyFiles.Add(launcher);
        }

        foreach (var marker in Directory.EnumerateFiles(root)
                     .Where(f => Path.GetFileName(f).Contains(TutorialMarkerFragment, StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            keyFiles.Add(marker);
        }

        return new RenpyThiefDiscovery
        {
            Found = true,
            Fingerprint = Fingerprint(keyFiles),
            MainExecutablePath = mainExe,
            LauncherPath = File.Exists(launcher) ? launcher : null,
            GuidedPlan = new RecipeProcessStep
            {
                Sequence = 0,
                ExecutablePath = mainExe,
                Arguments = [],
                WorkingDirectory = root,
                WaitForExit = false,
            },
            Capability = RenpyThiefCapability.Describe(),
            Notice = "保底 Guided：仅启动工具主界面（无参数），状态将保持 AwaitingUserInTool，由用户拖入游戏完成配置；启动协议未验证，不提供 CLI 假承诺",
        };
    }

    /// <summary>工具指纹：关键文件（相对名+长度+最后写入时间）摘要；自动更新/换目录即失效。</summary>
    public static string Fingerprint(IReadOnlyList<string> keyFiles)
    {
        var builder = new StringBuilder();
        foreach (var file in keyFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            builder.Append(Path.GetFileName(file))
                .Append(':')
                .Append(info.Length)
                .Append(':')
                .Append(info.LastWriteTimeUtc.Ticks)
                .Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>tools.discover 的 RenpyThief 结果。</summary>
public sealed record RenpyThiefDiscovery
{
    public bool Found { get; init; }

    public string? Fingerprint { get; init; }

    public string? MainExecutablePath { get; init; }

    public string? LauncherPath { get; init; }

    public RecipeProcessStep? GuidedPlan { get; init; }

    public required IReadOnlyDictionary<string, string> Capability { get; init; }

    public string? Notice { get; init; }
}
