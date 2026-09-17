using System.Runtime.InteropServices.ComTypes;

namespace GameLibrary.Infrastructure.Shell;

/// <summary>开机启动状态操作结果；失败携带原因，由调用方决定是否入错。</summary>
public sealed record StartupStateResult(bool Success, string? Error = null)
{
    public static StartupStateResult Ok() => new(true);

    public static StartupStateResult Fail(string error) => new(false, error);
}

/// <summary>
/// 可选开机启动（策划案 6.2/12）：在用户「启动」文件夹创建/删除指向 Desktop 的快捷方式。
/// 不写注册表、不装服务、不改环境变量（工程边界）；目录可注入以便测试与绿色部署。
/// </summary>
public sealed class StartupShortcutManager
{
    public const string ShortcutName = "GameLibrary.lnk";

    private readonly string _startupDirectory;

    public StartupShortcutManager(string? startupDirectory = null)
    {
        _startupDirectory = startupDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    }

    public string ShortcutPath => Path.Combine(_startupDirectory, ShortcutName);

    /// <summary>目标固定为当前安装的 GameLibrary.Desktop.exe；找不到即失败（不猜测其他路径）。</summary>
    public string? ResolveDesktopExe()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "GameLibrary.Desktop.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public bool IsEnabled() => File.Exists(ShortcutPath);

    /// <summary>
    /// 创建开机启动快捷方式。dataDirectory 非空时写入 --data-dir 参数
    /// （v1 审查意见：只设 EXE 与工作目录、不带数据目录参数，会让开机后进入错误状态）。
    /// </summary>
    public StartupStateResult Enable(string? dataDirectory = null)
    {
        var target = ResolveDesktopExe();
        if (target is null)
        {
            return StartupStateResult.Fail($"找不到 GameLibrary.Desktop.exe：{Path.Combine(AppContext.BaseDirectory, "GameLibrary.Desktop.exe")}");
        }

        try
        {
            Directory.CreateDirectory(_startupDirectory);
            var error = ShellLinkInterop.RunOnSta(() =>
            {
                try
                {
                    var link = ShellLinkInterop.CreateShellLink();
                    link.SetPath(target);
                    link.SetWorkingDirectory(AppContext.BaseDirectory);
                    if (!string.IsNullOrWhiteSpace(dataDirectory))
                    {
                        link.SetArguments($"--data-dir \"{dataDirectory}\"");
                    }

                    ((IPersistFile)link).Save(ShortcutPath, fRemember: false);
                    return (string?)null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });
            return error is null ? StartupStateResult.Ok() : StartupStateResult.Fail(error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StartupStateResult.Fail($"无法写入启动文件夹：{ex.Message}");
        }
    }

    public StartupStateResult Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            return StartupStateResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StartupStateResult.Fail($"无法移除启动快捷方式：{ex.Message}");
        }
    }
}
