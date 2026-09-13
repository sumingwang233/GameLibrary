using System.Diagnostics;
using GameLibrary.HostClient;
using Xunit;


namespace GameLibrary.IntegrationTests;

/// <summary>
/// 真实 Host 进程端到端：冷启动 → 客户端连接握手 → host.status → 第二实例退出码 7 → 清理。
/// 冷启动竞争语义由互斥键保证：多客户端同时拉起时只有一个成为宿主。
/// </summary>
public sealed class HostProcessE2ETests
{
    private static string HostExePath =>
        Path.Combine(AppContext.BaseDirectory, "GameLibrary.Host.exe");

    [Fact]
    public async Task HostProcess_ServesClientsAndRejectsSecondInstance()
    {
        Assert.True(File.Exists(HostExePath), $"找不到宿主可执行文件：{HostExePath}");
        var testId = Guid.NewGuid().ToString("N");
        var dataDir = @$"D:\Official\GameLibrary\artifacts\test-runs\{testId}\data";

        using var host = StartHost(dataDir);
        try
        {
            await using var client = await WaitForHostAsync(dataDir);

            Assert.Equal("1", client.Handshake.ApiVersion);
            Assert.False(client.Handshake.LibraryInitialized);

            var envelope = await client.InvokeAsync(
                new Contracts.Ipc.IpcRequest { RequestId = "e2e-1", OperationId = "host.status" },
                CancellationToken.None);

            Assert.True(envelope.Ok);
            Assert.Equal(host.Id, envelope.Data.GetProperty("processId").GetInt32());

            var second = StartHost(dataDir);
            var exited = await WaitForExitAsync(second, TimeSpan.FromSeconds(15));
            Assert.True(exited, "第二个宿主实例应在互斥锁上快速退出");
            Assert.Equal(7, second.ExitCode);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await WaitForExitAsync(host, TimeSpan.FromSeconds(10));
            }

            var testRoot = @$"D:\Official\GameLibrary\artifacts\test-runs\{testId}";
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static Process StartHost(string dataDir)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = HostExePath,
            ArgumentList = { "--data-dir", dataDir },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        }) ?? throw new InvalidOperationException("宿主进程启动失败");
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<HostConnection> WaitForHostAsync(string dataDir)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await HostConnection.ConnectAsync(dataDir, "e2e-test", CancellationToken.None);
            }
            catch (HostClientException ex) when (ex.Code == HostClientErrorCodes.HostUnavailable)
            {
                await Task.Delay(200);
            }
        }

        throw new TimeoutException("宿主在 15 秒内未就绪");
    }
}
