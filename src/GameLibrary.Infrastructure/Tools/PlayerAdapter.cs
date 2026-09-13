using GameLibrary.Domain.Tools;

namespace GameLibrary.Infrastructure.Tools;

/// <summary>
/// 外部播放器适配（任务书 T09，策划案 11）：只读发现常见播放器安装（固定文件名+常见目录）、
/// 生成"播放器打开本地文件"参数模板（[目标路径] 是所有主流播放器的公开稳定行为）。
/// 不调用系统文件关联自动执行、不自动安装播放器；参数差异须逐播放器以用户样本验证后保存 ExternalPlayer 配置。
/// </summary>
public sealed class PlayerAdapter
{
    /// <summary>候选播放器：名称 + 常见安装子目录 + 可执行文件名。</summary>
    private static readonly (string Name, string SubDirectory, string Executable)[] Candidates =
    [
        ("PotPlayer", "PotPlayer", "PotPlayerMini64.exe"),
        ("PotPlayer", "PotPlayer", "PotPlayerMini.exe"),
        ("MPC-HC", "MPC-HC", "mpc-hc64.exe"),
        ("MPC-HC", "MPC-HC", "mpc-hc.exe"),
        ("MPC-BE", "MPC-BE", "mpc-be64.exe"),
        ("MPC-BE", "MPC-BE", "mpc-be.exe"),
        ("VLC", Path.Combine("VideoLAN", "VLC"), "vlc.exe"),
        ("HFlashPlayer", "HFlashPlayer", "HFlashPlayer.exe"),
    ];

    private static readonly string[] SearchRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
    ];

    /// <summary>只读发现已安装播放器；不做注册表写入、不启动进程。</summary>
    public IReadOnlyList<DiscoveredPlayer> Discover()
    {
        var result = new List<DiscoveredPlayer>();
        foreach (var searchRoot in SearchRoots)
        {
            if (!Directory.Exists(searchRoot))
            {
                continue;
            }

            foreach (var (name, subDirectory, executable) in Candidates)
            {
                var path = Path.Combine(searchRoot, subDirectory, executable);
                if (File.Exists(path) && result.All(p => !string.Equals(p.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new DiscoveredPlayer(name, path));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 启动模板：播放器 exe + [目标路径]。播放器参数差异（工作目录/退出行为）须以用户样本
    /// 验证后保存 ExternalPlayer 配置——模板只是公开稳定行为的起点，不是兼容性承诺。
    /// </summary>
    public RecipeProcessStep BuildLaunchTemplate(DiscoveredPlayer player, string targetPath)
    {
        if (!File.Exists(player.ExecutablePath))
        {
            throw new ArgumentException($"播放器可执行文件不存在：{player.ExecutablePath}");
        }

        return new RecipeProcessStep
        {
            Sequence = 0,
            ExecutablePath = player.ExecutablePath,
            Arguments = [targetPath],
            WorkingDirectory = Path.GetDirectoryName(player.ExecutablePath) ?? "",
            WaitForExit = false,
        };
    }
}

/// <summary>发现的播放器安装。</summary>
public sealed record DiscoveredPlayer(string Name, string ExecutablePath);
