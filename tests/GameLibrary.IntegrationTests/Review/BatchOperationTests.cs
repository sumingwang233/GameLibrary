using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Hosting;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.IntegrationTests.Review;

public sealed class BatchOperationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;
    public BatchOperationTests(PipeServerFixture fixture) => _fixture = fixture;

    private async Task<Envelope<JsonElement>> Invoke(string operation, object parameters)
    {
        await using var connection = await HostConnection.ConnectAsync(_fixture.State.DataDirectory, "batch-test", CancellationToken.None);
        return await connection.InvokeAsync(new IpcRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OperationId = operation,
            Parameters = JsonSerializer.SerializeToElement(parameters),
        }, CancellationToken.None);
    }

    private string AddCandidate()
    {
        var id = "candidate-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_fixture.State.DataDirectory, "test-games", id);
        Directory.CreateDirectory(path);
        _fixture.State.Library.Store!.UpsertCandidate(new PersistedCandidate
        {
            CandidateId = id,
            PhysicalPath = path,
            RelativePath = id,
            Kind = "gameRoot",
            PayloadJson = "{}",
            ReviewState = "pendingReview",
            ObservedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task CandidateBatch_PreservesSuccessAndReplaysInInputOrder()
    {
        var first = AddCandidate();
        var second = AddCandidate();
        var input = new
        {
            action = "defer",
            items = new[]
        {
            new { candidateId = first, expectedRevision = 1 },
            new { candidateId = second, expectedRevision = 9 },
        },
            idempotencyKey = Guid.NewGuid().ToString("N")
        };
        var response = await Invoke("candidates.review_batch", input);
        Assert.True(response.Ok, response.Error?.Message);
        var results = response.Data.GetProperty("items");
        Assert.Equal(first, results[0].GetProperty("candidateId").GetString());
        Assert.True(results[0].GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.RevisionConflict, results[1].GetProperty("result").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("deferred", _fixture.State.Library.Store!.TryGetCandidate(first)!.ReviewState);
        Assert.Equal("pendingReview", _fixture.State.Library.Store.TryGetCandidate(second)!.ReviewState);
        var replay = await Invoke("candidates.review_batch", input);
        // Nested item responses carry the original request ID, but the outer response tracks this retry.
        Assert.Equal(response.Data.GetRawText(), replay.Data.GetRawText());
        Assert.NotEqual(response.RequestId, replay.RequestId);
        Assert.Equal(2, _fixture.State.Library.Store.TryGetCandidate(first)!.Revision);
    }

    [Fact]
    public async Task ConcurrentSameKeyBatch_ExecutesOnlyOnce()
    {
        var id = AddCandidate();
        var input = new
        {
            action = "defer",
            items = new[] { new { candidateId = id, expectedRevision = 1 } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Invoke("candidates.review_batch", input)));
        Assert.All(results, result => Assert.True(result.Ok, result.Error?.Message));
        Assert.All(results, result => Assert.Equal(results[0].Data.GetRawText(), result.Data.GetRawText()));
        Assert.Equal(2, _fixture.State.Library.Store!.TryGetCandidate(id)!.Revision);
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackAllCandidateItems()
    {
        var first = AddCandidate();
        var second = AddCandidate();
        _fixture.State.Library.Store!.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER fail_batch BEFORE UPDATE ON candidates WHEN NEW.candidate_id = '{second}' BEGIN SELECT RAISE(ABORT, 'simulated database failure'); END";
            command.ExecuteNonQuery();
        });
        try
        {
            var response = await Invoke("candidates.review_batch", new
            {
                action = "defer",
                items = new[] { new { candidateId = first, expectedRevision = 1 }, new { candidateId = second, expectedRevision = 1 } },
                idempotencyKey = Guid.NewGuid().ToString("N")
            });
            Assert.False(response.Ok);
            Assert.Equal("pendingReview", _fixture.State.Library.Store.TryGetCandidate(first)!.ReviewState);
            Assert.Equal(1, _fixture.State.Library.Store.TryGetCandidate(first)!.Revision);
        }
        finally
        {
            _fixture.State.Library.Store.WriteExclusive((connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TRIGGER fail_batch";
                command.ExecuteNonQuery();
            });
        }
    }

    [Fact]
    public async Task TagReorder_ConflictsRollBackAndSuccessfulRetriesReplay()
    {
        var first = await Invoke("tags.create", new { name = Guid.NewGuid().ToString("N"), idempotencyKey = Guid.NewGuid().ToString("N") });
        var second = await Invoke("tags.create", new { name = Guid.NewGuid().ToString("N"), idempotencyKey = Guid.NewGuid().ToString("N") });
        var a = first.Data.GetProperty("tagId").GetString()!;
        var b = second.Data.GetProperty("tagId").GetString()!;
        var conflict = await Invoke("tags.reorder", new
        {
            items = new[]
        {
            new { tagId = a, expectedRevision = 1, sortOrder = 10 },
            new { tagId = b, expectedRevision = 9, sortOrder = 20 },
        },
            idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal(ErrorCodes.RevisionConflict, conflict.Error?.Code);
        Assert.Equal(1, _fixture.State.Library.Store!.TryGetTag(a)!.Revision);
        var input = new
        {
            items = new[]
        {
            new { tagId = a, expectedRevision = 1, sortOrder = 10 },
            new { tagId = b, expectedRevision = 1, sortOrder = 20 },
        },
            idempotencyKey = Guid.NewGuid().ToString("N")
        };
        var success = await Invoke("tags.reorder", input);
        Assert.True(success.Ok, success.Error?.Message);
        Assert.Equal(success.Data.GetRawText(), (await Invoke("tags.reorder", input)).Data.GetRawText());
        Assert.Equal(2, _fixture.State.Library.Store.TryGetTag(a)!.Revision);
    }

    [Theory]
    [InlineData("candidates.review_batch", "candidateId")]
    [InlineData("tags.reorder", "tagId")]
    public async Task BatchInput_RejectsDuplicatesAndMoreThan1000Items(string operation, string idKey)
    {
        var duplicate = new Dictionary<string, object> { [idKey] = "same", ["expectedRevision"] = 1, ["sortOrder"] = 0 };
        var response = await Invoke(operation, new { action = "defer", items = new[] { duplicate, duplicate }, idempotencyKey = Guid.NewGuid().ToString("N") });
        Assert.Equal(ErrorCodes.InvalidArgument, response.Error?.Code);
        var large = await Invoke(operation, new
        {
            action = "defer",
            items = Enumerable.Range(0, 1001).Select(i =>
            new Dictionary<string, object> { [idKey] = i.ToString(), ["expectedRevision"] = 1, ["sortOrder"] = i }).ToArray(),
            idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal(ErrorCodes.InvalidArgument, large.Error?.Code);
    }

    [Fact]
    public async Task EventsWait_InvalidatesAnUnboundCursorWhenTheLibrarySessionChanges()
    {
        var dispatcher = new OperationDispatcher(_fixture.State);
        var waiting = dispatcher.DispatchAsync(new IpcRequest
        {
            RequestId = "old-cursor",
            OperationId = "events.wait",
            Parameters = JsonSerializer.SerializeToElement(new { cursor = _fixture.State.Events.LatestSequence, timeoutMs = 1000 }),
        });
        Assert.False(waiting.IsCompleted);
        var store = _fixture.State.Library.Store!;
        await store.RenewDataEpochAsync(CancellationToken.None);
        _fixture.State.BindLibraryStore(store);
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ErrorCodes.CursorExpired, result.Error?.Code);
    }

    [Fact]
    public async Task EventsWait_WakesOnNewSequenceAndTimesOutWithoutBlockingOtherRequests()
    {
        var dispatcher = new OperationDispatcher(_fixture.State);
        var cursor = _fixture.State.Events.LatestSequence;
        var waiting = dispatcher.DispatchAsync(new IpcRequest
        {
            RequestId = "wait",
            OperationId = "events.wait",
            Parameters = JsonSerializer.SerializeToElement(new { cursor, timeoutMs = 1000 }),
        });
        Assert.False(waiting.IsCompleted);
        var status = await dispatcher.DispatchAsync(new IpcRequest { RequestId = "status", OperationId = "host.status" });
        Assert.True(status.Ok);
        _fixture.State.Events.Publish("game.updated", "game:test", new { gameId = "test" }, DateTime.UtcNow);
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        var timeout = await Invoke("events.wait", new { cursor = _fixture.State.Events.LatestSequence, timeoutMs = 1 });
        Assert.True(timeout.Ok);
        Assert.Empty(timeout.Data.GetProperty("items").EnumerateArray());
    }
}
