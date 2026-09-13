using System.Collections.Concurrent;

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
/// 库事件流（T16）：内存环形队列，容量 4096（契约队列上限）；
/// 同实体 2 秒抖动窗口内合并（更新最后一条 payload，不新增行）——事件风暴折叠；
/// 跨重启不保留：旧游标读取返回 CursorExpired 由调用方全量重取。
/// </summary>
public sealed class EventStream
{
    public const int Capacity = 4096;
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

    private sealed record Slot(long Sequence, DateTime TimestampUtc, string Type, string EntityKey, string PayloadJson);

    private readonly Slot[] _ring = new Slot[Capacity];
    private readonly ConcurrentDictionary<string, int> _indexByEntity = new(StringComparer.Ordinal);
    private long _sequence;
    private int _head; // 下一写入位

    public LibraryEvent Publish(string type, string entityKey, object payload, DateTime utcNow)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, GameLibrary.Contracts.ContractJson.Options);
        lock (_ring)
        {
            // 折叠：2 秒窗口内同实体事件覆盖原槽（风暴合并）。
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
                    return ToEvent(merged);
                }
            }

            var sequence = ++_sequence;
            var index = _head;
            var overwritten = _ring[index];
            if (overwritten is not null)
            {
                // 槽被复用：清除旧实体索引（最旧事件自然淘汰）。
                _indexByEntity.TryRemove(new KeyValuePair<string, int>(overwritten.EntityKey, index));
            }

            _ring[index] = new Slot(sequence, utcNow, type, entityKey, payloadJson);
            _indexByEntity[entityKey] = index;
            _head = (index + 1) % Capacity;
            return ToEvent(_ring[index]);
        }
    }

    /// <summary>
    /// 增量读取：afterSequence 之后的事件按序返回；游标早于缓冲起点（已被淘汰）时返回 null（CursorExpired）。
    /// </summary>
    public IReadOnlyList<LibraryEvent>? ReadAfter(long? afterSequence, int limit)
    {
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
}
