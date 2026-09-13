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

        var hostExe = Path.Combine(AppContext.BaseDirectory, "GameLibrary.Host.exe");
        if (!File.Exists(hostExe))
        {
            throw new HostClientException(
                HostClientErrorCodes.HostUnavailable,
                $"找不到宿主可执行文件：{hostExe}");
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
            WorkingDirectory = AppContext.BaseDirectory,
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
                    throw new HostClientException(
                        HostClientErrorCodes.HostUnavailable,
                        $"宿主进程提前退出（退出码 {process.ExitCode}），可能已有另一宿主持有该数据目录");
                }

                await Task.Delay(200, ct);
            }
        }

        throw new HostClientException(HostClientErrorCodes.HostUnavailable, "宿主在超时内未就绪");
    }
}
