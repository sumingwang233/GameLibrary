using GameLibrary.Host.Scanning;
using Xunit;

namespace GameLibrary.IntegrationTests.Scanning;

/// <summary>事件流（T16）：容量 4096、2 秒抖动折叠、游标增量与过期判定。</summary>
public sealed class EventStreamTests
{
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
}
