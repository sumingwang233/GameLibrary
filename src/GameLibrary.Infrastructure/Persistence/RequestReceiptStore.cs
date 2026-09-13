using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 幂等收据（契约 7.1）：按 (libraryInstanceId, actor, operationId, idempotencyKey) 唯一。
/// prepared = 意图已登记、结果未定；completed = 结果已定（成功/业务失败都是终态，重放返回原结果）。
/// 收据在宿主重启后保留：活动收据保留到终态，超期淘汰随 T24/T27。
/// </summary>
public sealed record RequestReceipt
{
    public required string LibraryInstanceId { get; init; }

    public required string Actor { get; init; }

    public required string OperationId { get; init; }

    public required string IdempotencyKey { get; init; }

    /// <summary>请求参数摘要；同键不同请求返回 IdempotencyConflict。</summary>
    public required string RequestDigest { get; init; }

    public required string Status { get; init; }

    /// <summary>launch 类收据在进程创建后写入的尝试引用（attemptId/pid/startedUtc/exe）。</summary>
    public string? AttemptJson { get; init; }

    public string? ResultJson { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

public static class RequestReceiptStore
{
    private const string SelectSql = """
        SELECT request_digest, status, attempt_json, result_json, created_utc, updated_utc
        FROM request_receipts
        WHERE library_instance_id = $instance AND actor = $actor
          AND operation_id = $operation AND idempotency_key = $key
        """;

    public static RequestReceipt? TryGet(SqliteConnection connection, string instanceId, string actor, string operationId, string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql;
        command.Parameters.AddWithValue("$instance", instanceId);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RequestReceipt
        {
            LibraryInstanceId = instanceId,
            Actor = actor,
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            RequestDigest = reader.GetString(0),
            Status = reader.GetString(1),
            AttemptJson = reader.IsDBNull(2) ? null : reader.GetString(2),
            ResultJson = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedUtc = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedUtc = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    public static void InsertPrepared(SqliteConnection connection, RequestReceipt receipt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO request_receipts
                (library_instance_id, actor, operation_id, idempotency_key, request_digest,
                 status, attempt_json, result_json, created_utc, updated_utc)
            VALUES ($instance, $actor, $operation, $key, $digest, 'prepared', $attempt, NULL, $created, $created)
            """;
        command.Parameters.AddWithValue("$instance", receipt.LibraryInstanceId);
        command.Parameters.AddWithValue("$actor", receipt.Actor);
        command.Parameters.AddWithValue("$operation", receipt.OperationId);
        command.Parameters.AddWithValue("$key", receipt.IdempotencyKey);
        command.Parameters.AddWithValue("$digest", receipt.RequestDigest);
        command.Parameters.AddWithValue("$attempt", (object?)receipt.AttemptJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", receipt.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void UpdateAttempt(SqliteConnection connection, RequestReceipt receipt, string attemptJson)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE request_receipts
            SET attempt_json = $attempt, updated_utc = $updated
            WHERE library_instance_id = $instance AND actor = $actor
              AND operation_id = $operation AND idempotency_key = $key
            """;
        BindKey(command, receipt);
        command.Parameters.AddWithValue("$attempt", attemptJson);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void Complete(SqliteConnection connection, RequestReceipt receipt, string resultJson)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE request_receipts
            SET status = 'completed', result_json = $result, updated_utc = $updated
            WHERE library_instance_id = $instance AND actor = $actor
              AND operation_id = $operation AND idempotency_key = $key
            """;
        BindKey(command, receipt);
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void BindKey(SqliteCommand command, RequestReceipt receipt)
    {
        command.Parameters.AddWithValue("$instance", receipt.LibraryInstanceId);
        command.Parameters.AddWithValue("$actor", receipt.Actor);
        command.Parameters.AddWithValue("$operation", receipt.OperationId);
        command.Parameters.AddWithValue("$key", receipt.IdempotencyKey);
    }
}
