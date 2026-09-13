using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>
/// gamelibrary.exe 进程级契约测试：stdout 只有一个 JSON、退出码符合第 5 节、诊断走 stderr。
/// 测试数据仅用 artifacts/test-runs/&lt;guid&gt;/data。
/// </summary>
public sealed class CliProcessTests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "gamelibrary.exe");

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        Assert.True(File.Exists(CliExe), $"找不到 CLI：{CliExe}");
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("CLI 启动失败");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task SchemaGet_UnknownOperation_Exits2WithInvalidArgumentEnvelope()
    {
        var (code, stdout, stderr) = await RunCliAsync("schema", "get", "--operation", "nope.nope", "--format", "json");

        Assert.Equal(2, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr) || !stdout.Contains("用法"), "错误说明只能在 stderr");

        var envelope = JsonDocument.Parse(stdout);
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("InvalidArgument", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SchemaGet_KnownOperation_ReturnsCatalogEntryAndExit0()
    {
        var (code, stdout, _) = await RunCliAsync("schema", "get", "--operation", "games.update", "--format", "json");

        Assert.Equal(0, code);
        var envelope = JsonDocument.Parse(stdout);
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        var data = envelope.RootElement.GetProperty("data");
        Assert.Equal("games.update", data.GetProperty("operationId").GetString());
        Assert.Equal("games_update", data.GetProperty("mcpTool").GetString());
        Assert.False(data.GetProperty("available").GetBoolean());
        var cliWords = data.GetProperty("cli").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        Assert.Equal(new[] { "games", "update" }, cliWords);
    }

    [Fact]
    public async Task HostStatus_NoStart_HostDown_ReturnsRunningFalseExit0()
    {
        var dataDir = TestRunDirectory.Create("cli-no-start");

        var (code, stdout, _) = await RunCliAsync(
            "host", "status", "--no-start", "--data-dir", dataDir, "--format", "json");

        Assert.Equal(0, code);
        var envelope = JsonDocument.Parse(stdout);
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(envelope.RootElement.GetProperty("data").GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task CapabilitiesGet_HostDown_ReturnsStaticContractExit0()
    {
        var dataDir = TestRunDirectory.Create("cli-capabilities");

        var (code, stdout, _) = await RunCliAsync("capabilities", "get", "--data-dir", dataDir, "--format", "json");

        Assert.Equal(0, code);
        var envelope = JsonDocument.Parse(stdout);
        var data = envelope.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("hostConnected").GetBoolean());
        var available = data.GetProperty("availableOperations")
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("host.status", available);
        Assert.Contains("capabilities.get", available);
        Assert.DoesNotContain("games.update", available);
    }

    [Fact]
    public async Task HostStatus_AutoStart_ConnectsThenSecondCliSeesRunningHost()
    {
        var dataDir = TestRunDirectory.Create("cli-auto-start");
        int? hostPid = null;
        try
        {
            var (code, stdout, stderr) = await RunCliAsync("host", "status", "--data-dir", dataDir, "--format", "json");
            Assert.True(code == 0, $"exit={code} stdout={stdout} stderr={stderr}");
            var envelope = JsonDocument.Parse(stdout);
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean(), stdout);
            var data = envelope.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("libraryInitialized").GetBoolean());
            hostPid = data.GetProperty("processId").GetInt32();

            var (secondCode, secondOut, _) = await RunCliAsync(
                "host", "status", "--no-start", "--data-dir", dataDir, "--format", "json");
            Assert.Equal(0, secondCode);
            var second = JsonDocument.Parse(secondOut);
            Assert.True(second.RootElement.GetProperty("ok").GetBoolean(), secondOut);
            // --no-start 连上运行中的宿主时返回完整 host.status：同一实例、同一 PID。
            Assert.Equal(
                hostPid.Value,
                second.RootElement.GetProperty("data").GetProperty("processId").GetInt32());
        }
        finally
        {
            if (hostPid is not null)
            {
                try
                {
                    using var host = Process.GetProcessById(hostPid.Value);
                    host.Kill(entireProcessTree: true);
                    host.WaitForExit(5000);
                }
                catch (ArgumentException)
                {
                    // 进程已退出。
                }
            }

            TestRunDirectory.Delete(dataDir);
        }
    }

    [Fact]
    public async Task UnknownCommand_Exits2WithErrorOnStderr()
    {
        var (code, stdout, stderr) = await RunCliAsync("frobnicate", "list");

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("未知命令", stderr);
    }
}

internal static class TestRunDirectory
{
    private const string Root = @"D:\Official\GameLibrary\artifacts\test-runs";

    /// <summary>每个测试唯一 test-id 目录（任务书夹具规则），不接触日常 LocalData。</summary>
    public static string Create(string prefix) =>
        Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");

    public static void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
