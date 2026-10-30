using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>
/// 单 Bridge 进程、双 HostConnection 的真实宿主 E2E：events.wait 挂起时普通请求仍应返回，
/// 响应按 transportId 关联；关闭 stdin 后只停止自己的数据目录 Host。
/// </summary>
public sealed class TauriBridgeE2ETests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    [Fact]
    public async Task Bridge_LongPollDoesNotBlockOrdinaryRequest_AndCleansUpOnExit()
    {
        var runDir = Path.Combine(RepoRoot(), "artifacts", "test-runs", $"bridge-e2e-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(runDir, "data");
        using var parent = Process.Start(new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe"), "--hold-ms 60000")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "GameLibrary.TauriBridge.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(parent.Id.ToString());
        using var bridge = Process.Start(start)!;
        try
        {
            var init = await SendAsync(bridge, dataDir, "library.init", new { idempotencyKey = "init" });
            Assert.True(init.GetProperty("ok").GetBoolean());

            // 从当前游标开始长轮询，确保本次 events.wait 真的挂起而不是先读到历史事件。
            var baseline = await SendAsync(bridge, dataDir, "events.read", new { limit = 1 });
            var cursor = baseline.GetProperty("data").GetProperty("latestCursor").GetInt64();

            // 先用 0ms 等待预热 events lane 连接，再验证长时间挂起时普通请求不被阻塞。
            var warm = await SendAsync(bridge, dataDir, "events.wait",
                new { cursor, limit = 4096, timeoutMs = 0 });
            Assert.True(warm.GetProperty("ok").GetBoolean());

            var waitTransport = Guid.NewGuid().ToString("N");
            await SendFrameAsync(bridge, waitTransport, dataDir, "events.wait",
                new { cursor, limit = 4096, timeoutMs = 30000 });
            await Task.Delay(1000);

            var firstTransport = Guid.NewGuid().ToString("N");
            var firstRequest = await SendFrameAsync(bridge, firstTransport, dataDir, "host.status", new { });
            var first = await ReadFrameAsync(bridge, TimeSpan.FromSeconds(10));
            Assert.Equal(firstTransport, first.GetProperty("transportId").GetString());
            Assert.Equal(firstRequest, first.GetProperty("result").GetProperty("requestId").GetString());
            Assert.True(first.GetProperty("result").GetProperty("ok").GetBoolean());
            var hostPid = first.GetProperty("result").GetProperty("data").GetProperty("processId").GetInt32();

            var secondTransport = Guid.NewGuid().ToString("N");
            var secondRequest = await SendFrameAsync(bridge, secondTransport, dataDir, "host.status", new { });
            var second = await ReadFrameAsync(bridge, TimeSpan.FromSeconds(10));
            Assert.Equal(secondTransport, second.GetProperty("transportId").GetString());
            Assert.Equal(secondRequest, second.GetProperty("result").GetProperty("requestId").GetString());

            // A rejected directory must not replace the last successfully connected shutdown scope.
            var invalidTransport = Guid.NewGuid().ToString("N");
            await SendFrameAsync(bridge, invalidTransport, "relative", "host.status", new { });
            var invalid = await ReadFrameAsync(bridge, TimeSpan.FromSeconds(10));
            Assert.Equal(invalidTransport, invalid.GetProperty("transportId").GetString());
            Assert.True(invalid.TryGetProperty("transportError", out _));

            // stdin EOF：桥接必须取消挂起的长轮询、自行退出，并通过独立权限连接停掉自己的 Host。
            bridge.StandardInput.Close();
            using var bridgeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await bridge.WaitForExitAsync(bridgeDeadline.Token);
            Assert.Equal(0, bridge.ExitCode);

            try
            {
                using var host = Process.GetProcessById(hostPid);
                using var hostDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.WaitForExitAsync(hostDeadline.Token);
                Assert.Equal(0, host.ExitCode);
            }
            catch (ArgumentException)
            {
                // Host 已先行退出（快速时序）。
            }
        }
        finally
        {
            if (!bridge.HasExited) { bridge.Kill(); await bridge.WaitForExitAsync(); }
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            await RunCliAsync("host", "stop", "--data-dir", dataDir, "--format", "json", "--no-start");
            TryCleanup(runDir);
        }
    }

    [Fact]
    public async Task Bridge_TransportFailure_ReturnsTransportErrorFrame()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "GameLibrary.TauriBridge.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var bridge = Process.Start(start)!;
        try
        {
            var transportId = Guid.NewGuid().ToString("N");
            await SendFrameAsync(bridge, transportId, "relative", "host.status", new { });
            var frame = await ReadFrameAsync(bridge, TimeSpan.FromSeconds(10));
            Assert.Equal(transportId, frame.GetProperty("transportId").GetString());
            Assert.False(frame.TryGetProperty("result", out _));
            Assert.Equal("ProtocolError", frame.GetProperty("transportError").GetProperty("code").GetString());
        }
        finally
        {
            if (!bridge.HasExited)
            {
                bridge.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await bridge.WaitForExitAsync(deadline.Token);
            }
        }
    }

    private static async Task<string> SendFrameAsync(
        Process bridge,
        string transportId,
        string dataDir,
        string operationId,
        object parameters)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await bridge.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            transportId,
            request = new { dataDir, requestId, operationId, parameters },
        }));
        await bridge.StandardInput.FlushAsync();
        return requestId;
    }

    private static async Task<JsonElement> SendAsync(
        Process bridge,
        string dataDir,
        string operationId,
        object parameters)
    {
        var transportId = Guid.NewGuid().ToString("N");
        await SendFrameAsync(bridge, transportId, dataDir, operationId, parameters);
        var frame = await ReadFrameAsync(bridge, TimeSpan.FromSeconds(15));
        Assert.Equal(transportId, frame.GetProperty("transportId").GetString());
        return frame.GetProperty("result").Clone();
    }

    private static async Task<JsonElement> ReadFrameAsync(Process bridge, TimeSpan timeout)
    {
        var line = await bridge.StandardOutput.ReadLineAsync().WaitAsync(timeout);
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CliExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.False(directory is null, "未找到仓库根（向上遍历未命中 Directory.Build.props）");
        return directory!.FullName;
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
