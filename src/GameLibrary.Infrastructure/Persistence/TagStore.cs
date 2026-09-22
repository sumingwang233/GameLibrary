using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 持久化标签行（含游戏计数，供 tags.list）。
/// v22 起新增 category/sortOrder/starred/displayName 四字段（feat-3）：
/// category 按 kind 回填（engine→engine、user→special）；display_name 承载 engine
/// 标签的用户改名（name 是身份键，扫描识别与 Suppress 覆盖均以其匹配）。
/// </summary>
public sealed record PersistedTag(
    string TagId,
    string Kind,
    string Name,
    string? Color,
    int Revision,
    int GameCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string Category = "special",
    int SortOrder = 0,
    int Starred = 0,
    string? DisplayName = null);

/// <summary>
/// 标签存储（T-collections，迁移 v18）：标签定义按 (类型, 规范值) 唯一——
/// engine 标签来自引擎识别（入库时自动创建，重扫只替换同源记录），
/// user 标签由用户创建；tag_overrides 记录 Suppress（阻止扫描恢复用户删除的自动标签）
/// 与 ForceAdd（预留）。游戏↔标签关联随游戏删除级联清理。
/// </summary>
public static class TagStore
{
    /// <summary>公共列清单（v22 含四新列）+ 末位游戏计数；读取统一走 <see cref="ReadTag"/>。</summary>
    private const string SelectColumns = """
        t.tag_id, t.kind, t.name, t.color, t.revision, t.created_utc, t.updated_utc,
        t.category, t.sort_order, t.starred, t.display_name
        """;

    public static IReadOnlyList<PersistedTag> ListTags(SqliteConnection connection)
    {
        var result = new List<PersistedTag>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns},
                   (SELECT COUNT(*) FROM game_tags gt
                    JOIN games g ON g.game_id = gt.game_id
                    WHERE gt.tag_id = t.tag_id AND g.membership = 'active') AS game_count
            FROM tags t
            ORDER BY t.kind, t.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadTag(reader));
        }

        return result;
    }

    public static PersistedTag? TryGetTag(SqliteConnection connection, string tagId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns},
                   (SELECT COUNT(*) FROM game_tags gt
                    JOIN games g ON g.game_id = gt.game_id
                    WHERE gt.tag_id = t.tag_id AND g.membership = 'active')
            FROM tags t WHERE t.tag_id = $id
            """;
        command.Parameters.AddWithValue("$id", tagId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadTag(reader);
    }

    public static PersistedTag? TryGetTagByName(
        SqliteConnection connection, string kind, string name, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = $"""
            SELECT {SelectColumns},
                   (SELECT COUNT(*) FROM game_tags gt
                    JOIN games g ON g.game_id = gt.game_id
                    WHERE gt.tag_id = t.tag_id AND g.membership = 'active')
            FROM tags t WHERE t.kind = $kind AND t.name = $name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadTag(reader);
    }

    /// <summary>统一行读取：0–10 为 <see cref="SelectColumns"/> 顺序，11 为游戏计数。</summary>
    private static PersistedTag ReadTag(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(11),
        DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    public static void CreateTag(SqliteConnection connection, PersistedTag tag, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = """
            INSERT INTO tags (tag_id, kind, name, color, category, sort_order, starred, display_name, revision, created_utc, updated_utc)
            VALUES ($id, $kind, $name, $color, $category, $sortOrder, $starred, $displayName, $rev, $created, $updated)
            """;
        command.Parameters.AddWithValue("$id", tag.TagId);
        command.Parameters.AddWithValue("$kind", tag.Kind);
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", (object?)tag.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", tag.Category);
        command.Parameters.AddWithValue("$sortOrder", tag.SortOrder);
        command.Parameters.AddWithValue("$starred", tag.Starred);
        command.Parameters.AddWithValue("$displayName", (object?)tag.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rev", tag.Revision);
        command.Parameters.AddWithValue("$created", tag.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", tag.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 受限 patch 更新（feat-3）：仅拼接显式提供的字段；期望修订乐观校验，
    /// 返回新修订，冲突返回 null。display_name 特殊——值非 null 即写入、
    /// <paramref name="clearDisplayName"/> 为 true 时置 NULL（回落 name）。
    /// name 仅允许 user 标签传值（engine 身份键不可变由 Handler 层把关）。
    /// 空 patch 与旧行为一致：修订对齐即递增修订，不做其他改动。
    /// </summary>
    public static int? UpdateTag(
        SqliteConnection connection,
        string tagId,
        string? name,
        string? color,
        string? category,
        int? sortOrder,
        int? starred,
        string? displayName,
        bool clearDisplayName,
        int expectedRevision,
        DateTime utcNow)
    {
        var sets = new List<string>();
        if (name is not null)
        {
            sets.Add("name = $name");
        }

        if (color is not null)
        {
            sets.Add("color = $color");
        }

        if (category is not null)
        {
            sets.Add("category = $category");
        }

        if (sortOrder is not null)
        {
            sets.Add("sort_order = $sortOrder");
        }

        if (starred is not null)
        {
            sets.Add("starred = $starred");
        }

        if (clearDisplayName)
        {
            sets.Add("display_name = NULL");
        }
        else if (displayName is not null)
        {
            sets.Add("display_name = $displayName");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                UPDATE tags SET {string.Join(", ", sets)}, revision = revision + 1, updated_utc = $u
                WHERE tag_id = $id AND revision = $rev
                """;
            if (name is not null)
            {
                command.Parameters.AddWithValue("$name", name);
            }

            if (color is not null)
            {
                command.Parameters.AddWithValue("$color", color);
            }

            if (category is not null)
            {
                command.Parameters.AddWithValue("$category", category);
            }

            if (sortOrder is not null)
            {
                command.Parameters.AddWithValue("$sortOrder", sortOrder.Value);
            }

            if (starred is not null)
            {
                command.Parameters.AddWithValue("$starred", starred.Value);
            }

            if (!clearDisplayName && displayName is not null)
            {
                command.Parameters.AddWithValue("$displayName", displayName);
            }

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

    public static bool IsSuppressed(
        SqliteConnection connection, string gameId, string tagKind, string tagName, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
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
    public static bool EnsureEngineTagAssigned(
        SqliteConnection connection, string gameId, string engine, DateTime utcNow, SqliteTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return false;
        }

        if (IsSuppressed(connection, gameId, "engine", engine, transaction))
        {
            return false;
        }

        var tag = TryGetTagByName(connection, "engine", engine, transaction);
        if (tag is null)
        {
            // feat-3：新增 engine 标签 category 固定 'engine'（迁移 v22 存量同规则回填）。
            tag = new PersistedTag(
                $"tag-{Guid.NewGuid():N}", "engine", engine, null, 1, 0, utcNow, utcNow,
                Category: "engine");
            CreateTag(connection, tag, transaction);
        }

        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = "INSERT OR IGNORE INTO game_tags (game_id, tag_id, created_utc) VALUES ($g, $t, $u)";
        command.Parameters.AddWithValue("$g", gameId);
        command.Parameters.AddWithValue("$t", tag.TagId);
        command.Parameters.AddWithValue("$u", utcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() > 0;
    }
}
