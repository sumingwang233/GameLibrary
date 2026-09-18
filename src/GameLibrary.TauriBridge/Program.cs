namespace GameLibrary.TauriBridge;

internal static class Program
{
    private static async Task Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };

        await new BridgeServer(Console.In, Console.Out).RunAsync(cancellation.Token);
    }
}
