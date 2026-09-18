using System.Diagnostics;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.HostClient;

/// <summary>
/// 连接引导层（契约 2.1）：按需拉起与 HostClient 同目录的固定宿主二进制。
/// 不搜索 PATH、不执行配置中的任意命令；冷启动竞争由宿主互斥键消解
/// （并发拉起时多余实例以退出码 7 自行退出，无害）。
/// </summary>
public static class HostProcessLauncher
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>宿主可执行文件的环境变量覆盖（开发/部署特殊布局用；不搜索 PATH）。</summary>
    public const string HostExeEnvVar = "GAMELIBRARY_HOST_EXE";

    /// <summary>
    /// 解析宿主可执行文件位置（契约 2.1"固定宿主二进制"的部署布局适配）：
    /// 1) 环境变量 GAMELIBRARY_HOST_EXE 显式覆盖；
    /// 2) 与调用方同目录（扁平绿色布局 / 开发单目录）；
    /// 3) 兄弟子目录 ..\GameLibrary.Host\（按组件分目录的发布布局）。
    /// 仍不搜索 PATH、不执行任意命令。
    /// </summary>
    private static string? ResolveHostExe()
    {
        var overrideValue = Environment.GetEnvironmentVariable(HostExeEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue) && File.Exists(overrideValue))
        {
            return Path.GetFullPath(overrideValue);
        }

        var baseDir = AppContext.BaseDirectory;
        string?[] candidates =
        [
            Path.Combine(baseDir, "GameLibrary.Host.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "GameLibrary.Host", "GameLibrary.Host.exe")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<HostConnection> EnsureStartedAsync(
        string dataDirectory,
        string? clientName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var resolved = DataDirectory.Resolve(dataDirectory);
        if (!resolved.IsValid)
        {
            throw new HostClientException(HostClientErrorCodes.ProtocolError, $"数据目录非法：{resolved.Error}");
        }

        try
        {
            return await HostConnection.ConnectAsync(resolved.CanonicalPath!, clientName, ct);
        }
        catch (HostClientException ex) when (ex.Code == HostClientErrorCodes.HostUnavailable)
        {
            // 宿主未运行，继续拉起。
        }

        var hostExe = ResolveHostExe();
        if (hostExe is null)
        {
            var baseDir = AppContext.BaseDirectory;
            var overrideValue = Environment.GetEnvironmentVariable(HostExeEnvVar);
            throw new HostClientException(
                HostClientErrorCodes.HostUnavailable,
                "找不到宿主可执行文件（GameLibrary.Host.exe）。已尝试：\n"
                + $"  1) {Path.Combine(baseDir, "GameLibrary.Host.exe")}\n"
                + $"  2) {Path.Combine(baseDir, "..", "GameLibrary.Host", "GameLibrary.Host.exe")}\n"
                + (overrideValue is null
                    ? "  3) 环境变量 GAMELIBRARY_HOST_EXE 未设置"
                    : $"  3) 环境变量 GAMELIBRARY_HOST_EXE = {overrideValue}（文件不存在）"));
        }

        // 用 ShellExecute 启动：该路径不做句柄继承，宿主不会拿到调用方（及其父进程）的
        // stdout 管道写端。若用 UseShellExecute=false + 重定向，孙进程仍会通过
        // bInheritHandles 继承调用方被捕获的输出管道，导致脚本捕获 CLI 输出时挂死等 EOF。
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = hostExe,
            ArgumentList = { "--data-dir", resolved.CanonicalPath!, "--detach-stdio" },
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(hostExe),
        }) ?? throw new HostClientException(HostClientErrorCodes.HostUnavailable, "宿主进程启动失败");

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await HostConnection.ConnectAsync(resolved.CanonicalPath!, clientName, ct);
            }
            catch (HostClientException ex) when (ex.Code == HostClientErrorCodes.HostUnavailable)
            {
                if (process.HasExited)
                {
                    // 0x8000808x/9x 区间 = .NET 宿主二进制启动失败（缺运行时/配置损坏），
                    // 与业务退出码 7（单实例互斥）区分开，给出对应的处理建议。
                    var code = process.ExitCode;
                    var hint = (code & 0xFFFF0000) == unchecked((int)0x80000000)
                        ? "宿主二进制无法启动（缺 .NET 运行时或文件不完整）。请使用自包含发布包"
                            + "（artifacts/dist/GameLibrary-Portable-win-x64-*.zip），或运行与其同目录的自包含 Host。"
                        : "可能已有另一宿主持有该数据目录";
                    throw new HostClientException(
                        HostClientErrorCodes.HostUnavailable,
                        $"宿主进程提前退出（退出码 {code} / 0x{code & 0xFFFFFFFFL:X8}）。{hint}");
                }

                await Task.Delay(200, ct);
            }
        }

        throw new HostClientException(HostClientErrorCodes.HostUnavailable, "宿主在超时内未就绪");
    }
}
