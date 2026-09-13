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
        var dataDir = ParseDataDir(args);
        if (dataDir is null)
        {
            Console.Error.WriteLine("用法：GameLibrary.Host --data-dir <绝对路径>");
            return ExitArgumentError;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        using var host = builder.Build();

        HostRuntime runtime;
        try
        {
            runtime = HostRuntime.Start(dataDir, host.Services.GetRequiredService<ILoggerFactory>());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
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

    private static string? ParseDataDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
