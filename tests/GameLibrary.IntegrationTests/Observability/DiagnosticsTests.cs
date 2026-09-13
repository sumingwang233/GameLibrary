using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Observability;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Observability;

/// <summary>
/// T24-A 可观测性（经真实管道）：dispatcher 全请求审计、审计轮转/过期/脱敏、
/// diagnostics.status 队列指标、diagnostics.logs 脱敏读取。
/// </summary>
public sealed class DiagnosticsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public DiagnosticsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "diag-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters ?? new { });
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_WritesAuditRecordForEveryRequest()
    {
        var before = _fixture.State.AuditLog.ReadRecentLines(int.MaxValue).Count;

        var status = await InvokeAsync("diagnostics.status");
        Assert.True(status.Ok, status.Error?.Message);

        var lines = _fixture.State.AuditLog.ReadRecentLines(int.MaxValue);
        Assert.Equal(before + 1, lines.Count); // diagnostics.status 自身一条审计

        var record = JsonDocument.Parse(lines[^1]).RootElement;
        Assert.Equal("dispatcher", record.GetProperty("component").GetString());
        Assert.Equal("diagnostics.status", record.GetProperty("operationId").GetString());
        Assert.Equal("diag-test", record.GetProperty("actor").GetString());
        Assert.Equal("Completed", record.GetProperty("resultCode").GetString());
        Assert.True(record.GetProperty("durationMs").GetInt64() >= 0);
        Assert.True(DateTime.TryParse(record.GetProperty("timestampUtc").GetString(), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public async Task DiagnosticsStatus_ReturnsAuditStatsAndJobMetrics()
    {
        var status = await InvokeAsync("diagnostics.status");

        Assert.True(status.Ok, status.Error?.Message);
        Assert.True(status.Data.GetProperty("processId").GetInt32() > 0);
        Assert.True(status.Data.GetProperty("library").GetProperty("initialized").GetBoolean());
        var audit = status.Data.GetProperty("audit");
        Assert.True(audit.GetProperty("fileCount").GetInt32() >= 1);
        Assert.Equal(10L * 1024 * 1024, audit.GetProperty("maxFileBytes").GetInt64());
        Assert.Equal(90, audit.GetProperty("retentionDays").GetInt32());
        Assert.True(status.Data.GetProperty("jobs").GetProperty("activeCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task DiagnosticsLogs_ReturnsRecentSanitizedRecords()
    {
        // 触发一条带数据目录路径的失败审计（RootOffline），随后读取。
        var missingRoot = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"diag-missing-{Guid.NewGuid():N}");
        await InvokeAsync("scan.start", new { idempotencyKey = "diag-" + Guid.NewGuid().ToString("N"), root = missingRoot });

        var logs = await InvokeAsync("diagnostics.logs", new { limit = 1000 });
        Assert.True(logs.Ok, logs.Error?.Message);
        Assert.True(logs.Data.GetProperty("total").GetInt32() >= 2);

        // 失败审计在册，且完整数据目录路径不出现在日志原文中（脱敏为 {dataDir}）。
        Assert.Contains(logs.Data.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("operationId").GetString() == "scan.start"
                && r.GetProperty("resultCode").GetString() == "RootOffline");
        var raw = logs.Data.GetProperty("items").ToString();
        Assert.DoesNotContain(@"D:\Official\GameLibrary\artifacts\test-runs", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingRoot, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsLogs_LimitBoundsResultCount()
    {
        await InvokeAsync("diagnostics.status");
        await InvokeAsync("diagnostics.status");

        var logs = await InvokeAsync("diagnostics.logs", new { limit = 1 });
        Assert.True(logs.Ok);
        Assert.Equal(1, logs.Data.GetProperty("total").GetInt32());
    }

    [Fact]
    public void AuditLogWriter_RotatesBySizeAndPrunesExpiredFiles()
    {
        var dir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"audit-rot-{Guid.NewGuid():N}");
        try
        {
            var writer = new AuditLogWriter(dir, maxFileBytes: 400, maxFiles: 3, retentionDays: 30);
            for (var i = 0; i < 50; i++)
            {
                writer.Append(new AuditRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    Level = "information",
                    Component = "test",
                    OperationId = "test.op",
                    RequestId = $"req-{i}",
                    Actor = "tester",
                    ResultCode = "Completed",
                });
            }

            Assert.InRange(writer.Describe().FileCount, 2, 3);

            // 过期清理：把所有文件时间戳改老，再写一条触发清理（保留期 30 天）。
            foreach (var file in Directory.EnumerateFiles(dir, "audit-*.jsonl"))
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-31));
            }

            writer.Append(new AuditRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Level = "information",
                Component = "test",
                OperationId = "test.op",
                RequestId = "req-fresh",
                Actor = "tester",
                ResultCode = "Completed",
            });

            Assert.Equal(1, writer.Describe().FileCount);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void LogSanitizer_ReplacesKnownPrefixesOnly()
    {
        var dataDir = @"D:\Official\GameLibrary\artifacts\test-runs\sandbox";
        Assert.Equal("{dataDir}\\library.db", LogSanitizer.Sanitize(dataDir + "\\library.db", dataDir));
        Assert.Equal("{dataDir}", LogSanitizer.Sanitize(dataDir, dataDir));
        Assert.Equal("no known prefix here", LogSanitizer.Sanitize("no known prefix here", dataDir));
        Assert.DoesNotContain(dataDir, LogSanitizer.Sanitize(
            $"扫描根离线：{dataDir}\\tree 与 {dataDir}\\other", dataDir));
    }
}
