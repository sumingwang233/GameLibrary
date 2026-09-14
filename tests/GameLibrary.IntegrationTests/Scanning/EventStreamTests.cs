using GameLibrary.Host.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>事件流（T16/T23-B）：容量 4096、2 秒抖动折叠、游标增量与过期判定、持久化跨重启回放。</summary>
public sealed class EventStreamTests
{
    private static async Task<GameLibrary.Infrastructure.Persistence.SqliteLibraryStore> CreateStoreAsync(string prefix)
    {
        var dir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        var init = await GameLibrary.Infrastructure.Persistence.SqliteLibraryStore.InitializeAsync(
            dir,
            new GameLibrary.Infrastructure.Persistence.SqliteLibraryStoreOptions
            {
                AppVersion = "test",
                ApiVersion = "1",
            },
            CancellationToken.None);
        Assert.True(init.IsOpened, init.Detail);
        return init.Store!;
    }

    private static async Task CleanupAsync(GameLibrary.Infrastructure.Persistence.SqliteLibraryStore store)
    {
        var dir = Path.GetDirectoryName(store.DatabasePath);
        await store.DisposeAsync();
        try
        {
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
    [Fact]
    public void Publish_CoalescesSameEntityWithinWindow()
    {
        var stream = new EventStream();
        var now = DateTime.UtcNow;

        var first = stream.Publish("candidate.discovered", "candidate:X", new { v = 1 }, now);
        var second = stream.Publish("candidate.promoted", "candidate:X", new { v = 2 }, now.AddSeconds(1));

        Assert.Equal(first.Sequence, second.Sequence); // 折叠进同一槽
        var events = stream.ReadAfter(null, 100)!;
        var slot = Assert.Single(events);
        Assert.Equal("candidate.promoted", slot.Type);
    }

    [Fact]
    public void Publish_DifferentEntities_GetDistinctSequences()
    {
        var stream = new EventStream();
        var now = DateTime.UtcNow;
        stream.Publish("a", "entity:A", new { }, now);
        stream.Publish("b", "entity:B", new { }, now);

        var events = stream.ReadAfter(null, 100)!;
        Assert.Equal(2, events.Count);
        Assert.True(events[1].Sequence > events[0].Sequence);
    }

    [Fact]
    public void Publish_EvictsOldestAtCapacity()
    {
        var stream = new EventStream();
        var now = DateTime.UtcNow;
        for (var i = 0; i < EventStream.Capacity; i++)
        {
            stream.Publish("t", $"entity:{i}", new { i }, now);
        }

        Assert.Equal(EventStream.Capacity, stream.ReadAfter(null, int.MaxValue)!.Count);

        // 再写一条：最旧被淘汰；指向最旧序号的游标过期。
        stream.Publish("t", "entity:new", new { }, now);
        Assert.Equal(EventStream.Capacity, stream.ReadAfter(null, int.MaxValue)!.Count);
        Assert.Null(stream.ReadAfter(0, int.MaxValue));
    }

    [Fact]
    public void ReadAfter_ReturnsOnlyNewerEvents()
    {
        var stream = new EventStream();
        var now = DateTime.UtcNow;
        var e1 = stream.Publish("t1", "e1", new { }, now);
        var e2 = stream.Publish("t2", "e2", new { }, now);

        var afterFirst = stream.ReadAfter(e1.Sequence, 100)!;
        Assert.Single(afterFirst);
        Assert.Equal(e2.Sequence, afterFirst[0].Sequence);

        var afterLatest = stream.ReadAfter(e2.Sequence, 100)!;
        Assert.Empty(afterLatest);
    }

    [Fact]
    public async Task PersistedStream_ReplaysAcrossRestart_WithMonotonicSequence()
    {
        var store = await CreateStoreAsync(
            $"evt-restart-{Guid.NewGuid():N}");
        try
        {
            var now = DateTime.UtcNow;
            var first = new EventStream(store);
            var e1 = first.Publish("game.created", "game:1", new { n = 1 }, now);
            var e2 = first.Publish("game.updated", "game:1", new { n = 2 }, now.AddSeconds(5));

            // 模拟重启：新 EventStream 实例（新环），持久层恢复序号与事件。
            var restarted = new EventStream(store);
            Assert.Equal(e2.Sequence, restarted.LatestSequence);

            var replay = restarted.ReadAfter(null, 100)!;
            Assert.Equal(2, replay.Count);
            Assert.Equal(e1.Sequence, replay[0].Sequence);
            Assert.Equal(e2.Sequence, replay[1].Sequence);

            // 重启后发布：序号延续不回绕；旧游标仍有效（事件未被淘汰）。
            var e3 = restarted.Publish("scan.completed", "job:1", new { }, now.AddSeconds(10));
            Assert.True(e3.Sequence > e2.Sequence, $"重启后序号应延续：{e3.Sequence} > {e2.Sequence}");
            var incremental = restarted.ReadAfter(e2.Sequence, 100)!;
            Assert.Single(incremental, e3);
        }
        finally
        {
            await CleanupAsync(store);
        }
    }

    [Fact]
    public async Task PersistedStream_EpochChange_InvalidatesOldCursor()
    {
        var store = await CreateStoreAsync(
            $"evt-epoch-{Guid.NewGuid():N}");
        try
        {
            var now = DateTime.UtcNow;
            var stream = new EventStream(store);
            stream.Publish("game.created", "game:1", new { }, now);

            // 模拟备份恢复：更换 dataEpoch。
            await store.RenewDataEpochAsync(CancellationToken.None);
            var epochChanged = new EventStream(store);

            // 旧序号游标在当前 epoch 无事件 → CursorExpired（null）。
            Assert.Null(epochChanged.ReadAfter(1, 100));
            // 不带游标：当前 epoch 全量读取为空。
            Assert.Empty(epochChanged.ReadAfter(null, 100)!);
        }
        finally
        {
            await CleanupAsync(store);
        }
    }
}
