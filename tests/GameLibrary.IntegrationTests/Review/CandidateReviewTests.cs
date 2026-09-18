using System.Security.Cryptography;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Review;

/// <summary>
/// T11 入库/忽略（经真实管道）：手动扫描直接进入 pendingReview → accept/defer/ignore 状态机、
/// accept 幂等返回 GameId、Revision 冲突、忽略规则抑制与撤销恢复。
/// </summary>
public sealed class CandidateReviewTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public CandidateReviewTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "review-test",
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

    private static string CreateGameTree(string prefix, string gameName = "GameA")
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, gameName));
        File.WriteAllText(Path.Combine(path, gameName, "Game.exe"), "x");
        File.WriteAllText(Path.Combine(path, gameName, "data.xp3"), "x");
        return path;
    }

    private async Task ScanAndWaitAsync(string root)
    {
        var start = await InvokeAsync("scan.start", new { idempotencyKey = "scan-" + Guid.NewGuid().ToString("N"), root });
        Assert.True(start.Ok, start.Error?.Message);
        var jobId = start.JobId!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync("jobs.get", new { jobId });
            var state = status.Data.GetProperty("state").GetString();
            if (state is "succeeded" or "failed")
            {
                Assert.Equal("succeeded", state);
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"扫描作业 {jobId} 未在 15 秒内完成");
    }

    [Fact]
    public async Task ManualScan_ProducesPendingReview_AcceptCreatesGameWithAbsoluteEntry()
    {
        var root = CreateGameTree("review-accept");
        await InvokeAsync("roots.add", new { root });

        await ScanAndWaitAsync(root);

        // 用户主动发起的完整扫描已经是稳定观察，单轮即可进入待审核。
        var list = await InvokeAsync("candidates.list", new { });
        var pending = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("relativePath").GetString() == "GameA"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        Assert.Equal("pendingReview", pending.GetProperty("reviewState").GetString());
        var candidateId = pending.GetProperty("candidateId").GetString()!;
        var pendingRevision = pending.GetProperty("revision").GetInt32();

        // Revision 冲突。
        var stale = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "stale-accept",
            candidateId,
            expectedRevision = pendingRevision - 1,
        });
        Assert.False(stale.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, stale.Error!.Code);

        // 接受入库。
        var accept = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + candidateId,
            candidateId,
            expectedRevision = pendingRevision,
        });
        Assert.True(accept.Ok, accept.Error?.Message);
        var gameId = accept.Data.GetProperty("gameId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(gameId));

        // 同键重试：收据重放返回同一 GameId。
        var replay = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "accept-" + candidateId,
            candidateId,
            expectedRevision = pendingRevision,
        });
        Assert.True(replay.Ok);
        Assert.Equal(gameId, replay.Data.GetProperty("gameId").GetString());

        var acceptedCandidates = await InvokeAsync("candidates.list", new { state = "accepted" });
        Assert.True(acceptedCandidates.Ok, acceptedCandidates.Error?.Message);
        Assert.All(acceptedCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => Assert.Equal("accepted", candidate.GetProperty("reviewState").GetString()));
        Assert.Contains(acceptedCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("candidateId").GetString() == candidateId);

        var pendingCandidates = await InvokeAsync("candidates.list", new { state = "pendingReview" });
        Assert.True(pendingCandidates.Ok, pendingCandidates.Error?.Message);
        Assert.DoesNotContain(pendingCandidates.Data.GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("candidateId").GetString() == candidateId);

        var games = await InvokeAsync("games.list", new { });
        Assert.Contains(games.Data.GetProperty("items").EnumerateArray(),
            g => g.GetProperty("gameId").GetString() == gameId);
        var game = await InvokeAsync("games.get", new { gameId });
        Assert.True(game.Ok);
        Assert.Equal("GameA", game.Data.GetProperty("title").GetString());
        Assert.Equal("kirikiri", game.Data.GetProperty("engine").GetString());
        Assert.Equal(Path.Combine(root, "GameA", "Game.exe"), game.Data.GetProperty("entryPath").GetString());
    }

    [Fact]
    public async Task DeferFlow_MovesToDeferred()
    {
        var root = CreateGameTree("review-defer");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("reviewState").GetString() == "pendingReview"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var revision = item.GetProperty("revision").GetInt32();

        var defer = await InvokeAsync("candidates.defer", new
        {
            idempotencyKey = "defer-" + candidateId,
            candidateId,
            expectedRevision = revision,
        });
        Assert.True(defer.Ok, defer.Error?.Message);
        Assert.Equal("deferred", defer.Data.GetProperty("reviewState").GetString());

        // deferred 不能直接接受（状态机：Deferred → PendingReview）。
        var illegal = await InvokeAsync("candidates.accept", new
        {
            idempotencyKey = "deferred-accept-" + candidateId,
            candidateId,
            expectedRevision = defer.Data.GetProperty("revision").GetInt32(),
        });
        Assert.False(illegal.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, illegal.Error!.Code);
    }

    [Fact]
    public async Task IgnoreFlow_ExactPathRule_SuppressesRescan_AndRemoveRestores()
    {
        var root = CreateGameTree("review-ignore");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);

        var list = await InvokeAsync("candidates.list", new { });
        var item = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("reviewState").GetString() == "pendingReview"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal));
        var candidateId = item.GetProperty("candidateId").GetString()!;
        var detail = await InvokeAsync("candidates.get", new { candidateId });
        var physicalPath = detail.Data.GetProperty("physicalPath").GetString()!;
        var revision = item.GetProperty("revision").GetInt32();

        var ignore = await InvokeAsync("candidates.ignore", new
        {
            idempotencyKey = "ignore-" + candidateId,
            candidateId,
            expectedRevision = revision,
        });
        Assert.True(ignore.Ok, ignore.Error?.Message);
        var ignoreId = ignore.Data.GetProperty("ignoreId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(ignoreId));

        var rules = await InvokeAsync("ignores.list", new { });
        Assert.Contains(rules.Data.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("ignoreId").GetString() == ignoreId
                && r.GetProperty("scope").GetString() == "ExactPath");

        // 重扫：被 ExactPath 规则抑制，不再出现待审核候选。
        await ScanAndWaitAsync(root);
        var afterRescan = await InvokeAsync("candidates.list", new { });
        Assert.DoesNotContain(afterRescan.Data.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("relativePath").GetString() == "GameA"
                && i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal)
                && i.GetProperty("reviewState").GetString() == "pendingReview");

        // 撤销规则：ignored 候选回到 observed（恢复提示的唯一途径）。
        var remove = await InvokeAsync("ignores.remove", new
        {
            idempotencyKey = "unignore-" + ignoreId,
            ignoreId,
        });
        Assert.True(remove.Ok, remove.Error?.Message);
        Assert.Equal(1, remove.Data.GetProperty("restoredCandidates").GetInt32());

        var restored = await InvokeAsync("candidates.get", new { candidateId });
        Assert.Equal("observed", restored.Data.GetProperty("reviewState").GetString());
    }

    [Fact]
    public async Task IgnoreSubtree_ImmediatelySuppressesExistingCandidates()
    {
        var root = CreateGameTree("review-subtree");
        await InvokeAsync("roots.add", new { root });
        await ScanAndWaitAsync(root);
        var before = await InvokeAsync("candidates.list", new { });
        Assert.True(before.Data.GetProperty("total").GetInt32() >= 1);

        var create = await InvokeAsync("ignores.create", new
        {
            idempotencyKey = "subtree-" + root,
            scope = "Subtree",
            path = root,
            reason = "测试子树忽略",
        });
        Assert.True(create.Ok, create.Error?.Message);
        Assert.Equal(1, create.Data.GetProperty("suppressedCandidates").GetInt32());

        var after = await InvokeAsync("candidates.list", new { });
        Assert.DoesNotContain(after.Data.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("physicalPath").GetString()!.StartsWith(root, StringComparison.Ordinal)
                && i.GetProperty("reviewState").GetString() is "observed" or "pendingReview");
    }

    [Fact]
    public async Task IgnoreCreate_OutsideRegisteredRoots_IsDenied()
    {
        var denied = await InvokeAsync("ignores.create", new
        {
            idempotencyKey = "deny-" + Guid.NewGuid().ToString("N"),
            scope = "ExactPath",
            path = @"D:\Official\GameLibrary\artifacts",
        });
        Assert.False(denied.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, denied.Error!.Code);
    }
}
