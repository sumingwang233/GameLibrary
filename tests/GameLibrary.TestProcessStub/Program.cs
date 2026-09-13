namespace GameLibrary.TestProcessStub;

/// <summary>
/// 启动执行器测试专用进程桩：把收到的 argv/cwd 记录为 JSON 输出到 stdout，
/// 供 LA-xx 用例断言进程实际收到了什么参数。不执行任何业务逻辑。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
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
