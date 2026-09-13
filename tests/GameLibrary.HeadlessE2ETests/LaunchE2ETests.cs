using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>
/// CLI 经真实宿主进程完成 Profile → launch execute → status 全流程（契约 7.2 骨架）。
/// 启动目标固定为 TestProcessStub；真实工具/游戏永不由测试或计划器自动启动。
/// </summary>
public sealed class LaunchE2ETests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private static string NewRunDir(string prefix) =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");

    [Fact]
    public async Task Cli_ProfilesCreate_LaunchExecute_Status_WorkThroughRealHost()
    {
        var runId = NewRunDir("e2e-launch");
        var dataDir = Path.Combine(runId, "data");
        var cwd = Path.Combine(runId, "cwd");
        Directory.CreateDirectory(cwd);
        int? hostPid = null;
        try
        {
            var gameId = $"game-{Guid.NewGuid():N}";

            // 路径包含边界：stub 所在目录注册为库根后，profiles.create 才接受该 exe。
            var rootAdd = await RunCliAsync(
                "roots", "add", "--root", AppContext.BaseDirectory,
                "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, rootAdd.ExitCode);

            var create = await RunCliAsync(
                "profiles", "create", "--game-id", gameId, "--exe", StubExe,
                "--arg", "--from-cli", "--cwd", cwd,
                "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, create.ExitCode);
            var createDoc = JsonDocument.Parse(create.StdOut);
            Assert.True(createDoc.RootElement.GetProperty("ok").GetBoolean());
            var profileId = createDoc.RootElement.GetProperty("data").GetProperty("profileId").GetString()!;

            var execute = await RunCliAsync(
                "launch", "execute", "--profile-id", profileId, "--idempotency-key", $"e2e-{gameId}",
                "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, execute.ExitCode);
            var executeDoc = JsonDocument.Parse(execute.StdOut);
            var attemptId = executeDoc.RootElement.GetProperty("data").GetProperty("attemptId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(attemptId));

            string? state = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var status = await RunCliAsync(
                    "launch", "status", "--attempt-id", attemptId, "--data-dir", dataDir, "--format", "json");
                Assert.Equal(0, status.ExitCode);
                state = JsonDocument.Parse(status.StdOut).RootElement
                    .GetProperty("data").GetProperty("state").GetString();
                if (state is "exited" or "processStartFailed")
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.Equal("exited", state);

            var history = await RunCliAsync(
                "launch", "history", "--game-id", gameId, "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, history.ExitCode);
            Assert.Equal(1, JsonDocument.Parse(history.StdOut).RootElement
                .GetProperty("data").GetProperty("total").GetInt32());

            hostPid = JsonDocument.Parse(
                (await RunCliAsync("host", "status", "--data-dir", dataDir, "--format", "json")).StdOut)
                .RootElement.GetProperty("data").GetProperty("processId").GetInt32();
        }
        finally
        {
            KillHost(hostPid);
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
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static void KillHost(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        try
        {
            using var host = Process.GetProcessById(pid.Value);
            host.Kill(entireProcessTree: true);
            host.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
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
