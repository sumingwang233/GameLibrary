using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GameLibrary.HeadlessE2ETests;

/// <summary>
/// 真实 stdio MCP 客户端（契约第 12 节验收）：不依赖 C# SDK 客户端类型，
/// 直接按 JSON-RPC over stdio 与 GameLibrary.Mcp.exe 交互。
/// </summary>
public sealed class McpStdioTests
{
    private static string McpExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.Mcp.exe");

    [Fact]
    public async Task Stdio_Initialize_ListTools_CallTool_WorkEndToEnd()
    {
        Assert.True(File.Exists(McpExe), $"找不到 MCP 服务端：{McpExe}");
        var dataDir = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs",
            $"mcp-e2e-{Guid.NewGuid():N}", "data");
        int? hostPid = null;

        using var process = Process.Start(new ProcessStartInfo
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
        }) ?? throw new InvalidOperationException("MCP 服务端启动失败");

        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var initialize = await SendRequestAsync(process, 1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "gamelibrary-e2e-test", version = "1.0" },
            });
            Assert.Equal("gamelibrary", initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await SendNotificationAsync(process, "notifications/initialized");

            var tools = await SendRequestAsync(process, 2, "tools/list", new { });
            var toolNames = tools.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains("capabilities_get", toolNames);
            Assert.Contains("schema_get", toolNames);
            Assert.Contains("host_status", toolNames);
            Assert.Contains("games_list", toolNames);

            var call = await SendRequestAsync(process, 3, "tools/call", new
            {
                name = "host_status",
                arguments = new { },
            });
            var result = call.GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            var envelope = JsonDocument.Parse(text!);
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            hostPid = envelope.RootElement.GetProperty("data").GetProperty("processId").GetInt32();

            var badCall = await SendRequestAsync(process, 4, "tools/call", new
            {
                name = "schema_get",
                arguments = new { operationId = "nope.nope" },
            });
            var badResult = badCall.GetProperty("result");
            Assert.True(badResult.GetProperty("isError").GetBoolean());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

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
                    // 宿主已退出。
                }
            }

            var testRoot = Path.GetDirectoryName(dataDir)!;
            if (Directory.Exists(testRoot) && testRoot.StartsWith(
                    @"D:\Official\GameLibrary\artifacts\test-runs", StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task<JsonElement> SendRequestAsync(Process process, int id, string method, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await WriteLineAsync(process, $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{json}}}""");
        var line = await ReadResponseLineAsync(process);
        return JsonDocument.Parse(line).RootElement;
    }

    private static async Task SendNotificationAsync(Process process, string method)
    {
        await WriteLineAsync(process, $$"""{"jsonrpc":"2.0","method":"{{method}}"}""");
        await Task.Delay(100);
    }

    private static async Task WriteLineAsync(Process process, string line)
    {
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
    }

    /// <summary>读一行 JSON-RPC 响应，跳过空行；15 秒超时。</summary>
    private static async Task<string> ReadResponseLineAsync(Process process)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cts.Token).AsTask();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return line;
        }
    }
}
