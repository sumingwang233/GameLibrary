using System.Text.Json;
using GameLibrary.Host.Observability;
using Xunit;

namespace GameLibrary.IntegrationTests.Observability;

public sealed class AuditLogWriterTests
{
    [Fact]
    public async Task ConcurrentAppendAndRotation_KeepCompleteUniqueRecordsAndRecentOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new AuditLogWriter(directory, maxFileBytes: 1000, maxFiles: 1000);
            await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(() => writer.Append(Record(i.ToString())))));
            var lines = writer.ReadRecentLines(1000);
            Assert.Equal(400, lines.Count);
            Assert.Equal(400, lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("requestId").GetString()).Distinct().Count());
            Assert.Equal(lines.TakeLast(7), writer.ReadRecentLines(7));
            Assert.Empty(writer.ReadRecentLines(0));
            Assert.True(writer.Describe().FileCount > 1);
            Assert.Equal(0, writer.FailureCount);
            var current = Directory.GetFiles(directory, "audit-*.jsonl").Order(StringComparer.Ordinal).Last();
            using var locked = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.None);
            var failingWriter = new AuditLogWriter(directory, maxFileBytes: long.MaxValue, maxFiles: 1000);
            failingWriter.Append(Record("blocked"));
            Assert.Equal(1, failingWriter.FailureCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AuditRecord Record(string id) => new()
    {
        TimestampUtc = DateTime.UtcNow,
        Level = "info",
        Component = "test",
        OperationId = "test",
        RequestId = id,
        Actor = "test",
        ResultCode = "completed",
    };
}
