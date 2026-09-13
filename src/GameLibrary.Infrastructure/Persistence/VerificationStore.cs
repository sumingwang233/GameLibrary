using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>验证记录落库（T08）：绑定工具指纹与隔离样本；双结论（游戏启动/翻译生效）分开累积。</summary>
public static class VerificationStore
{
    public static void Insert(SqliteConnection connection, GameLibrary.Domain.Tools.ToolVerificationRecord record)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO verification_records
                (record_id, tool_id, tool_fingerprint, engine, sample_path, status,
                 game_started, translation_confirmed, note, created_utc, updated_utc)
            VALUES ($id, $tool, $fp, $engine, $sample, $status, $started, $confirmed, $note, $created, $created)
            """;
        Bind(command, record);
        command.ExecuteNonQuery();
    }

    public static GameLibrary.Domain.Tools.ToolVerificationRecord? TryGet(SqliteConnection connection, string recordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_id, tool_id, tool_fingerprint, engine, sample_path, status,
                   game_started, translation_confirmed, note, created_utc, updated_utc
            FROM verification_records WHERE record_id = $id
            """;
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static IReadOnlyList<GameLibrary.Domain.Tools.ToolVerificationRecord> List(SqliteConnection connection, string? toolId)
    {
        var result = new List<GameLibrary.Domain.Tools.ToolVerificationRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_id, tool_id, tool_fingerprint, engine, sample_path, status,
                   game_started, translation_confirmed, note, created_utc, updated_utc
            FROM verification_records
            WHERE $tool IS NULL OR tool_id = $tool
            ORDER BY created_utc, record_id
            """;
        command.Parameters.AddWithValue("$tool", (object?)toolId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public static void Update(SqliteConnection connection, GameLibrary.Domain.Tools.ToolVerificationRecord record)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE verification_records
            SET status = $status, game_started = $started, translation_confirmed = $confirmed,
                note = $note, updated_utc = $updated
            WHERE record_id = $id
            """;
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$started", record.GameStartedConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$confirmed", record.TranslationConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", record.RecordId);
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, GameLibrary.Domain.Tools.ToolVerificationRecord record)
    {
        command.Parameters.AddWithValue("$id", record.RecordId);
        command.Parameters.AddWithValue("$tool", record.ToolId);
        command.Parameters.AddWithValue("$fp", record.ToolFingerprint);
        command.Parameters.AddWithValue("$engine", record.Engine);
        command.Parameters.AddWithValue("$sample", record.SamplePath);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$started", record.GameStartedConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$confirmed", record.TranslationConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static GameLibrary.Domain.Tools.ToolVerificationRecord Read(SqliteDataReader reader) => new()
    {
        RecordId = reader.GetString(0),
        ToolId = reader.GetString(1),
        ToolFingerprint = reader.GetString(2),
        Engine = reader.GetString(3),
        SamplePath = reader.GetString(4),
        Status = Enum.Parse<GameLibrary.Domain.Tools.ToolVerificationStatus>(reader.GetString(5)),
        GameStartedConfirmed = reader.GetInt64(6) == 1,
        TranslationConfirmed = reader.GetInt64(7) == 1,
        Note = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
