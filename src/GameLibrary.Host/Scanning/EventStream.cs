using System.Collections.Concurrent;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Scanning;

/// <summary>库事件（策划案 5.6 / 契约 events.read）：实体/扫描变化的结构化记录。</summary>
public sealed record LibraryEvent
{
    public required long Sequence { get; init; }

    public required DateTime TimestampUtc { get; init; }

    /// <summary>candidate.discovered / candidate.promoted / game.created / scan.completed / scan.failed。</summary>
    public required string Type { get; init; }

    /// <summary>折叠键：同一实体的事件在抖动窗口内合并。</summary>
    public required string EntityKey { get; init; }

    public required string PayloadJson { get; init; }
}

/// <summary>
/// 库事件流（T16/T23-B）：内存环形队列折叠事件风暴（容量 4096、同实体 2 秒合并）；
/// 每条发布事件同步落库（event_records，折叠结果为唯一事实）——重启后序号延续、事件可回放；
/// 库可用时 events.read 以库为准（含 CursorExpired 判定与 dataEpoch 过滤），
/// 库未初始化时退回纯内存语义。保留策略 7 天 / 100,000 条惰性裁剪。
/// </summary>
public sealed class EventStream
{
    public const int Capacity = 4096;
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

    private sealed record Slot(long Sequence, DateTime TimestampUtc, string Type, string EntityKey, string PayloadJson);

    private readonly Slot[] _ring = new Slot[Capacity];
    private readonly ConcurrentDictionary<string, int> _indexByEntity = new(StringComparer.Ordinal);
    private readonly SqliteLibraryStore? _store;
    private long _sequence;
    private int _head; // 下一写入位

    public EventStream(SqliteLibraryStore? store = null)
    {
        _store = store;
        if (store is not null)
        {
            // 跨重启单调：序号从持久层恢复。
            _sequence = EventRecordStore.LatestSequence(
                store.DatabaseConnection,
                store.Info.DataEpoch);
        }
    }

    public LibraryEvent Publish(string type, string entityKey, object payload, DateTime utcNow)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, GameLibrary.Contracts.ContractJson.Options);
        lock (_ring)
        {
            // 折叠：2 秒窗口内同实体事件覆盖原槽（风暴合并；序号不变，持久层 UPSERT 同行）。
            if (_indexByEntity.TryGetValue(entityKey, out var existingIndex))
            {
                var existing = _ring[existingIndex];
                if (existing is not null && utcNow - existing.TimestampUtc <= CoalesceWindow)
                {
                    var merged = existing with
                    {
                        TimestampUtc = utcNow,
                        Type = type,
                        PayloadJson = payloadJson,
                    };
                    _ring[existingIndex] = merged;
                    Persist(merged);
                    return ToEvent(merged);
                }
            }

            var next = ++_sequence;
            var index = _head;
            var overwritten = _ring[index];
            if (overwritten is not null)
            {
                // 槽被复用：清除旧实体索引（最旧事件自然淘汰）。
                _indexByEntity.TryRemove(new KeyValuePair<string, int>(overwritten.EntityKey, index));
            }

            var slot = new Slot(next, utcNow, type, entityKey, payloadJson);
            _ring[index] = slot;
            _indexByEntity[entityKey] = index;
            _head = (index + 1) % Capacity;
            Persist(slot);
            return ToEvent(slot);
        }
    }

    private void Persist(Slot slot)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            EventRecordStore.AppendWithPrune(
                _store.DatabaseConnection,
                new PersistedEvent(
                    slot.Sequence,
                    _store.Info.DataEpoch,
                    slot.Type,
                    slot.EntityKey,
                    slot.PayloadJson,
                    slot.TimestampUtc));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // 事件落库失败不阻塞业务路径：内存环仍可读，持久化缺口由下次扫描补齐。
        }
    }

    /// <summary>
    /// 增量读取：库可用时以持久层为准（跨重启可回放、CursorExpired 判定含旧 epoch）；
    /// 库未初始化时退回内存环。
    /// </summary>
    public IReadOnlyList<LibraryEvent>? ReadAfter(long? afterSequence, int limit)
    {
        if (_store is not null)
        {
            var persisted = EventRecordStore.ReadAfter(
                _store.DatabaseConnection,
                _store.Info.DataEpoch,
                afterSequence,
                limit);
            return persisted is null ? null : persisted.Select(ToEvent).ToArray();
        }

        lock (_ring)
        {
            var slots = _ring.Where(s => s is not null).OrderBy(s => s.Sequence).ToArray();
            if (afterSequence is { } cursor)
            {
                var oldest = slots.Length > 0 ? slots[0].Sequence : _sequence + 1;
                if (cursor < oldest - 1 && slots.Length == Capacity)
                {
                    return null; // 游标落在已淘汰区间。
                }
            }

            return slots
                .Where(s => afterSequence is null || s.Sequence > afterSequence.Value)
                .TakeLast(limit)
                .Select(ToEvent)
                .ToArray();
        }
    }

    public long LatestSequence
    {
        get
        {
            lock (_ring)
            {
                return _sequence;
            }
        }
    }

    private static LibraryEvent ToEvent(Slot slot) => new()
    {
        Sequence = slot.Sequence,
        TimestampUtc = slot.TimestampUtc,
        Type = slot.Type,
        EntityKey = slot.EntityKey,
        PayloadJson = slot.PayloadJson,
    };

    private static LibraryEvent ToEvent(PersistedEvent evt) => new()
    {
        Sequence = evt.Sequence,
        TimestampUtc = evt.TimestampUtc,
        Type = evt.Type,
        EntityKey = evt.EntityKey,
        PayloadJson = evt.PayloadJson,
    };
}
