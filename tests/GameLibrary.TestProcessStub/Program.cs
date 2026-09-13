namespace GameLibrary.TestProcessStub;

/// <summary>
/// 启动执行器测试专用进程桩：把收到的 argv/cwd 记录为 JSON 输出到 stdout，
/// 供 LA-xx 用例断言进程实际收到了什么参数。不执行任何业务逻辑。
/// `--hold-ms N` 在输出前保持进程存活 N 毫秒（供全入口互斥用例制造进行中的启动）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var holdIndex = Array.FindIndex(args, a => a.Equals("--hold-ms", StringComparison.OrdinalIgnoreCase));
        if (holdIndex >= 0 && holdIndex + 1 < args.Length && int.TryParse(args[holdIndex + 1], out var holdMs))
        {
            Thread.Sleep(Math.Clamp(holdMs, 0, 60_000));
        }

        var payload = new
        {
            argv = args,
            cwd = Environment.CurrentDirectory,
            processId = Environment.ProcessId,
            startTimeUtc = DateTime.UtcNow.ToString("O"),
        };
        Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
        return 0;
    }
}
