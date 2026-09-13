using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>CLI/MCP 经真实宿主进程完成扫描作业全流程（AI-02 骨架：无 GUI 原生调用）。</summary>
public sealed class ScanE2ETests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    private static string McpExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.Mcp.exe");

    private static string NewRunDir(string prefix) =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");

    private static string CreateTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "GameA"));
        File.WriteAllText(Path.Combine(root, "GameA", "Game.exe"), "x");
        File.WriteAllText(Path.Combine(root, "GameA", "data.xp3"), "x");
        return root;
    }

    [Fact]
    public async Task Cli_ScanStart_Status_Coverage_WorkThroughRealHost()
    {
        var runId = NewRunDir("e2e-cli-scan");
        var dataDir = Path.Combine(runId, "data");
        var scanRoot = CreateTree(Path.Combine(runId, "tree"));
        int? hostPid = null;
        try
        {
            var start = await RunCliAsync(
                "scan", "start", "--root", scanRoot, "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, start.ExitCode);
            var envelope = JsonDocument.Parse(start.StdOut);
            Assert.Equal("accepted", envelope.RootElement.GetProperty("status").GetString());
            var jobId = envelope.RootElement.GetProperty("jobId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobId));

            string? state = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var status = await RunCliAsync(
                    "jobs", "get", "--job-id", jobId!, "--data-dir", dataDir, "--format", "json");
                Assert.Equal(0, status.ExitCode);
                var statusDoc = JsonDocument.Parse(status.StdOut);
                state = statusDoc.RootElement.GetProperty("data").GetProperty("state").GetString();
                if (state is "succeeded" or "failed")
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.Equal("succeeded", state);

            var coverage = await RunCliAsync(
                "scan", "coverage", "--job-id", jobId!, "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, coverage.ExitCode);
            var coverageDoc = JsonDocument.Parse(coverage.StdOut);
            var coverageData = coverageDoc.RootElement.GetProperty("data").GetProperty("coverage");
            Assert.Equal("complete", coverageData.GetProperty("completion").GetString());
            Assert.Equal(2, coverageData.GetProperty("scannedDirectories").GetInt64());

            // 顺便从 host.status 拿到宿主 PID 以便清理。
            var hostStatus = await RunCliAsync("host", "status", "--data-dir", dataDir, "--format", "json");
            hostPid = JsonDocument.Parse(hostStatus.StdOut)
                .RootElement.GetProperty("data").GetProperty("processId").GetInt32();
        }
        finally
        {
            KillHost(hostPid);
            TryCleanup(runId);
        }
    }

    [Fact]
    public async Task Mcp_ScanStart_ReturnsAcceptedJob()
    {
        var runId = NewRunDir("e2e-mcp-scan");
        var dataDir = Path.Combine(runId, "data");
        var scanRoot = CreateTree(Path.Combine(runId, "tree"));
        int? hostPid = null;
        Process? mcpProcess = null;
        try
        {
            mcpProcess = StartMcp(dataDir);
            await SendRequestAsync(mcpProcess, 1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "scan-e2e", version = "1.0" },
            });
            await WriteLineAsync(mcpProcess, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var call = await SendRequestAsync(mcpProcess, 2, "tools/call", new
            {
                name = "scan_start",
                arguments = new { root = scanRoot },
            });
            var result = call.GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean());
            var envelope = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("accepted", envelope.RootElement.GetProperty("status").GetString());
            var jobId = envelope.RootElement.GetProperty("jobId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobId));

            // host_status 工具拿宿主 PID 用于清理。
            var hostCall = await SendRequestAsync(mcpProcess, 3, "tools/call", new
            {
                name = "host_status",
                arguments = new { },
            });
            var hostEnvelope = JsonDocument.Parse(
                hostCall.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            hostPid = hostEnvelope.RootElement.GetProperty("data").GetProperty("processId").GetInt32();
        }
        finally
        {
            KillHost(hostPid);
            if (mcpProcess is { HasExited: false })
            {
                mcpProcess.Kill(entireProcessTree: true);
                mcpProcess.WaitForExit(5000);
            }

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

    private static Process StartMcp(string dataDir)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = McpExe,
            ArgumentList = { "--stdio", "--data-dir", dataDir },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
    }

    private static async Task<JsonElement> SendRequestAsync(Process process, int id, string method, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await WriteLineAsync(process, $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{json}}}""");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cts.Token).AsTask();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return JsonDocument.Parse(line).RootElement;
        }
    }

    private static async Task WriteLineAsync(Process process, string line)
    {
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
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
