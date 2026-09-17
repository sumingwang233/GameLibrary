using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>持久化标签行（含游戏计数，供 tags.list）。</summary>
public sealed record PersistedTag(
    string TagId,
    string Kind,
    string Name,
    string? Color,
    int Revision,
    int GameCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// 标签存储（T-collections，迁移 v18）：标签定义按 (类型, 规范值) 唯一——
/// engine 标签来自引擎识别（入库时自动创建，重扫只替换同源记录），
/// user 标签由用户创建；tag_overrides 记录 Suppress（阻止扫描恢复用户删除的自动标签）
/// 与 ForceAdd（预留）。游戏↔标签关联随游戏删除级联清理。
/// </summary>
public static class TagStore
{
    public static IReadOnlyList<PersistedTag> ListTags(SqliteConnection connection)
    {
        var result = new List<PersistedTag>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tag_id, t.kind, t.name, t.color, t.revision, t.created_utc, t.updated_utc,
                   (SELECT COUNT(*) FROM game_tags gt WHERE gt.tag_id = t.tag_id) AS game_count
            FROM tags t
            ORDER BY t.kind, t.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedTag(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(7),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public static PersistedTag? TryGetTag(SqliteConnection connection, string tagId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tag_id, t.kind, t.name, t.color, t.revision, t.created_utc, t.updated_utc,
                   (SELECT COUNT(*) FROM game_tags gt WHERE gt.tag_id = t.tag_id)
            FROM tags t WHERE t.tag_id = $id
            """;
        command.Parameters.AddWithValue("$id", tagId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PersistedTag(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(7),
            DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public static PersistedTag? TryGetTagByName(SqliteConnection connection, string kind, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tag_id, t.kind, t.name, t.color, t.revision, t.created_utc, t.updated_utc,
                   (SELECT COUNT(*) FROM game_tags gt WHERE gt.tag_id = t.tag_id)
            FROM tags t WHERE t.kind = $kind AND t.name = $name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PersistedTag(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(7),
            DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public static void CreateTag(SqliteConnection connection, PersistedTag tag)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tags (tag_id, kind, name, color, revision, created_utc, updated_utc)
            VALUES ($id, $kind, $name, $color, $rev, $created, $updated)
            """;
        command.Parameters.AddWithValue("$id", tag.TagId);
        command.Parameters.AddWithValue("$kind", tag.Kind);
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", (object?)tag.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$rev", tag.Revision);
        command.Parameters.AddWithValue("$created", tag.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", tag.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>更新用户标签（名称/颜色）；期望修订乐观校验，返回新修订，冲突返回 null。</summary>
    public static int? UpdateTag(
        SqliteConnection connection, string tagId, string? name, string? color, int expectedRevision, DateTime utcNow)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE tags SET name = COALESCE($name, name), color = COALESCE($color, color), revision = revision + 1, updated_utc = $u WHERE tag_id = $id AND revision = $rev";
            command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
            command.Parameters.AddWithValue("$u", utcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", tagId);
            command.Parameters.AddWithValue("$rev", expectedRevision);
            if (command.ExecuteNonQuery() == 0)
            {
                return null;
            }
        }

        return TryGetTag(connection, tagId)?.Revision;
    }

    /// <summary>删除标签，返回受影响的游戏列表（契约 note：remove 必须返回受影响游戏）。</summary>
    public static IReadOnlyList<string> RemoveTag(SqliteConnection connection, string tagId)
    {
        var affected = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT game_id FROM game_tags WHERE tag_id = $id ORDER BY game_id";
            select.Parameters.AddWithValue("$id", tagId);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                affected.Add(reader.GetString(0));
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tags WHERE tag_id = $id";
        command.Parameters.AddWithValue("$id", tagId);
        command.ExecuteNonQuery();
        return affected;
    }

    /// <summary>把标签挂到游戏：幂等；同时清除该标签的 Suppress 覆盖（显式加回即恢复自动语义）。</summary>
    public static bool AssignTag(SqliteConnection connection, string gameId, string tagId, DateTime utcNow)
    {
        var tag = TryGetTag(connection, tagId) ?? throw new InvalidOperationException("标签不存在");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT OR IGNORE INTO game_tags (game_id, tag_id, created_utc) VALUES ($g, $t, $u)";
            command.Parameters.AddWithValue("$g", gameId);
            command.Parameters.AddWithValue("$t", tagId);
            command.Parameters.AddWithValue("$u", utcNow.ToString("O", CultureInfo.InvariantCulture));
            if (command.ExecuteNonQuery() == 0)
            {
                ClearOverride(connection, gameId, tag.Kind, tag.Name, utcNow);
                return false;
            }
        }

        ClearOverride(connection, gameId, tag.Kind, tag.Name, utcNow);
        return true;
    }

    /// <summary>解除关联；返回标签 kind（自动标签解除时调用方应登记 Suppress）。</summary>
    public static string? UnassignTag(SqliteConnection connection, string gameId, string tagId)
    {
        var tag = TryGetTag(connection, tagId) ?? throw new InvalidOperationException("标签不存在");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_tags WHERE game_id = $g AND tag_id = $t";
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$t", tagId);
        command.ExecuteNonQuery();
        return tag.Kind;
    }

    public static void SetOverride(SqliteConnection connection, string gameId, string tagKind, string tagName, string action, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tag_overrides (game_id, tag_kind, tag_name, action, created_utc)
            VALUES ($g, $k, $n, $a, $u)
            ON CONFLICT(game_id, tag_kind, tag_name) DO UPDATE SET action = excluded.action
            """;
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$k", tagKind);
        command.Parameters.AddWithValue("$n", tagName);
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$u", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static bool ClearOverride(SqliteConnection connection, string gameId, string tagKind, string tagName, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tag_overrides WHERE game_id = $g AND tag_kind = $k AND tag_name = $n";
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$k", tagKind);
        command.Parameters.AddWithValue("$n", tagName);
        return command.ExecuteNonQuery() > 0;
    }

    public static bool IsSuppressed(SqliteConnection connection, string gameId, string tagKind, string tagName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tag_overrides WHERE game_id = $g AND tag_kind = $k AND tag_name = $n AND action = 'suppress'";
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$k", tagKind);
        command.Parameters.AddWithValue("$n", tagName);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>某游戏的标签名列表（kind:name），供游戏详情 DTO。</summary>
    public static IReadOnlyList<(string Kind, string Name)> ListGameTags(SqliteConnection connection, string gameId)
    {
        var result = new List<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.kind, t.name FROM game_tags gt
            JOIN tags t ON t.tag_id = gt.tag_id
            WHERE gt.game_id = $g ORDER BY t.kind, t.name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$g", gameId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>引擎标签缺失/被抑制时补齐（重扫与 reset 用）：不覆盖 Suppress 语义——被抑制则不恢复。</summary>
    public static bool EnsureEngineTagAssigned(SqliteConnection connection, string gameId, string engine, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return false;
        }

        if (IsSuppressed(connection, gameId, "engine", engine))
        {
            return false;
        }

        var tag = TryGetTagByName(connection, "engine", engine);
        if (tag is null)
        {
            tag = new PersistedTag(
                $"tag-{Guid.NewGuid():N}", "engine", engine, null, 1, 0, utcNow, utcNow);
            CreateTag(connection, tag);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO game_tags (game_id, tag_id, created_utc) VALUES ($g, $t, $u)";
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$t", tag.TagId);
        command.Parameters.AddWithValue("$u", utcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() > 0;
    }
}
