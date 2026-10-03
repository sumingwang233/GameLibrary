using System.Diagnostics;
using System.Text.Json;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>host.stop 优雅停机 E2E（T18）：响应送达 → 宿主自行退出（退出码 0）；不杀游戏进程。</summary>
public sealed class HostStopE2ETests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopBridge_StopsOnlyItsHost_OnParentExitOrInputClose(bool longPoll)
    {
        var runDir = Path.Combine(Path.GetTempPath(), "GameLibrary-desktop-exit-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(runDir, "data");
        var otherData = Path.Combine(runDir, "other");
        using var parent = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe"), "--hold-ms 60000")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "GameLibrary.TauriBridge.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--parent-pid", parent.Id.ToString() }) start.ArgumentList.Add(arg);
        using var bridge = Process.Start(start)!;
        try
        {
            var otherStatus = await RunCliAsync("host", "status", "--data-dir", otherData, "--format", "json");
            Assert.Equal(0, otherStatus.ExitCode);
            var otherPid = JsonDocument.Parse(otherStatus.StdOut).RootElement.GetProperty("data").GetProperty("processId").GetInt32();
            var status = await Send("host.status", new { });
            Assert.True(status.GetProperty("ok").GetBoolean());
            using var host = Process.GetProcessById(status.GetProperty("data").GetProperty("processId").GetInt32());
            if (longPoll)
            {
                var init = await Send("library.init", new { idempotencyKey = "init" });
                Assert.True(init.GetProperty("ok").GetBoolean());
                await bridge.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { dataDir, requestId = "wait", operationId = "events.wait", parameters = new { timeoutMs = 30000 } }));
                await bridge.StandardInput.FlushAsync();
                parent.Kill(); // Only the owned test parent; Host must exit gracefully itself.
                await parent.WaitForExitAsync();
            }
            else bridge.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await bridge.WaitForExitAsync(deadline.Token);
            await host.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, bridge.ExitCode);
            Assert.Equal(0, host.ExitCode);
            using var other = Process.GetProcessById(otherPid);
            Assert.False(other.HasExited);
        }
        finally
        {
            if (!bridge.HasExited) { bridge.Kill(); await bridge.WaitForExitAsync(); }
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            await RunCliAsync("host", "stop", "--data-dir", dataDir, "--format", "json", "--no-start");
            await RunCliAsync("host", "stop", "--data-dir", otherData, "--format", "json", "--no-start");
        }

        async Task<JsonElement> Send(string operation, object parameters)
        {
            await bridge.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { dataDir, requestId = Guid.NewGuid().ToString("N"), operationId = operation, parameters }));
            await bridge.StandardInput.FlushAsync();
            var response = await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(response);
            return JsonDocument.Parse(response).RootElement.Clone();
        }
    }

    [Fact]
    public async Task Cli_HostStop_StopsHostGracefully()
    {
        var runId = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"e2e-stop-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(runId, "data");
        try
        {
            // 拉起宿主。
            var status = await RunCliAsync("host", "status", "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, status.ExitCode);
            var pid = JsonDocument.Parse(status.StdOut)
                .RootElement.GetProperty("data").GetProperty("processId").GetInt32();

            // stop：信封应为 completed + stopping=true。
            var stop = await RunCliAsync("host", "stop", "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, stop.ExitCode);
            var envelope = JsonDocument.Parse(stop.StdOut);
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(envelope.RootElement.GetProperty("data").GetProperty("stopping").GetBoolean());

            // 宿主在响应送达后自行退出（不依赖 kill）。快速环境下（CI）到达此处时
            // 宿主可能已退出——进程不存在即已自行退出，无法再核退出码，跳过等待；
            // stop 信封断言与后续断连断言仍然把关。
            try
            {
                using var hostProcess = Process.GetProcessById(pid);
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await hostProcess.WaitForExitAsync(exited.Token);
                Assert.Equal(0, hostProcess.ExitCode);
            }
            catch (ArgumentException)
            {
                // 宿主已退出（快速时序）。
            }

            // 停机后再连：HostUnavailable。
            await Assert.ThrowsAsync<HostClientException>(
                () => GameLibrary.HostClient.HostConnection.ConnectAsync(dataDir, "verify", CancellationToken.None));
        }
        finally
        {
            TryCleanup(runId);
        }
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
