using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>扫描操作经真实管道往返（进程内 PipeServer）：start→accepted、status→succeeded、coverage 计数。</summary>
public sealed class ScanOperationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ScanOperationTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "scan-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters);
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
    public async Task ScanStart_AcceptsJobAndCompletesWithCoverage()
    {
        var root = CreateFixtureTree("scanop-full");

        var start = await InvokeAsync("scan.start", new { root });
        Assert.True(start.Ok, start.Error?.Message);
        Assert.Equal(OperationStatus.Accepted, start.Status);
        Assert.NotNull(start.JobId);
        Assert.Equal("running", start.Data.GetProperty("state").GetString());

        var jobId = start.JobId!;
        await WaitForJobAsync(jobId, "succeeded");

        var status = await InvokeAsync("scan.status", new { jobId });
        Assert.True(status.Ok);
        Assert.Equal("succeeded", status.Data.GetProperty("state").GetString());

        var coverage = await InvokeAsync("scan.coverage", new { jobId });
        Assert.True(coverage.Ok);
        Assert.Equal("complete", coverage.Data.GetProperty("coverage").GetProperty("completion").GetString());
        Assert.True(coverage.Data.GetProperty("coverage").GetProperty("scannedDirectories").GetInt64() >= 2);
        Assert.True(coverage.Data.GetProperty("coverage").GetProperty("observedFileEntries").GetInt64() >= 3);

        var jobsGet = await InvokeAsync("jobs.get", new { jobId });
        Assert.True(jobsGet.Ok);
        Assert.Equal("scan", jobsGet.Data.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ScanStart_NonexistentRoot_FailsWithRootOffline()
    {
        var missing = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"scanop-missing-{Guid.NewGuid():N}");

        var envelope = await InvokeAsync("scan.start", new { root = missing });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.RootOffline, envelope.Error!.Code);
    }

    [Fact]
    public async Task ScanStart_InvalidPath_IsRejected()
    {
        var invalid = await InvokeAsync("scan.start", new { root = @"D:\Game""s" });
        Assert.False(invalid.Ok);
        Assert.Equal(ErrorCodes.InvalidPath, invalid.Error!.Code);

        var unc = await InvokeAsync("scan.start", new { root = @"\\server\share" });
        Assert.False(unc.Ok);
        Assert.Equal(ErrorCodes.UnsupportedPath, unc.Error!.Code);
    }

    [Fact]
    public async Task ScanOperations_UnknownJobId_IsNotFound()
    {
        var status = await InvokeAsync("scan.status", new { jobId = "job-missing" });
        Assert.False(status.Ok);
        Assert.Equal(ErrorCodes.NotFound, status.Error!.Code);

        var cancel = await InvokeAsync("scan.cancel", new { jobId = "job-missing" });
        Assert.False(cancel.Ok);
        Assert.Equal(ErrorCodes.NotFound, cancel.Error!.Code);
    }

    [Fact]
    public async Task ScanCancel_AfterCompletion_IsInvalidArgument()
    {
        var root = CreateFixtureTree("scanop-cancel");
        var start = await InvokeAsync("scan.start", new { root });
        var jobId = start.JobId!;
        await WaitForJobAsync(jobId, "succeeded");

        var cancel = await InvokeAsync("scan.cancel", new { jobId });

        Assert.False(cancel.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, cancel.Error!.Code);
    }

    private string CreateFixtureTree(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "GameA"));
        File.WriteAllText(Path.Combine(path, "GameA", "Game.exe"), "x");
        File.WriteAllText(Path.Combine(path, "GameA", "data.xp3"), "x");
        Directory.CreateDirectory(Path.Combine(path, "GameB"));
        File.WriteAllText(Path.Combine(path, "GameB", "Game.exe"), "x");
        return path;
    }

    private async Task WaitForJobAsync(string jobId, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync("jobs.get", new { jobId });
            var state = status.Data.GetProperty("state").GetString();
            if (state == expected)
            {
                return;
            }

            if (state == "failed")
            {
                throw new InvalidOperationException($"作业失败：{status.Data.GetProperty("error").GetString()}");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"作业 {jobId} 未在 15 秒内进入 {expected}");
    }
}
