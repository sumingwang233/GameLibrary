using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GameLibrary.Mcp;

/// <summary>
/// 原生 MCP stdio 服务端（ADR-0007）：直接映射 HostConnection，不经 CLI 文本输出。
/// stdout 只承载 MCP 协议；日志全部走 stderr。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--stdio", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("用法：GameLibrary.Mcp.exe --stdio --data-dir <绝对路径>");
            return 2;
        }

        var dataDir = ExtractValue(args, "--data-dir");
        if (dataDir is null)
        {
            Console.Error.WriteLine("缺少 --data-dir 启动参数");
            return 2;
        }

        McpSession.DataDirectory = dataDir;
        var appVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "gamelibrary", Version = appVersion };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }

    private static string? ExtractValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
