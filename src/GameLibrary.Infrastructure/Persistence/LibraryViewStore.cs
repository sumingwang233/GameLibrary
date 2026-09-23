using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 自定义视图（T15-C）：搜索/筛选/排序的语义状态（契约 3.1 views.*）。
/// 内置视图（all/favorites/pending）由 BuiltInViews 代码定义，不入库。
/// filter_json 形如 {"search":"…","favoriteOnly":true,"tagId":"…"}；字段未出现即不启用该条件。
/// </summary>
public sealed record LibraryView
{
    public required string ViewId { get; init; }

    public required string Name { get; init; }

    public string? Search { get; init; }

    public bool FavoriteOnly { get; init; }

    /// <summary>可选标签筛选；保存收藏夹时与搜索、收藏条件一起持久化。</summary>
    public string? TagId { get; init; }

    /// <summary>排序值；白名单与 games.list 一致：title/title-asc/title-desc/recent/updated-desc/accepted-desc（迁移 v21 起 CHECK 放宽为六值）。</summary>
    public string Sort { get; init; } = "title";

    public int Revision { get; init; } = 1;

    public required DateTime CreatedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }

    public static string SerializeFilter(LibraryView view)
    {
        var parts = new List<string>();
        if (view.Search is not null)
        {
            parts.Add($"\"search\":{JsonEncode(view.Search)}");
        }

        if (view.FavoriteOnly)
        {
            parts.Add("\"favoriteOnly\":true");
        }

        if (!string.IsNullOrWhiteSpace(view.TagId))
        {
            parts.Add($"\"tagId\":{JsonEncode(view.TagId)}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    public static (string? Search, bool FavoriteOnly, string? TagId) ParseFilter(string filterJson)
    {
        string? search = null;
        var favoriteOnly = false;
        string? tagId = null;
        try
        {
            using var document = JsonDocument.Parse(filterJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("search", out var searchElement)
                    && searchElement.ValueKind == JsonValueKind.String)
                {
                    search = searchElement.GetString();
                }

                if (document.RootElement.TryGetProperty("favoriteOnly", out var favoriteElement)
                    && favoriteElement.ValueKind == JsonValueKind.True)
                {
                    favoriteOnly = true;
                }

                if (document.RootElement.TryGetProperty("tagId", out var tagElement)
                    && tagElement.ValueKind == JsonValueKind.String)
                {
                    tagId = tagElement.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // 损坏 filter 按空视图条件处理，不阻塞列表。
        }

        return (search, favoriteOnly, tagId);
    }

    private static string JsonEncode(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}

public static class LibraryViewStore
{
    public static void InsertView(SqliteConnection connection, LibraryView view)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_views (view_id, name, filter_json, sort, revision, created_utc, updated_utc)
            VALUES ($id, $name, $filter, $sort, 1, $created, $created)
            """;
        command.Parameters.AddWithValue("$id", view.ViewId);
        command.Parameters.AddWithValue("$name", view.Name);
        command.Parameters.AddWithValue("$filter", LibraryView.SerializeFilter(view));
        command.Parameters.AddWithValue("$sort", view.Sort);
        command.Parameters.AddWithValue("$created", view.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static LibraryView? TryGetView(SqliteConnection connection, string viewId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT view_id, name, filter_json, sort, revision, created_utc, updated_utc FROM library_views WHERE view_id = $id";
        command.Parameters.AddWithValue("$id", viewId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadView(reader) : null;
    }

    public static IReadOnlyList<LibraryView> ListViews(SqliteConnection connection)
    {
        var result = new List<LibraryView>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT view_id, name, filter_json, sort, revision, created_utc, updated_utc FROM library_views ORDER BY created_utc, view_id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadView(reader));
        }

        return result;
    }

    /// <summary>受限 patch：期望 Revision 对齐，事务内更新并递增；视图不存在或冲突返回 null。未提供的字段保持不变。</summary>
    public static int? UpdateView(
        SqliteConnection connection, string viewId, string? name, string? search, bool? favoriteOnly,
        string? tagId, string? sort, int expectedRevision, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        string currentFilter;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision, filter_json FROM library_views WHERE view_id = $id";
            select.Parameters.AddWithValue("$id", viewId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = reader.GetInt32(0);
            currentFilter = reader.GetString(1);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
            return null;
        }

        var (existingSearch, existingFavoriteOnly, existingTagId) = LibraryView.ParseFilter(currentFilter);
        var filterChanged = search is not null || favoriteOnly is not null || tagId is not null;
        var filterJson = filterChanged
            ? LibraryView.SerializeFilter(new LibraryView
            {
                ViewId = viewId,
                Name = string.Empty,
                Search = search ?? existingSearch,
                FavoriteOnly = favoriteOnly ?? existingFavoriteOnly,
                TagId = tagId ?? existingTagId,
                CreatedUtc = default,
                UpdatedUtc = default,
            })
            : currentFilter;

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE library_views
                SET name = COALESCE($name, name), filter_json = $filter, sort = COALESCE($sort, sort),
                    revision = revision + 1, updated_utc = $now
                WHERE view_id = $id
                """;
            update.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            update.Parameters.AddWithValue("$filter", filterJson);
            update.Parameters.AddWithValue("$sort", (object?)sort ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", viewId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return currentRevision + 1;
    }

    /// <summary>删除自定义视图；视图不存在返回 false。</summary>
    public static bool DeleteView(SqliteConnection connection, string viewId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_views WHERE view_id = $id";
        command.Parameters.AddWithValue("$id", viewId);
        return command.ExecuteNonQuery() > 0;
    }

    private static LibraryView ReadView(SqliteDataReader reader)
    {
        var (search, favoriteOnly, tagId) = LibraryView.ParseFilter(reader.GetString(2));
        return new LibraryView
        {
            ViewId = reader.GetString(0),
            Name = reader.GetString(1),
            Search = search,
            FavoriteOnly = favoriteOnly,
            TagId = tagId,
            Sort = reader.GetString(3),
            Revision = reader.GetInt32(4),
            CreatedUtc = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }
}
