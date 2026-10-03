using System.Diagnostics;
using System.Text;

namespace GameLibrary.TauriBridge;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // WinExe 在 Windows 上经重定向管道启动时会继承系统 ANSI 代码页；Tauri 侧固定
        // 使用 UTF-8 JSON。两端编码不一致会把日文/中文路径和标题永久写成乱码。
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var server = new BridgeServer(input, Console.Out);
        var parentId = args.Length == 2 && args[0] == "--parent-pid" && int.TryParse(args[1], out var id) && id > 0 ? id : (int?)null;
        var parentWatch = parentId is { } pid ? WatchParentAsync(pid, cancellation) : Task.CompletedTask;
        try { await server.RunAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Cancel();
            await parentWatch;
            if (parentId is not null) await server.StopHostAsync();
        }
    }

    private static async Task WatchParentAsync(int id, CancellationTokenSource cancellation)
    {
        try
        {
            using var parent = Process.GetProcessById(id);
            await parent.WaitForExitAsync(cancellation.Token);
            cancellation.Cancel();
        }
        catch (ArgumentException) { cancellation.Cancel(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
