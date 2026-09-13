using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>通知批（T18，策划案 6.1）：稳定的候选 ID 集合 + 已通知/已处理状态，避免重启重复弹窗。</summary>
public sealed record NotificationBatch
{
    public required string NotificationId { get; init; }

    /// <summary>当前仅 candidatesReady（候选审核提醒）。</summary>
    public string Kind { get; init; } = "candidatesReady";

    /// <summary>批内候选 ID 集合（持久；ack/defer 不清空集合，保证重开仍可见详情）。</summary>
    public required IReadOnlyList<string> CandidateIds { get; init; }

    public required string Title { get; init; }

    /// <summary>pending / acknowledged / deferred。ack ≠ 接受候选；deferred 默认不再次主动通知。</summary>
    public required string State { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

public static class NotificationStore
{
    /// <summary>
    /// 通知批生成（T18）：找出尚无任何批覆盖的 pendingReview 候选；
    /// 有新候选时挂到既有 pending 批（更新集合）或创建新批。已 acknowledged/deferred 的批
    /// 不再复用（NW-04：保留用户决定，不持续重复提示）。
    /// 返回受影响的通知与本次是否新建。
    /// </summary>
    public static (NotificationBatch Batch, bool Created)? EnsureCandidateBatch(
        SqliteConnection connection, DateTime utcNow)
    {
        var pendingCandidateIds = ListCandidatesInReview(connection);
        if (pendingCandidateIds.Count == 0)
        {
            return null;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in ListBatches(connection, state: null))
        {
            foreach (var id in existing.CandidateIds)
            {
                covered.Add(id);
            }
        }

        var newIds = pendingCandidateIds.Where(id => !covered.Contains(id)).ToList();
        if (newIds.Count == 0)
        {
            return null;
        }

        var existingPending = ListBatches(connection, "pending")
            .FirstOrDefault(b => b.Kind == "candidatesReady");
        if (existingPending is not null)
        {
            var merged = existingPending.CandidateIds.Concat(newIds).Distinct().ToList();
            UpdateBatch(connection, existingPending.NotificationId, merged,
                $"发现 {merged.Count} 个新游戏候选", utcNow);
            return (existingPending with { CandidateIds = merged, Title = $"发现 {merged.Count} 个新游戏候选" }, false);
        }

        var batch = new NotificationBatch
        {
            NotificationId = $"notif-{Guid.NewGuid():N}",
            CandidateIds = newIds,
            Title = $"发现 {newIds.Count} 个新游戏候选",
            State = "pending",
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow,
        };
        InsertBatch(connection, batch);
        return (batch, true);
    }

    public static IReadOnlyList<NotificationBatch> ListBatches(SqliteConnection connection, string? state)
    {
        var result = new List<NotificationBatch>();
        using var command = connection.CreateCommand();
        command.CommandText = state is null
            ? "SELECT notification_id, kind, candidate_ids_json, title, state, created_utc, updated_utc FROM notification_batches ORDER BY created_utc, notification_id"
            : "SELECT notification_id, kind, candidate_ids_json, title, state, created_utc, updated_utc FROM notification_batches WHERE state = $state ORDER BY created_utc, notification_id";
        if (state is not null)
        {
            command.Parameters.AddWithValue("$state", state);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadBatch(reader));
        }

        return result;
    }

    public static NotificationBatch? TryGetBatch(SqliteConnection connection, string notificationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT notification_id, kind, candidate_ids_json, title, state, created_utc, updated_utc FROM notification_batches WHERE notification_id = $id";
        command.Parameters.AddWithValue("$id", notificationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatch(reader) : null;
    }

    /// <summary>状态迁移（acknowledge/defer）：仅 pending 可迁移；通知不存在或已处理返回 null。</summary>
    public static NotificationBatch? TransitionBatch(SqliteConnection connection, string notificationId, string toState, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        NotificationBatch? current;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT notification_id, kind, candidate_ids_json, title, state, created_utc, updated_utc FROM notification_batches WHERE notification_id = $id";
            select.Parameters.AddWithValue("$id", notificationId);
            using var reader = select.ExecuteReader();
            current = reader.Read() ? ReadBatch(reader) : null;
        }

        if (current is null || current.State != "pending")
        {
            transaction.Rollback();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE notification_batches SET state = $state, updated_utc = $now WHERE notification_id = $id";
            update.Parameters.AddWithValue("$state", toState);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", notificationId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return current with { State = toState, UpdatedUtc = utcNow };
    }

    private static void InsertBatch(SqliteConnection connection, NotificationBatch batch)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notification_batches (notification_id, kind, candidate_ids_json, title, state, created_utc, updated_utc)
            VALUES ($id, $kind, $candidates, $title, $state, $created, $created)
            """;
        command.Parameters.AddWithValue("$id", batch.NotificationId);
        command.Parameters.AddWithValue("$kind", batch.Kind);
        command.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(batch.CandidateIds));
        command.Parameters.AddWithValue("$title", batch.Title);
        command.Parameters.AddWithValue("$state", batch.State);
        command.Parameters.AddWithValue("$created", batch.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void UpdateBatch(SqliteConnection connection, string notificationId, IReadOnlyList<string> candidateIds, string title, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notification_batches SET candidate_ids_json = $candidates, title = $title, updated_utc = $now WHERE notification_id = $id";
        command.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(candidateIds));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", notificationId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ListCandidatesInReview(SqliteConnection connection)
    {
        var ids = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_id FROM candidates WHERE review_state = 'pendingReview' ORDER BY candidate_id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static NotificationBatch ReadBatch(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(2));
        var ids = document.RootElement.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
        return new NotificationBatch
        {
            NotificationId = reader.GetString(0),
            Kind = reader.GetString(1),
            CandidateIds = ids,
            Title = reader.GetString(3),
            State = reader.GetString(4),
            CreatedUtc = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }
}
