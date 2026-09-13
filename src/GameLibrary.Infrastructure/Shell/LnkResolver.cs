using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GameLibrary.Infrastructure.Shell;

public enum LnkStatus
{
    /// <summary>目标存在、是文件且属于允许范围：可作为入口线索。</summary>
    ValidEntry,

    /// <summary>目标目录：仅导航线索，不作为入口。</summary>
    DirectoryTarget,

    /// <summary>目标不存在（失效快捷方式）。</summary>
    MissingTarget,

    /// <summary>指向自身或链回自身的快捷方式链。</summary>
    Cyclic,

    /// <summary>目标在授权范围之外。</summary>
    OutOfScopeTarget,

    /// <summary>目标/参数是 cmd/PowerShell/脚本：不自动执行，交用户处理。</summary>
    ShellCommand,

    /// <summary>读取/解析失败。</summary>
    Unreadable,
}

/// <summary>解析出的快捷方式内容。</summary>
public sealed record LnkInfo(string? TargetPath, string Arguments, string WorkingDirectory);

public sealed record LnkResolution(LnkStatus Status, LnkInfo? Info, string? Detail)
{
    /// <summary>只有 ValidEntry 可作为入口；其余状态由调用方按语义处理。</summary>
    public bool IsUsableEntry => Status == LnkStatus.ValidEntry;
}

/// <summary>
/// 只读 LNK 解析（策划案 5.3）：解析目标、参数、工作目录；
/// 目标存在、属于允许根且可验证时才作为入口；目录链接只是导航线索；
/// 带 Shell 命令的 LNK 不自动执行。链式/Cyclic 分支是对第三方工具写入的
/// 原始 .lnk 目标的防御——经 Windows Shell 接口创建的快捷方式在保存时
/// 即被解析为最终目标（实测：outer→inner→exe 存储为 outer→exe），
/// 无法用原生接口构造循环。
/// </summary>
public sealed class LnkResolver
{
    private const int MaxChainDepth = 16;
    private const int BufferSize = 1024;

    private readonly IReadOnlyList<GameLibrary.Domain.Paths.GamePath> _allowedRoots;

    public LnkResolver(IEnumerable<GameLibrary.Domain.Paths.GamePath> allowedRoots)
    {
        _allowedRoots = [.. allowedRoots];
    }

    public LnkResolution Resolve(string shortcutPath)
    {
        try
        {
            return ShellLinkInterop.RunOnSta(() => ResolveCore(shortcutPath));
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException)
        {
            return new LnkResolution(LnkStatus.Unreadable, null, ex.Message);
        }
    }

    private LnkResolution ResolveCore(string shortcutPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = shortcutPath;

        for (var depth = 0; depth <= MaxChainDepth; depth++)
        {
            var info = ReadLink(current);
            if (info.TargetPath is null || info.TargetPath.Length == 0)
            {
                return new LnkResolution(LnkStatus.MissingTarget, info, "快捷方式无目标");
            }

            var target = info.TargetPath;

            if (string.Equals(target, shortcutPath, StringComparison.OrdinalIgnoreCase))
            {
                return new LnkResolution(LnkStatus.Cyclic, info, "目标即快捷方式自身");
            }

            if (!visited.Add(target))
            {
                return new LnkResolution(LnkStatus.Cyclic, info, "快捷方式链回到已访问目标");
            }

            if (IsShellCommand(target, info.Arguments))
            {
                return new LnkResolution(LnkStatus.ShellCommand, info, "目标或参数为 Shell 命令/脚本，不自动执行");
            }

            var targetValidation = GameLibrary.Domain.Paths.GamePath.TryCreate(target);
            if (!targetValidation.IsValid || !targetValidation.Path!.Segments[^1].Contains('.', StringComparison.Ordinal))
            {
                // 无扩展名或路径非法：按目录/导航线索处理。
                if (Directory.Exists(target))
                {
                    return new LnkResolution(LnkStatus.DirectoryTarget, info, "目标为目录，仅作导航线索");
                }

                return new LnkResolution(LnkStatus.MissingTarget, info, "目标不存在");
            }

            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // 链式快捷方式：跟随下一层（深度受限，visited 防环）。
                current = target;
                continue;
            }

            if (!File.Exists(target))
            {
                return new LnkResolution(LnkStatus.MissingTarget, info, "目标文件不存在");
            }

            if (Directory.Exists(target))
            {
                return new LnkResolution(LnkStatus.DirectoryTarget, info, "目标为目录，仅作导航线索");
            }

            if (_allowedRoots.Count > 0
                && !_allowedRoots.Any(root => targetValidation.Path!.IsUnderOrEqualTo(root)))
            {
                return new LnkResolution(LnkStatus.OutOfScopeTarget, info, "目标不在授权扫描根内");
            }

            return new LnkResolution(LnkStatus.ValidEntry, info, null);
        }

        return new LnkResolution(LnkStatus.Cyclic, null, $"快捷方式链超过 {MaxChainDepth} 层");
    }

    private static LnkInfo ReadLink(string shortcutPath)
    {
        var link = ShellLinkInterop.CreateShellLink();
        var persistFile = (IPersistFile)link;
        persistFile.Load(shortcutPath, (int)STGM.READ);

        var pathBuilder = new System.Text.StringBuilder(BufferSize);
        // SLGP_RAWPATH(0x2)：返回存储的原始目标路径，不做 Shell 链式解析。
        link.GetPath(pathBuilder, BufferSize, out _, 0x2);
        var argsBuilder = new System.Text.StringBuilder(BufferSize);
        link.GetArguments(argsBuilder, BufferSize);
        var dirBuilder = new System.Text.StringBuilder(BufferSize);
        link.GetWorkingDirectory(dirBuilder, BufferSize);

        return new LnkInfo(
            pathBuilder.Length == 0 ? null : pathBuilder.ToString(),
            argsBuilder.ToString(),
            dirBuilder.ToString());
    }

    private static bool IsShellCommand(string target, string arguments)
    {
        var fileName = Path.GetFileName(target).ToLowerInvariant();
        if (fileName is "cmd.exe" or "powershell.exe" or "pwsh.exe" or "wt.exe" or "cscript.exe" or "wscript.exe" or "mshta.exe")
        {
            return true;
        }

        return arguments.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || arguments.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || arguments.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            || arguments.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase);
    }
}

internal enum STGM
{
    READ = 0,
}
