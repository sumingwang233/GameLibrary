using GameLibrary.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgumentError = 2;
    private const int ExitAlreadyRunning = 7;

    private static async Task<int> Main(string[] args)
    {
        var dataDir = ParseValue(args, "--data-dir");
        if (dataDir is null)
        {
            Console.Error.WriteLine("用法：GameLibrary.Host --data-dir <绝对路径> [--detach-stdio]");
            return ExitArgumentError;
        }

        var detachStdio = Array.IndexOf(args, "--detach-stdio") >= 0;
        if (detachStdio)
        {
            StdioDetach.CloseInheritedHandles();
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
        if (detachStdio)
        {
            // 后台模式无控制台；文件日志在可观测性任务（T24）落地。
            builder.Logging.ClearProviders();
        }
        else
        {
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

        using var host = builder.Build();

        HostRuntime runtime;
        try
        {
            runtime = HostRuntime.Start(dataDir, host.Services.GetRequiredService<ILoggerFactory>());
        }
        catch (InvalidOperationException ex)
        {
            if (!detachStdio)
            {
                Console.Error.WriteLine(ex.Message);
            }

            return ExitAlreadyRunning;
        }

        var logger = host.Services.GetRequiredService<ILogger<HostRuntime>>();
        logger.LogInformation(
            "宿主已启动：instance={InstanceId} dataDir={DataDir} pid={ProcessId}",
            runtime.Identity.InstanceId,
            runtime.DataDirectory,
            Environment.ProcessId);

        try
        {
            await host.RunAsync();
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        return ExitOk;
    }

    private static string? ParseValue(string[] args, string name)
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
