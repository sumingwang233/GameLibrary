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

    /// <summary>扫描路径包含边界（roots 白名单）：夹具都位于 test-runs 下，注册一次即可。</summary>
    private Task<Envelope<JsonElement>> EnsureRootAsync() =>
        InvokeAsync("roots.add", new { root = @"D:\Official\GameLibrary\artifacts\test-runs" });

    [Fact]
    public async Task ScanStart_AcceptsJobAndCompletesWithCoverage()
    {
        var root = CreateFixtureTree("scanop-full");
        await EnsureRootAsync();

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
    public async Task ScanStart_PathOutsideRegisteredRoots_IsDenied()
    {
        await EnsureRootAsync();

        // artifacts 与已注册的 artifacts\test-runs 平级：存在但不在白名单内。
        var envelope = await InvokeAsync("scan.start", new { root = @"D:\Official\GameLibrary\artifacts" });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, envelope.Error!.Code);
    }

    [Fact]
    public async Task ScanCancel_AfterCompletion_IsInvalidArgument()
    {
        var root = CreateFixtureTree("scanop-cancel");
        await EnsureRootAsync();
        var start = await InvokeAsync("scan.start", new { root });
        var jobId = start.JobId!;
        await WaitForJobAsync(jobId, "succeeded");

        var cancel = await InvokeAsync("scan.cancel", new { jobId });

        Assert.False(cancel.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, cancel.Error!.Code);
    }

    [Fact]
    public async Task CandidatesQuery_AfterScan_ReturnsDiscoveredCandidate()
    {
        var root = CreateFixtureTree("scanop-cands");
        await EnsureRootAsync();
        var start = await InvokeAsync("scan.start", new { root });
        var jobId = start.JobId!;
        await WaitForJobAsync(jobId, "succeeded");

        var list = await InvokeAsync("candidates.list", new { jobId });
        Assert.True(list.Ok, list.Error?.Message);
        Assert.True(list.Data.GetProperty("total").GetInt32() >= 1);
        var item = list.Data.GetProperty("items")[0];
        Assert.Equal("gameRoot", item.GetProperty("kind").GetString());
        Assert.Equal("observed", item.GetProperty("reviewState").GetString());
        Assert.Equal("kirikiri", item.GetProperty("engines")[0].GetProperty("engine").GetString());

        var candidateId = item.GetProperty("candidateId").GetString()!;
        var detail = await InvokeAsync("candidates.get", new { candidateId });
        Assert.True(detail.Ok, detail.Error?.Message);
        Assert.Equal("GameA", detail.Data.GetProperty("relativePath").GetString());
        Assert.True(detail.Data.GetProperty("entryCandidates").GetArrayLength() >= 1);

        var missing = await InvokeAsync("candidates.get", new { candidateId = "cand-missing" });
        Assert.False(missing.Ok);
        Assert.Equal(ErrorCodes.NotFound, missing.Error!.Code);

        var filtered = await InvokeAsync("candidates.list", new { jobId = "job-other" });
        Assert.True(filtered.Ok);
        Assert.Equal(0, filtered.Data.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ScanInspect_SinglePathRecognition_DoesNotCreateCandidates()
    {
        var root = CreateFixtureTree("scanop-inspect");
        await EnsureRootAsync();
        var gameDir = Path.Combine(root, "GameA");

        var inspect = await InvokeAsync("scan.inspect", new { path = gameDir });
        Assert.True(inspect.Ok, inspect.Error?.Message);
        Assert.True(inspect.Data.GetProperty("recognized").GetBoolean());
        Assert.False(inspect.Data.GetProperty("engineConflict").GetBoolean());
        Assert.Equal("kirikiri", inspect.Data.GetProperty("engines")[0].GetProperty("engine").GetString());
        Assert.True(inspect.Data.GetProperty("evidence").GetArrayLength() >= 1);

        var emptyDir = Path.Combine(root, "GameB");
        var notRecognized = await InvokeAsync("scan.inspect", new { path = emptyDir });
        Assert.True(notRecognized.Ok);
        Assert.False(notRecognized.Data.GetProperty("recognized").GetBoolean());

        var missing = await InvokeAsync(
            "scan.inspect",
            new { path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"inspect-missing-{Guid.NewGuid():N}") });
        Assert.False(missing.Ok);
        Assert.Equal(ErrorCodes.RootOffline, missing.Error!.Code);

        var invalid = await InvokeAsync("scan.inspect", new { path = @"D:\Game""s" });
        Assert.False(invalid.Ok);
        Assert.Equal(ErrorCodes.InvalidPath, invalid.Error!.Code);
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
