using System.Globalization;
using System.Text.Json;
using GameLibrary.Domain.Detection;

namespace GameLibrary.Infrastructure.Persistence;

public sealed partial class SqliteLibraryStore
{
    public IReadOnlyList<FlashDirectoryRule> ListFlashDirectoryRules() => Info.SchemaVersion < 27 ? [] : ReadExclusive((connection, _) =>
    {
        var result = new List<FlashDirectoryRule>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT directory_path, kind, entry_paths_json, inventory_json, revision FROM flash_directory_rules ORDER BY directory_path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1),
                JsonSerializer.Deserialize<string[]>(reader.GetString(2))!,
                JsonSerializer.Deserialize<string[]>(reader.GetString(3))!, reader.GetInt32(4)));
        return result;
    });

    public void SaveFlashDirectoryRule(FlashDirectoryRule rule, DateTime now) => Execute((connection, _) =>
    {
        using var command = connection.CreateCommand();
        command.Transaction = _writeTransaction;
        command.CommandText = """
            INSERT INTO flash_directory_rules (directory_path, kind, entry_paths_json, inventory_json, revision, updated_utc)
            VALUES ($path, $kind, $entries, $inventory, 1, $now)
            ON CONFLICT(directory_path) DO UPDATE SET kind=excluded.kind, entry_paths_json=excluded.entry_paths_json,
                inventory_json=excluded.inventory_json, revision=flash_directory_rules.revision+1, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$path", rule.DirectoryPath);
        command.Parameters.AddWithValue("$kind", rule.Kind);
        command.Parameters.AddWithValue("$entries", JsonSerializer.Serialize(rule.EntryPaths));
        command.Parameters.AddWithValue("$inventory", JsonSerializer.Serialize(rule.Inventory));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    });

    /// <summary>只在审核事务内修改库成员/入口，原 ID 与全部字段、资产、历史保持。</summary>
    public void ApplyFlashGameAdjustment(string gameId, int revision, string membership, string? entryPath, DateTime now)
        => Execute((connection, _) =>
        {
            if (_writeTransaction is null) throw new InvalidOperationException("Flash 调整必须属于审核事务");
            using var command = connection.CreateCommand();
            command.Transaction = _writeTransaction;
            command.CommandText = """
                UPDATE games SET membership=$membership, entry_path=coalesce($entry, entry_path),
                    engine=CASE WHEN $entry IS NOT NULL THEN 'flash' ELSE engine END,
                    revision=revision+1, updated_utc=$now WHERE game_id=$id AND revision=$revision
                """;
            command.Parameters.AddWithValue("$membership", membership);
            command.Parameters.AddWithValue("$entry", (object?)entryPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", gameId);
            command.Parameters.AddWithValue("$revision", revision);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Flash 库记录已变更，请重新审核调整清单");
        });
}
