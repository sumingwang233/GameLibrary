using System.Diagnostics;
using GameLibrary.Domain.Paths;
using GameLibrary.Domain.Scan;
using GameLibrary.Infrastructure.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Performance;

/// <summary>
/// T25 扫描性能基线（PERF-01/02 缩减规模版）：
/// 夹具 1,000 目录 × 10 文件 = 10,000 文件（全量 10 万文件版用同一生成器在 T30 前放大复核）。
/// 测量：扫描吞吐、工作集内存增量、取消停止延迟（P95）。
/// </summary>
public sealed class ScanPerformanceTests
{
    private static string GenerateFixture(string root, int directories, int filesPerDirectory)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < directories; i++)
        {
            var dir = Path.Combine(root, $"dir-{i:D5}");
            Directory.CreateDirectory(dir);
            for (var j = 0; j < filesPerDirectory; j++)
            {
                File.WriteAllText(Path.Combine(dir, $"file-{j:D3}.dat"), new string('x', 128));
            }
        }

        stopwatch.Stop();
        return $"{directories} dirs × {filesPerDirectory} files in {stopwatch.ElapsedMilliseconds} ms";
    }

    private static (long WorkingSetMb, TimeSpan Elapsed, long Dirs) MeasureScan(string root)
    {
        var process = Process.GetCurrentProcess();
        var memoryBefore = process.WorkingSet64;

        var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));
        var stopwatch = Stopwatch.StartNew();
        var coverage = walker.Walk(_ => { }, pause: null, CancellationToken.None);
        stopwatch.Stop();

        var memoryAfter = process.WorkingSet64;
        return (WorkingSetMb: (memoryAfter - memoryBefore) / 1024 / 1024, Elapsed: stopwatch.Elapsed, Dirs: (int)coverage.ScannedDirectories);
    }

    [Fact]
    public void Perf01_ThousandDirectories_ScanThroughputAndMemory()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"perf01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 冷扫（生成即扫）与热扫各一次：报告两者，回归阈值看热扫。
            var fixtureInfo = GenerateFixture(root, directories: 1_000, filesPerDirectory: 10);
            var cold = MeasureScan(root);
            var warm = MeasureScan(root);

            // 根目录本身计入 ScannedDirectories：1,000 子目录 + 根 = 1,001。
            Assert.Equal(1_001, cold.Dirs);
            Assert.Equal(1_001, warm.Dirs);
            // 补充规格 PERF-01：扫描额外内存目标 ≤256 MiB（此处规模按比例远小于上限）。
            Assert.True(cold.WorkingSetMb <= 256, $"冷扫内存增量 {cold.WorkingSetMb} MiB 超过 256 MiB 上限");

            var report = $"""
                PERF-01（缩减规模 1/10）夹具：{fixtureInfo}
                冷扫：{cold.Elapsed.TotalMilliseconds:F0} ms，工作集增量 {cold.WorkingSetMb} MiB
                热扫：{warm.Elapsed.TotalMilliseconds:F0} ms，工作集增量 {warm.WorkingSetMb} MiB
                吞吐（热扫）：{1_000 / Math.Max(warm.Elapsed.TotalSeconds, 0.001):F0} 目录/秒
                """;
            File.AppendAllText(@"D:\Official\GameLibrary\artifacts\perf\scan-perf.txt", report + Environment.NewLine);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public void Perf02_CancelDuringScan_StopsWithinTwoSecondsP95()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"perf02-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            GenerateFixture(root, directories: 500, filesPerDirectory: 5);
            var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));
            var samples = new List<double>();

            // PERF-02：重复取消采样，P95 ≤ 2 秒。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var cts = new CancellationTokenSource();
                var stopwatch = Stopwatch.StartNew();
                var cancelRequestedAt = TimeSpan.MinValue;

                var coverage = walker.Walk(
                    _ =>
                    {
                        if (cancelRequestedAt == TimeSpan.MinValue && stopwatch.ElapsedMilliseconds > 20)
                        {
                            cancelRequestedAt = stopwatch.Elapsed;
                            cts.Cancel();
                        }
                    },
                    pause: null,
                    cts.Token);

                var stopLatency = stopwatch.Elapsed - cancelRequestedAt;
                samples.Add(stopLatency.TotalMilliseconds);
                Assert.Equal(ScanCompletion.Cancelled, coverage.Completion);
            }

            samples.Sort();
            var p95 = samples[^1];
            Assert.True(p95 <= 2_000, $"取消停止延迟 P95 {p95:F0} ms 超过 2000 ms");
            File.AppendAllText(@"D:\Official\GameLibrary\artifacts\perf\scan-perf.txt",
                $"PERF-02 取消停止延迟样本：{string.Join(", ", samples.Select(s => $"{s:F0}"))} ms（P95={p95:F0} ms）{Environment.NewLine}");
        }
        finally
        {
            TryCleanup(root);
        }
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
