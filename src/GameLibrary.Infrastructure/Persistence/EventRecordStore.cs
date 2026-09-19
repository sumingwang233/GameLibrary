using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>持久化事件行（T23-B）：折叠后的库事件唯一事实；内存环只是热缓存。</summary>
public sealed record PersistedEvent(
    long Sequence,
    string DataEpoch,
    string Type,
    string EntityKey,
    string PayloadJson,
    DateTime TimestampUtc);

/// <summary>
/// 事件持久化（T23-B，契约第 8 节）：序号跨重启单调（库自增）；
/// 保留策略 = 7 天或 10,000 条先到为准，惰性裁剪；
/// 读取按当前 dataEpoch 过滤——备份恢复更换 epoch 后旧游标自然失效。
/// </summary>
public static class EventRecordStore
{
    // 原为 100_000：日常库实测长期卡在该上限（约 33.6 MB），对事件流增量读取毫无收益。
    // 前端按 cursor 增量拉取，10_000 条足以覆盖离线期间的事件回放。
    public const int MaxEvents = 10_000;
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);
    private const int PruneEvery = 256;

    /// <summary>当前 epoch 的最大序号；无库/无事件为 0。EventStream 以此恢复跨重启序号。</summary>
    public static long LatestSequence(SqliteConnection connection, string dataEpoch)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM event_records WHERE data_epoch = $epoch";
        command.Parameters.AddWithValue("$epoch", dataEpoch);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// UPSERT：折叠（同实体 2 秒窗口合并）复用原序号覆盖内容，与内存环语义一致（T16 游标语义）。
    /// </summary>
    public static void Append(SqliteConnection connection, PersistedEvent evt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_records (sequence, data_epoch, type, entity_key, payload_json, timestamp_utc)
            VALUES ($seq, $epoch, $type, $entity, $payload, $ts)
            ON CONFLICT(sequence) DO UPDATE SET
                type = excluded.type, entity_key = excluded.entity_key,
                payload_json = excluded.payload_json, timestamp_utc = excluded.timestamp_utc
            """;
        command.Parameters.AddWithValue("$seq", evt.Sequence);
        command.Parameters.AddWithValue("$epoch", evt.DataEpoch);
        command.Parameters.AddWithValue("$type", evt.Type);
        command.Parameters.AddWithValue("$entity", evt.EntityKey);
        command.Parameters.AddWithValue("$payload", evt.PayloadJson);
        command.Parameters.AddWithValue("$ts", evt.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 增量读取（当前 epoch）。游标落在已裁剪区间或旧 epoch 时返回 null（CursorExpired）。
    /// </summary>
    public static IReadOnlyList<PersistedEvent>? ReadAfter(
        SqliteConnection connection, string dataEpoch, long? afterSequence, int limit)
    {
        long minSequence;
        long maxSequence;
        using (var bounds = connection.CreateCommand())
        {
            bounds.CommandText = "SELECT COALESCE(MIN(sequence), 0), COALESCE(MAX(sequence), 0) FROM event_records WHERE data_epoch = $epoch";
            bounds.Parameters.AddWithValue("$epoch", dataEpoch);
            using var reader = bounds.ExecuteReader();
            reader.Read();
            minSequence = reader.GetInt64(0);
            maxSequence = reader.GetInt64(1);
        }

        if (afterSequence is { } cursor)
        {
            // 无事件且游标非零：游标来自旧 epoch 或已清空 → 过期。
            // 有事件：游标指向的行不在当前 epoch 范围内 → 过期。
            if (maxSequence == 0
                || cursor < minSequence - 1
                || cursor > maxSequence)
            {
                return null;
            }
        }

        var result = new List<PersistedEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sequence, type, entity_key, payload_json, timestamp_utc
                FROM event_records
                WHERE data_epoch = $epoch AND sequence > $after
                ORDER BY sequence
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$epoch", dataEpoch);
            command.Parameters.AddWithValue("$after", afterSequence ?? 0);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PersistedEvent(
                    reader.GetInt64(0),
                    dataEpoch,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        return result;
    }

    /// <summary>惰性裁剪：每 PruneEvery 次追加触发一次（7 天窗口 + 绝对上限，先到为准）。</summary>
    public static void AppendWithPrune(SqliteConnection connection, PersistedEvent evt)
    {
        Append(connection, evt);
        if (evt.Sequence % PruneEvery != 0)
        {
            return;
        }

        var cutoff = evt.TimestampUtc - RetentionWindow;
        using (var byAge = connection.CreateCommand())
        {
            byAge.CommandText = "DELETE FROM event_records WHERE timestamp_utc < $cutoff";
            byAge.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
            byAge.ExecuteNonQuery();
        }

        using (var byCount = connection.CreateCommand())
        {
            byCount.CommandText = """
                DELETE FROM event_records WHERE sequence IN (
                    SELECT sequence FROM event_records ORDER BY sequence DESC LIMIT -1 OFFSET $keep
                )
                """;
            byCount.Parameters.AddWithValue("$keep", MaxEvents);
            byCount.ExecuteNonQuery();
        }
    }
}
