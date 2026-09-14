using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>T24-B：请求延迟/错误码/事件/作业指标经 diagnostics.status 暴露。</summary>
public sealed class MetricsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public MetricsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t24b-test",
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
    public async Task DiagnosticsStatus_ExposesRequestMetricsAfterTraffic()
    {
        // 产生成功与失败请求各至少一条。
        await InvokeAsync("games.list");
        await InvokeAsync("games.get", new { gameId = "game-missing" });
        await InvokeAsync("games.get", new { gameId = "game-missing" });

        var diagnostics = await InvokeAsync("diagnostics.status");

        Assert.True(diagnostics.Ok, diagnostics.Error?.Message);
        var metrics = diagnostics.Data.GetProperty("metrics");
        var requests = metrics.GetProperty("requests");
        Assert.True(requests.GetProperty("total").GetInt64() >= 3, "应统计到至少 3 条请求");
        Assert.True(requests.GetProperty("failed").GetInt64() >= 2, "应统计到至少 2 条失败请求");
        Assert.True(requests.GetProperty("maxDurationMs").GetInt64() >= 0);
        var errorsByCode = requests.GetProperty("errorsByCode");
        Assert.True(errorsByCode.TryGetProperty("NotFound", out var notFound) && notFound.GetInt64() >= 2);
        var byOperation = requests.GetProperty("byOperation");
        Assert.True(byOperation.TryGetProperty("games.get", out var gamesGet) && gamesGet.GetInt64() >= 2);
    }

    [Fact]
    public async Task DiagnosticsStatus_ExposesEventAndJobCounters()
    {
        // 直接触发一次事件发布（事件计数路径）。
        _fixture.State.Events.Publish("game.created", "game:metrics-probe", new { probe = true }, DateTime.UtcNow);
        var diagnostics = await InvokeAsync("diagnostics.status");

        var metrics = diagnostics.Data.GetProperty("metrics");
        Assert.True(metrics.GetProperty("events").GetProperty("published").GetInt64() >= 1);
        Assert.True(metrics.GetProperty("events").GetProperty("coalesced").GetInt64() >= 0);
        Assert.True(metrics.GetProperty("jobs").GetProperty("succeeded").GetInt64() >= 0);

        var eventStream = diagnostics.Data.GetProperty("eventStream");
        Assert.True(eventStream.GetProperty("occupiedSlots").GetInt64() >= 1);
        Assert.True(eventStream.GetProperty("overflowed").GetInt64() >= 0);
    }
}
