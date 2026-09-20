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
    /// <summary>CI 运行器性能/内存行为不稳定：预算断言仅本机执行；完成度断言始终生效。</summary>
    private static bool EnforceBudget => Environment.GetEnvironmentVariable("CI") != "true";

    private static void AppendPerfReport(string text)
    {
        var path = @"D:\Official\GameLibrary\artifacts\perf\scan-perf.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, text + Environment.NewLine);
    }

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
            if (EnforceBudget)
            {
                Assert.True(cold.WorkingSetMb <= 256, $"冷扫内存增量 {cold.WorkingSetMb} MiB 超过 256 MiB 上限");
            }

            var report = $"""
                PERF-01（缩减规模 1/10）夹具：{fixtureInfo}
                冷扫：{cold.Elapsed.TotalMilliseconds:F0} ms，工作集增量 {cold.WorkingSetMb} MiB
                热扫：{warm.Elapsed.TotalMilliseconds:F0} ms，工作集增量 {warm.WorkingSetMb} MiB
                吞吐（热扫）：{1_000 / Math.Max(warm.Elapsed.TotalSeconds, 0.001):F0} 目录/秒
                """;
            AppendPerfReport(report);
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
            var samples = new List<double>();

            // PERF-02：重复取消采样，P95 ≤ 2 秒。每次全新 walker——复用实例会从上次
            // 取消的续走状态开始，快速环境剩余目录可在取消阈值（20ms）前走完，
            // 使 cancelRequestedAt 保持 MinValue，差值算术溢出（CI 实测触发过）。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var walker = new DirectoryWalker(GamePath.Create(root), new ScanRuleSet([]));
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

                if (cancelRequestedAt == TimeSpan.MinValue)
                {
                    // 该次走查在取消阈值前完成：无延迟可测，正常完成即可，跳过采样。
                    Assert.Equal(ScanCompletion.Complete, coverage.Completion);
                    continue;
                }

                var stopLatency = stopwatch.Elapsed - cancelRequestedAt;
                samples.Add(stopLatency.TotalMilliseconds);
                Assert.Equal(ScanCompletion.Cancelled, coverage.Completion);
            }

            Assert.NotEmpty(samples);
            samples.Sort();
            var p95 = samples[^1];
            if (EnforceBudget)
            {
                Assert.True(p95 <= 2_000, $"取消停止延迟 P95 {p95:F0} ms 超过 2000 ms");
            }

            AppendPerfReport(
                $"PERF-02 取消停止延迟样本：{string.Join(", ", samples.Select(s => $"{s:F0}"))} ms（P95={p95:F0} ms）");
        }
        finally
        {
            TryCleanup(root);
        }
    }

    private static void TryCleanup(string path)
    {
        // 杀软/索引器瞬时锁住刚生成的 .dat 时 Directory.Delete 抛
        // UnauthorizedAccessException——重试一次后仍失败则放弃（产物在 gitignored
        // artifacts/test-runs，不判测试失败；性能断言已在 finally 之前完成）。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt == 0)
            {
                await2();
            }
            catch (UnauthorizedAccessException) when (attempt == 0)
            {
                await2();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static void await2() => Thread.Sleep(300);
    }
}
