using System.Globalization;
using System.Text.Json;
using GameLibrary.Domain.Detection;

namespace GameLibrary.Infrastructure.Persistence;

public sealed partial class SqliteLibraryStore
{
    /// <summary>只重扫仍待处理的旧逐文件候选所属扫描根；手动根保持由用户管理。</summary>
    public IReadOnlyList<string> ListLegacyFlashScanRoots() => ReadExclusive((connection, _) =>
    {
        var paths = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT physical_path FROM candidates
                WHERE kind = 'fileGame' AND review_state IN ('observed', 'pendingReview')
                    AND lower(substr(physical_path, -4)) = '.swf'
                    AND coalesce(json_type(payload_json, '$.flash'), 'null') <> 'object'
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }
        var roots = RuntimeStateStore.ReadRoots(connection);
        paths.RemoveAll(path => LibraryCatalogStore.IsDefinitelyMissing(path)
            || roots.Any(root => root.Kind == "manual" && RuntimeStateStore.ContainsPath(root.PhysicalPath, path)));
        return roots.Where(root => root.Kind == "library"
            && paths.Any(path => RuntimeStateStore.ContainsPath(root.PhysicalPath, path)))
            .Select(root => root.PhysicalPath).ToArray();
    });

    /// <summary>完整目录分组代替旧待审核文件；不修改游戏、用户暂缓/忽略或已接受记录。</summary>
    public int SupersedeLegacyFlashCandidates(FlashDirectoryGroup group, DateTime now,
        ILookup<string, PersistedCandidate>? candidates = null) => Execute((connection, _) =>
    {
        if (!group.Complete) return 0;
        var manualRoots = RuntimeStateStore.ReadRoots(connection).Where(root => root.Kind == "manual").ToArray();
        var paths = group.Inventory.Select(entry => Path.GetFullPath(Path.Combine(group.DirectoryPath,
                entry.Replace('/', Path.DirectorySeparatorChar))))
            .Where(path => RuntimeStateStore.ContainsPath(group.DirectoryPath, path)
                && !manualRoots.Any(root => RuntimeStateStore.ContainsPath(root.PhysicalPath, path)))
            .ToArray();
        candidates ??= LibraryCatalogStore.ListCandidates(connection).ToLookup(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase);
        var revisions = paths.SelectMany(path => candidates[path])
            .Select(item => new { id = item.CandidateId, revision = item.Revision }).ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = _writeTransaction;
        command.CommandText = """
            UPDATE candidates SET review_state='deferred', revision=revision+1,
                payload_json=json_set(payload_json, '$.flashSupersededBy', $directory), updated_utc=$now
            WHERE kind='fileGame' AND review_state IN ('observed', 'pendingReview')
                AND lower(substr(physical_path, -4))='.swf'
                AND coalesce(json_type(payload_json, '$.flash'), 'null') <> 'object'
                AND (candidate_id, revision) IN (
                    SELECT json_extract(value, '$.id'), json_extract(value, '$.revision') FROM json_each($revisions))
            """;
        command.Parameters.AddWithValue("$directory", group.DirectoryPath);
        command.Parameters.AddWithValue("$revisions", JsonSerializer.Serialize(revisions));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    });

    public PersistedCandidate? RestoreSupersededFlashCandidate(PersistedCandidate candidate, string directory,
        string payloadJson, DateTime now) => Execute((connection, _) =>
    {
        if (_writeTransaction is null) throw new InvalidOperationException("Flash 入口恢复必须属于审核事务");
        using var document = JsonDocument.Parse(candidate.PayloadJson);
        if (!document.RootElement.TryGetProperty("flashSupersededBy", out var marker)
            || marker.ValueKind != JsonValueKind.String
            || !string.Equals(marker.GetString(), directory, StringComparison.OrdinalIgnoreCase)) return null;
        using var command = connection.CreateCommand();
        command.Transaction = _writeTransaction;
        command.CommandText = """
            UPDATE candidates SET review_state='pendingReview', revision=revision+1, payload_json=$payload, updated_utc=$now
            WHERE candidate_id=$id AND revision=$revision AND review_state='deferred'
                AND json_extract(payload_json, '$.flashSupersededBy')=$directory
            """;
        command.Parameters.AddWithValue("$id", candidate.CandidateId);
        command.Parameters.AddWithValue("$revision", candidate.Revision);
        command.Parameters.AddWithValue("$directory", marker.GetString()!);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1 ? LibraryCatalogStore.TryGetCandidate(connection, candidate.CandidateId) : null;
    });

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
