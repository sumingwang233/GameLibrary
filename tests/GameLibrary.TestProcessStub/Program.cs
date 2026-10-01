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
        if (args.Length == 4 && args[0] == "--restore-crash") return RestoreCrash(args);
        if (args.Contains("--spawn-child", StringComparer.Ordinal))
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--hold-ms");
            start.ArgumentList.Add("3000");
            using var child = System.Diagnostics.Process.Start(start);
        }
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
        var exitIndex = Array.IndexOf(args, "--exit-code");
        return exitIndex >= 0 && exitIndex + 1 < args.Length && int.TryParse(args[exitIndex + 1], out var exitCode) ? exitCode : 0;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RestoreCrash(string[] args)
    {
        var options = new GameLibrary.Infrastructure.Persistence.SqliteLibraryStoreOptions
        { AppVersion = "test", ApiVersion = "1" };
        var opened = GameLibrary.Infrastructure.Persistence.SqliteLibraryStore.TryOpenAsync(args[1],
            options, CancellationToken.None).GetAwaiter().GetResult();
        var manifest = GameLibrary.Infrastructure.Backups.BackupArchive.TryReadManifest(args[2])!;
        var storage = new GameLibrary.Infrastructure.Backups.LibraryRestoreStorage(args[1], args[2],
            manifest, opened.Store!, options, "crash-key", "digest", () => { }, _ => { }, "job-crash")
        {
            Checkpoint = phase => { if (phase == args[3]) Environment.Exit(86); },
        };
        GameLibrary.Application.Backups.BackupRestoreService.RestoreAsync(storage).GetAwaiter().GetResult();
        return 0;

    }
}
