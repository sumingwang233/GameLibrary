using System.Text;

namespace GameLibrary.TauriBridge;

internal static class Program
{
    private static async Task Main()
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

        await new BridgeServer(Console.In, Console.Out).RunAsync(cancellation.Token);
    }
}
