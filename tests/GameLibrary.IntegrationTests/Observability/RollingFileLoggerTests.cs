using GameLibrary.Host.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameLibrary.IntegrationTests.Observability;

/// <summary>
/// R45 滚动文件日志：格式化写入、尺寸滚动上限、IO 失败静默降级（日志不得拖垮宿主）。
/// </summary>
public sealed class RollingFileLoggerTests
{
    private static string FreshLogDir()
    {
        var path = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs", $"rollinglog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void WritesFormattedMessageWithException()
    {
        var dir = FreshLogDir();
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir))
            {
                provider.CreateLogger("GameLibrary.Host.HostRuntime").LogInformation(
                    "宿主已启动：instance={InstanceId}", "test-instance");
                provider.CreateLogger("GameLibrary.Host.Ipc.PipeServer").LogError(
                    new InvalidOperationException("管道已断开"), "连接失败：{Client}", "cli");
                provider.CreateLogger("Any").LogDebug("低于 Information 不落盘");
            }

            var lines = File.ReadAllLines(Path.Combine(dir, "host.log"));
            Assert.Equal(4, lines.Length);
            Assert.Contains("INF [GameLibrary.Host.HostRuntime] 宿主已启动：instance=test-instance", lines[0]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z ", lines[0]);
            Assert.Contains("ERR [GameLibrary.Host.Ipc.PipeServer] 连接失败：cli", lines[1]);
            Assert.Contains("System.InvalidOperationException", lines[2]);
            Assert.Contains("管道已断开", lines[3]);
            Assert.DoesNotContain(lines, l => l.Contains("低于", StringComparison.Ordinal));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void RotatesBySizeAndCapsFileCount()
    {
        var dir = FreshLogDir();
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir, maxBytes: 600, maxFiles: 3))
            {
                var logger = provider.CreateLogger("Rotate");
                for (var i = 0; i < 40; i++)
                {
                    logger.LogInformation("条目 {Index:D3} padding-padding-padding-padding", i);
                }
            }

            // host.log + host.log.1 + host.log.2；更早的滚动代被删除，总量有界。
            var files = Directory.GetFiles(dir, "host.log*").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(3, files.Length);
            var totalBytes = files.Sum(f => new FileInfo(f).Length);
            Assert.True(totalBytes <= 600 * 4, $"滚动后总量 {totalBytes} 字节超出上限");
            var current = File.ReadAllLines(Path.Combine(dir, "host.log"));
            Assert.Contains(current, l => l.Contains("条目 039", StringComparison.Ordinal));
            Assert.DoesNotContain(current, l => l.Contains("条目 000", StringComparison.Ordinal));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void WriteFailure_DegradesSilentlyAndRecovers()
    {
        var dir = FreshLogDir();
        try
        {
            using var provider = new RollingFileLoggerProvider(dir);
            var logger = provider.CreateLogger("Degrade");
            logger.LogInformation("第一条");

            // 日志目录被同名文件占据：Directory.CreateDirectory 抛 IO 异常，
            // 静默降级不拖垮宿主，本条丢弃。
            Directory.Delete(dir, recursive: true);
            File.WriteAllText(dir, "占位文件");
            logger.LogInformation("丢失的一条");

            // 障碍清除后自动恢复续写。
            File.Delete(dir);
            logger.LogInformation("恢复后的一条");
            var lines = File.ReadAllLines(Path.Combine(dir, "host.log"));
            Assert.Contains(lines, l => l.Contains("恢复后的一条", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("丢失的一条", StringComparison.Ordinal));
        }
        finally
        {
            TryCleanup(dir);
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
