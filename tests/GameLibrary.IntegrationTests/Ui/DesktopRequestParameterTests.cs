using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Desktop;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Ui;

public sealed class DesktopRequestParameterTests
{
    [Fact]
    public void ScanProgress_FormatsLiveCountersWithoutFakePercentage()
    {
        var coverage = JsonSerializer.SerializeToElement(new
        {
            phase = "scanning",
            scannedDirectories = 123,
            observedFileEntries = 456,
            candidatesFound = 7,
            currentPath = "Games/Example",
        });

        var text = DesktopScanProgress.Format(coverage, rootIndex: 1, rootCount: 2);

        Assert.Contains("123 个目录", text, StringComparison.Ordinal);
        Assert.Contains("456 个文件", text, StringComparison.Ordinal);
        Assert.Contains("识别 7 个候选（含已入库）", text, StringComparison.Ordinal);
        Assert.Contains("Games/Example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scan.start")]
    [InlineData("scan.cancel")]
    [InlineData("roots.add")]
    public void Prepare_WriteOperationWithoutKey_AddsIdempotencyKey(string operationId)
    {
        var parameters = DesktopRequestParameters.Prepare(operationId, new { root = @"C:\fixture" });

        var key = parameters.GetProperty("idempotencyKey").GetString();
        Assert.StartsWith($"desktop-{operationId}-", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ExplicitIdempotencyKey_PreservesIt()
    {
        var parameters = DesktopRequestParameters.Prepare(
            "scan.start",
            new { idempotencyKey = "desktop-scan-stable", root = @"C:\fixture" });

        Assert.Equal("desktop-scan-stable", parameters.GetProperty("idempotencyKey").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Prepare_BlankIdempotencyKey_ReplacesIt(string invalidKey)
    {
        var parameters = DesktopRequestParameters.Prepare(
            "scan.start",
            new { idempotencyKey = invalidKey, root = @"C:\fixture" });

        var key = parameters.GetProperty("idempotencyKey").GetString();
        Assert.StartsWith("desktop-scan.start-", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_NonStringIdempotencyKey_ReplacesIt()
    {
        var parameters = DesktopRequestParameters.Prepare(
            "scan.start",
            new { idempotencyKey = 42, root = @"C:\fixture" });

        var key = parameters.GetProperty("idempotencyKey").GetString();
        Assert.StartsWith("desktop-scan.start-", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ReadOperation_DoesNotAddIdempotencyKey()
    {
        var parameters = DesktopRequestParameters.Prepare("roots.list", null);

        Assert.False(parameters.TryGetProperty("idempotencyKey", out _));
    }

    [Fact]
    public void FormatLocalTimestamp_UsesCompactSecondPrecision()
    {
        const string timestamp = "2026-09-18T12:34:56+08:00";
        var expected = DateTimeOffset.Parse(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Equal(expected, MainWindow.FormatLocalTimestamp(timestamp));
    }

    [Theory]
    [InlineData("v1.1.0", 1, 1, 0)]
    [InlineData("V2.3.4-beta.1", 2, 3, 4)]
    public void ParseVersionTag_AcceptsGitHubReleaseTags(string tag, int major, int minor, int build)
    {
        var parsed = GitHubReleaseChecker.ParseVersionTag(tag);

        Assert.NotNull(parsed);
        Assert.Equal(new Version(major, minor, build), parsed);
    }

    [Fact]
    public void IsNewer_OnlyNotifiesForLaterVersion()
    {
        Assert.True(GitHubReleaseChecker.IsNewer(new Version(1, 2, 0), "1.1.0"));
        Assert.False(GitHubReleaseChecker.IsNewer(new Version(1, 1, 0), "1.1.0"));
        Assert.False(GitHubReleaseChecker.IsNewer(new Version(1, 0, 9), "1.1.0"));
    }
}

public sealed class DesktopScanRequestTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public DesktopScanRequestTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PreparedScanStart_IsAcceptedByHostAndCompletes()
    {
        var root = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs",
            _fixture.TestId,
            "desktop-scan");
        var gameDirectory = Path.Combine(root, "GameA");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "Game.exe"), "test");
        File.WriteAllText(Path.Combine(gameDirectory, "data.xp3"), "test");

        var added = await InvokeAsync("roots.add", DesktopRequestParameters.Prepare("roots.add", new { root }));
        Assert.True(added.Ok, added.Error?.Message);

        var started = await InvokeAsync("scan.start", DesktopRequestParameters.Prepare("scan.start", new { root }));
        Assert.True(started.Ok, started.Error?.Message);
        Assert.Equal(OperationStatus.Accepted, started.Status);
        Assert.NotNull(started.JobId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync(
                "jobs.get",
                DesktopRequestParameters.Prepare("jobs.get", new { jobId = started.JobId }));
            var state = status.Data.GetProperty("state").GetString();
            if (state == "succeeded")
            {
                return;
            }

            Assert.NotEqual("failed", state);
            await Task.Delay(50);
        }

        throw new TimeoutException("Desktop 发起的扫描未在 15 秒内完成");
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, JsonElement parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "desktop-request-test",
            CancellationToken.None);
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"desktop-test-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = parameters,
            },
            CancellationToken.None);
    }
}
