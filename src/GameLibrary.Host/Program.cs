using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var host = CreateHost(args);
        await host.RunAsync();
        return 0;
    }

    private static IHost CreateHost(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        return builder.Build();
    }
}
