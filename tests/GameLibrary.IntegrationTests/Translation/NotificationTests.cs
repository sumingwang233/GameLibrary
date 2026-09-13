using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>T18：通知批生成/生命周期、ack≠accept、host.stop 优雅停机。</summary>
public sealed class NotificationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public NotificationTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t18-test",
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

    private string InsertPendingCandidate()
    {
        var store = _fixture.State.Library.Store!;
        var candidateId = $"cand-{Guid.NewGuid():N}";
        store.UpsertCandidate(new PersistedCandidate
        {
            CandidateId = candidateId,
            JobId = "job-test",
            Kind = "gameRoot",
            RelativePath = "SomeGame",
            PhysicalPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{candidateId}",
            PayloadJson = "{}",
            ReviewState = "observed",
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        // 通知批只覆盖 pendingReview：模拟重扫晋升（T16 的合法双跳）。
        store.PromoteRescannedCandidate(
            $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{candidateId}",
            DateTime.UtcNow);
        // 通知批生成挂在扫描落库末尾（ScanCandidatePersistence.Persist）；
        // 直插候选的测试需显式触发一次，等价于"扫描发现新候选"步骤。
        store.EnsureCandidateBatch(DateTime.UtcNow);
        return candidateId;
    }

    [Fact]
    public async Task Notifications_CreatedForPendingReview_ListedAsPending()
    {
        var candidateId = InsertPendingCandidate();

        var list = await InvokeAsync("notifications.list");

        Assert.True(list.Ok, list.Error?.Message);
        var items = list.Data.GetProperty("items");
        var matching = items.EnumerateArray()
            .Where(i => i.GetProperty("candidateIds").EnumerateArray().Any(c => c.GetString() == candidateId))
            .ToList();
        var notification = Assert.Single(matching);
        Assert.Equal("pending", notification.GetProperty("state").GetString());
        Assert.Equal("candidatesReady", notification.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Acknowledge_MarksReadButDoesNotAcceptCandidate()
    {
        var candidateId = InsertPendingCandidate();
        var list = await InvokeAsync("notifications.list");
        var notificationId = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("candidateIds").EnumerateArray().Any(c => c.GetString() == candidateId))
            .GetProperty("notificationId").GetString()!;

        var ack = await InvokeAsync("notifications.acknowledge", new
        {
            idempotencyKey = $"t18-{notificationId}",
            notificationId,
        });

        Assert.True(ack.Ok, ack.Error?.Message);
        Assert.Equal("acknowledged", ack.Data.GetProperty("state").GetString());
        // ack ≠ accept：候选仍是 pendingReview。
        var candidate = await InvokeAsync("candidates.get", new { candidateId });
        Assert.True(candidate.Ok, candidate.Error?.Message);
        Assert.Equal("pendingReview", candidate.Data.GetProperty("reviewState").GetString());
        Assert.Contains(ack.NextActions, a => a.OperationId == "candidates.list");

        // 重复 ack：仅 pending 可迁移。
        var second = await InvokeAsync("notifications.acknowledge", new
        {
            idempotencyKey = $"t18-second-{Guid.NewGuid():N}",
            notificationId,
        });
        Assert.False(second.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, second.Error!.Code);
    }

    [Fact]
    public async Task Defer_KeepsStateAndSuppressedFromNewBatches()
    {
        var candidateId = InsertPendingCandidate();
        var list = await InvokeAsync("notifications.list");
        var notificationId = list.Data.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("candidateIds").EnumerateArray().Any(c => c.GetString() == candidateId))
            .GetProperty("notificationId").GetString()!;

        var defer = await InvokeAsync("notifications.defer", new
        {
            idempotencyKey = $"t18-def-{Guid.NewGuid():N}",
            notificationId,
        });

        Assert.True(defer.Ok, defer.Error?.Message);
        Assert.Equal("deferred", defer.Data.GetProperty("state").GetString());

        // 再次入库新候选：deferred 批不复用（新批覆盖新候选），旧候选集合不被复活。
        // InsertPendingCandidate 内部已触发一次批生成——先找到它创建的新批再断言。
        var another = InsertPendingCandidate();
        var store = _fixture.State.Library.Store!;
        var batches = store.ListNotifications("pending");
        var newBatch = Assert.Single(batches);
        Assert.Contains(another, newBatch.CandidateIds);
        Assert.DoesNotContain(candidateId, newBatch.CandidateIds);
        // 旧通知仍是 deferred，未被复活。
        var old = store.TryGetNotification(notificationId)!;
        Assert.Equal("deferred", old.State);
    }

    [Fact]
    public async Task NotificationsGet_UnknownId_IsNotFound()
    {
        var envelope = await InvokeAsync("notifications.get", new { notificationId = "notif-missing" });
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.NotFound, envelope.Error!.Code);
    }

    [Fact]
    public async Task NotificationsList_InvalidStateFilter_IsRejected()
    {
        var envelope = await InvokeAsync("notifications.list", new { state = "bogus" });
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, envelope.Error!.Code);
    }
}
