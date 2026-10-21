using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

public static partial class LibraryCatalogStore
{
    public static void InsertGame(SqliteConnection connection, GameCard game, SqliteTransaction? transaction = null, bool includePreferences = true)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = $"""
            INSERT INTO games
                (game_id, title, root_path, kind, engine, entry_path, membership, favorite,
                 translation_inherited, availability, missing_since_utc, revision, accepted_utc, updated_utc{(includePreferences ? ", played, rating" : "")})
            VALUES ($id, $title, $root, $kind, $engine, $entry, 'active', $favorite,
                    $inherited, $availability, $missing, 1, $accepted, $accepted{(includePreferences ? ", $played, $rating" : "")})
            """;
        command.Parameters.AddWithValue("$id", game.GameId);
        command.Parameters.AddWithValue("$title", game.Title);
        command.Parameters.AddWithValue("$root", game.RootPath);
        command.Parameters.AddWithValue("$kind", game.Kind);
        command.Parameters.AddWithValue("$engine", (object?)game.Engine ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry", (object?)game.EntryPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", game.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$played", game.Played ? 1 : 0);
        command.Parameters.AddWithValue("$rating", game.Rating);
        command.Parameters.AddWithValue("$inherited", game.TranslationInherited ? 1 : 0);
        command.Parameters.AddWithValue("$availability", game.Availability);
        command.Parameters.AddWithValue("$missing", game.MissingSinceUtc is { } missing
            ? missing.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.Parameters.AddWithValue("$accepted", game.AcceptedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>仅将游戏从可见库移除，并原子登记 ExactPath 忽略；绝不触碰游戏文件。</summary>
    public static int? RemoveGame(
        SqliteConnection connection, string gameId, int expectedRevision, IgnoreRule ignore, DateTime utcNow, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE games
                SET membership = 'removed', revision = revision + 1, updated_utc = $now
                WHERE game_id = $id AND membership = 'active' AND revision = $revision
                """;
            update.Parameters.AddWithValue("$id", gameId);
            update.Parameters.AddWithValue("$revision", expectedRevision);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            if (update.ExecuteNonQuery() != 1)
            {
                ownedTransaction?.Rollback();
                return null;
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ignore_rules (ignore_id, scope, path, game_id, reason, revision, created_utc)
                VALUES ($id, 'ExactPath', $path, $game, $reason, 1, $created)
                """;
            insert.Parameters.AddWithValue("$id", ignore.IgnoreId);
            insert.Parameters.AddWithValue("$path", ignore.Path!);
            insert.Parameters.AddWithValue("$game", ignore.GameId!);
            insert.Parameters.AddWithValue("$reason", ignore.Reason!);
            insert.Parameters.AddWithValue("$created", utcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        ownedTransaction?.Commit();
        return expectedRevision + 1;
    }

    public static GameCard? TryGetGame(SqliteConnection connection, string gameId, bool includePreferences = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
                {(includePreferences ? ", played, rating" : ", 0, 0")}
            FROM games WHERE game_id = $id
            """;
        command.Parameters.AddWithValue("$id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    public static IReadOnlyList<GameCard> ListGames(SqliteConnection connection, bool includePreferences = true)
    {
        var result = new List<GameCard>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
                {(includePreferences ? ", played, rating" : ", 0, 0")}
            FROM games ORDER BY accepted_utc, game_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadGame(reader));
        }

        return result;
    }

    public static IReadOnlyList<GameCard> ListUnknownEngineGames(SqliteConnection connection, string afterGameId, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc,
                translation_inherited, translation_override, availability, missing_since_utc, played, rating
            FROM games WHERE membership = 'active' AND (engine IS NULL OR trim(engine) = '' OR engine = 'unknown')
                AND game_id > $after ORDER BY game_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", afterGameId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var games = new List<GameCard>();
        while (reader.Read()) games.Add(ReadGame(reader));
        return games;
    }

    /// <summary>
    /// 数据库侧检索（阶段三：列表分页/检索下沉到 SQL）：搜索命中用户标题或原文件夹名、
    /// 收藏/封面/标签过滤（与搜索 AND 组合）、名称/修改时间/入库时间排序、LIMIT/OFFSET 分页。
    /// total 为过滤后的总数（分页前）。limit &lt;= 0 表示不分页（兼容旧全量语义）。
    /// </summary>
    public static (int Total, IReadOnlyList<GameCard> Items) QueryGames(
        SqliteConnection connection,
        string? search,
        bool favoriteOnly,
        string? tagId,
        string? sort,
        int limit,
        int offset,
        bool? hasCover = null,
        bool includePreferences = true,
        bool? played = null)
    {
        const string originalTitle = "COALESCE((SELECT COALESCE(value, '') FROM game_fields gf WHERE gf.game_id = games.game_id AND gf.field_key = 'title' ORDER BY CASE gf.source WHEN 'user' THEN 0 ELSE 1 END LIMIT 1), title)";
        var where = new List<string> { "membership = 'active'" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            // LIKE 通配符转义；中文按字节子串匹配（策划案：预归一化内存搜索的数据库侧等价）。
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            where.Add($"({originalTitle} LIKE $like ESCAPE '\\' OR root_path LIKE $like ESCAPE '\\' OR EXISTS (SELECT 1 FROM game_title_translations tt WHERE tt.game_id = games.game_id AND tt.translated_title LIKE $like ESCAPE '\\'))");
        }

        if (favoriteOnly)
        {
            where.Add("favorite = 1");
        }

        if (played is { } playedRequired)
        {
            where.Add($"{(includePreferences ? "played" : "0")} = {(playedRequired ? 1 : 0)}");
        }

        if (hasCover is { } coverRequired)
        {
            const string currentCover = "EXISTS (SELECT 1 FROM game_assets ga WHERE ga.game_id = games.game_id AND ga.is_current = 1)";
            where.Add(coverRequired ? currentCover : $"NOT {currentCover}");
        }

        if (!string.IsNullOrWhiteSpace(tagId))
        {
            where.Add("EXISTS (SELECT 1 FROM game_tags gt WHERE gt.game_id = games.game_id AND gt.tag_id = $tagId)");
        }

        var displayedTitle = $"COALESCE((SELECT translated_title FROM game_title_translations tt WHERE tt.game_id = games.game_id AND tt.display_mode = 'translated'), {originalTitle})";
        var orderBy = sort switch
        {
            "title-desc" => $"{displayedTitle} COLLATE NOCASE DESC, game_id",
            "recent" or "updated-desc" => "updated_utc DESC, game_id",
            "accepted-desc" => "accepted_utc DESC, game_id",
            _ => $"{displayedTitle} COLLATE NOCASE, game_id",
        };

        int total;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM games WHERE {string.Join(" AND ", where)}";
            BindQueryParameters(countCommand, search, tagId);
            total = Convert.ToInt32(countCommand.ExecuteScalar() ?? 0L);
        }

        var items = new List<GameCard>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
                    {(includePreferences ? ", played, rating" : ", 0, 0")}
                FROM games WHERE {string.Join(" AND ", where)}
                ORDER BY {orderBy}
                """ + (limit > 0 ? " LIMIT $limit OFFSET $offset" : "");
            BindQueryParameters(command, search, tagId);
            if (limit > 0)
            {
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadGame(reader));
            }
        }

        return (total, items);
    }

    private static void BindQueryParameters(SqliteCommand command, string? search, string? tagId)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue("$like", $"%{escaped}%");
        }

        if (!string.IsNullOrWhiteSpace(tagId))
        {
            command.Parameters.AddWithValue("$tagId", tagId);
        }
    }

    /// <summary>
    /// 批量充实（R41）：按 500 个 gameId 一组分三次查询取回字段/封面/标签，
    /// 取代 games.list 每游戏 4 次独立查询（各过一次存储锁）。
    /// 语义与单游戏路径一致：字段 user 来源优先、封面取 (imported_utc, asset_id)
    /// 序下第一条 is_current、标签按 (kind, name NOCASE) 排序。
    /// </summary>
    public static IReadOnlyDictionary<string, GameCardEnrichment> EnrichGameCards(
        SqliteConnection connection, IReadOnlyList<GameCard> games)
    {
        const int chunkSize = 500;
        var titles = new Dictionary<string, (string? Value, string Source)>(StringComparer.Ordinal);
        var summaries = new Dictionary<string, (string? Value, string Source)>(StringComparer.Ordinal);
        var covers = new Dictionary<string, string>(StringComparer.Ordinal);
        var translations = new Dictionary<string, GameTitleTranslation>(StringComparer.Ordinal);
        var tags = new Dictionary<string, List<(string Kind, string Name)>>(StringComparer.Ordinal);

        for (var chunkStart = 0; chunkStart < games.Count; chunkStart += chunkSize)
        {
            var ids = new string[Math.Min(chunkSize, games.Count - chunkStart)];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = games[chunkStart + i].GameId;
            }

            var idList = string.Join(", ", ids.Select((_, i) => $"$id{i}"));

            using (var aliases = connection.CreateCommand())
            {
                aliases.CommandText = $"SELECT game_id, translated_title, source_title, provider, translated_utc, manually_edited, display_mode FROM game_title_translations WHERE game_id IN ({idList})";
                BindIds(aliases, ids);
                using var reader = aliases.ExecuteReader();
                while (reader.Read()) translations[reader.GetString(0)] = SqliteLibraryStore.ReadTitleTranslationRow(reader, 1);
            }

            using (var fields = connection.CreateCommand())
            {
                fields.CommandText = $"""
                    SELECT game_id, field_key, value, source FROM game_fields
                    WHERE field_key IN ('title', 'summary') AND game_id IN ({idList})
                    ORDER BY game_id, field_key, CASE source WHEN 'user' THEN 0 ELSE 1 END
                    """;
                BindIds(fields, ids);
                using var reader = fields.ExecuteReader();
                while (reader.Read())
                {
                    var gameId = reader.GetString(0);
                    var entry = (reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
                    var target = reader.GetString(1) == "title" ? titles : summaries;
                    // 同键多行时保留优先级更高（user 先出现）的第一条。
                    if (!target.ContainsKey(gameId))
                    {
                        target[gameId] = entry;
                    }
                }
            }

            using (var assets = connection.CreateCommand())
            {
                assets.CommandText = $"""
                    SELECT game_id, asset_id, is_current FROM game_assets
                    WHERE game_id IN ({idList})
                    ORDER BY imported_utc, asset_id
                    """;
                BindIds(assets, ids);
                using var reader = assets.ExecuteReader();
                while (reader.Read())
                {
                    var gameId = reader.GetString(0);
                    if (reader.GetInt64(2) == 1 && !covers.ContainsKey(gameId))
                    {
                        covers[gameId] = reader.GetString(1);
                    }
                }
            }

            using (var gameTags = connection.CreateCommand())
            {
                gameTags.CommandText = $"""
                    SELECT gt.game_id, t.kind, t.name FROM game_tags gt
                    JOIN tags t ON t.tag_id = gt.tag_id
                    WHERE gt.game_id IN ({idList})
                    ORDER BY gt.game_id, t.kind, t.name COLLATE NOCASE
                    """;
                BindIds(gameTags, ids);
                using var reader = gameTags.ExecuteReader();
                while (reader.Read())
                {
                    var gameId = reader.GetString(0);
                    if (!tags.TryGetValue(gameId, out var list))
                    {
                        list = [];
                        tags[gameId] = list;
                    }

                    list.Add((reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        var result = new Dictionary<string, GameCardEnrichment>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            var title = titles.GetValueOrDefault(game.GameId, (Value: game.Title, Source: "auto"));
            var summary = summaries.GetValueOrDefault(game.GameId, (Value: "", Source: "auto"));
            result[game.GameId] = new GameCardEnrichment
            {
                Title = title.Value,
                TitleTranslation = translations.GetValueOrDefault(game.GameId),
                TitleSource = title.Source,
                Summary = summary.Value ?? "",
                SummarySource = summary.Source,
                CoverAssetId = covers.GetValueOrDefault(game.GameId),
                Tags = tags.GetValueOrDefault(game.GameId, []),
            };
        }

        return result;
    }

    private static void BindIds(SqliteCommand command, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }
    }

    /// <summary>
    /// 游玩统计聚合（feat-1）：SQL 侧按 game_id 聚合 launch_attempts——
    /// 只统计同时具备 process_started_utc 与 finished_utc 的 attempt（无起止时间的不计入，
    /// processStartFailed 只有 finished、未完成只有 started，天然被排除）；
    /// GROUP BY 保证同一 attempt（唯一行，UPSERT 语义）只计一次。
    /// julianday() 解析库内 ISO-8601 往返格式（含 'Z' 后缀与多位小数秒），差值 ×86400 得秒；
    /// 先 ROUND 到整秒再 CAST——julianday 内部是毫秒整数，转 double 后 CAST 截断会丢整秒
    /// （如 30 分钟变 1799.999…→1799）。
    /// 查询走既有 idx_launch_attempts_game(game_id, created_utc)。
    /// </summary>
    public static IReadOnlyDictionary<string, PlaytimeStats> QueryPlaytimeStats(
        SqliteConnection connection, IReadOnlyCollection<string> gameIds)
    {
        var result = new Dictionary<string, PlaytimeStats>(StringComparer.Ordinal);
        const int chunkSize = 500;
        var ids = gameIds.Distinct(StringComparer.Ordinal).ToArray();
        for (var chunkStart = 0; chunkStart < ids.Length; chunkStart += chunkSize)
        {
            var chunk = ids.Skip(chunkStart).Take(chunkSize).ToArray();
            var idList = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT game_id,
                       SUM(CAST(ROUND((julianday(finished_utc) - julianday(process_started_utc)) * 86400.0) AS INTEGER)) AS total_seconds,
                       MAX(finished_utc) AS last_finished
                FROM launch_attempts
                WHERE game_id IN ({idList})
                  AND process_started_utc IS NOT NULL
                  AND finished_utc IS NOT NULL
                GROUP BY game_id
                """;
            BindIds(command, chunk);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var totalSeconds = Convert.ToInt64(reader.GetValue(1) ?? 0L);
                var minutes = Math.Max(0, totalSeconds / 60);
                DateTime? lastFinished = reader.IsDBNull(2)
                    ? null
                    : DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                result[reader.GetString(0)] = new PlaytimeStats(minutes, lastFinished);
            }
        }

        return result;
    }

    public static GameCard? TryGetGameByRootPath(SqliteConnection connection, string rootPath, SqliteTransaction? transaction = null,
        bool includeRemoved = false, bool includePreferences = true)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = $"""
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
                {(includePreferences ? ", played, rating" : ", 0, 0")}
            FROM games WHERE root_path = $root AND ($includeRemoved = 1 OR membership = 'active')
            ORDER BY CASE membership WHEN 'active' THEN 0 ELSE 1 END, updated_utc DESC, game_id LIMIT 1
            """;
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$includeRemoved", includeRemoved ? 1 : 0);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    /// <summary>收藏切换（games.update 受限字段）：期望 Revision 对齐 games.revision，事务内更新并递增；冲突返回 null。</summary>
    public static int? SetFavorite(SqliteConnection connection, string gameId, bool favorite, int expectedRevision, DateTime utcNow, SqliteTransaction? parentTransaction = null, bool includePreferences = true)
        => SetGamePreferences(connection, gameId, favorite, null, null, expectedRevision, utcNow, parentTransaction, includePreferences);

    public static int? SetGamePreferences(SqliteConnection connection, string gameId, bool? favorite, bool? played, int? rating,
        int expectedRevision, DateTime utcNow, SqliteTransaction? transaction = null, bool includePreferences = true)
    {
        if (rating is < 0 or > 5 || favorite is null && played is null && rating is null)
            throw new ArgumentException("至少提供一项游戏状态；评分必须为 0–5");
        if (!includePreferences && (played is not null || rating is not null))
            throw new InvalidOperationException("请先升级游戏库以保存已游玩状态和评分");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE games SET favorite = coalesce($favorite, favorite)
                {(includePreferences ? ", played = coalesce($played, played), rating = coalesce($rating, rating)" : "")},
                revision = revision + 1, updated_utc = $now
            WHERE game_id = $game AND revision = $revision
            """;
        command.Parameters.AddWithValue("$favorite", favorite is { } f ? f ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$played", played is { } p ? p ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$game", gameId);
        return command.ExecuteNonQuery() == 1 ? expectedRevision + 1 : null;
    }

    public static bool SetUnknownGameEngine(SqliteConnection connection, string gameId, string engine, int expectedRevision,
        DateTime utcNow, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE games SET engine = $engine, revision = revision + 1, updated_utc = $now
            WHERE game_id = $game AND membership = 'active' AND revision = $revision
                AND (engine IS NULL OR trim(engine) = '' OR engine = 'unknown')
            """;
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$engine", engine);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() != 1) return false;
        TagStore.EnsureEngineTagAssigned(connection, gameId, engine, utcNow, transaction);
        return true;
    }

    /// <summary>
    /// 翻译策略用户覆盖（translation.set）：仅写 translation_override 列，继承列不动。
    /// 期望 Revision 对齐 games.revision，事务内更新并递增；游戏不存在或冲突返回 null。
    /// </summary>
    public static int? SetTranslationOverride(
        SqliteConnection connection, string gameId, string? overrideValue, int expectedRevision, DateTime utcNow, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                ownedTransaction?.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            ownedTransaction?.Rollback();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE games SET translation_override = $override, revision = revision + 1, updated_utc = $now
                WHERE game_id = $game
                """;
            update.Parameters.AddWithValue("$override", (object?)overrideValue ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$game", gameId);
            update.ExecuteNonQuery();
        }

        ownedTransaction?.Commit();
        return currentRevision + 1;
    }

    /// <summary>
    /// 可用性写回（T17 核对）：扫描器维护字段，不递增 Revision（避免与用户编辑互相踩踏）；
    /// 返回是否发生了状态迁移，供事件发布判断。
    /// </summary>
    public static bool UpdateAvailability(
        SqliteConnection connection, string gameId, string availability, DateTime? missingSinceUtc, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE games SET availability = $availability, missing_since_utc = $missingSince, updated_utc = $now
            WHERE game_id = $id
            """;
        command.Parameters.AddWithValue("$availability", availability);
        command.Parameters.AddWithValue("$missingSince", missingSinceUtc is null ? DBNull.Value : missingSinceUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", gameId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 重关联（T17 games.relink）：仅改数据库路径绑定，不移动磁盘文件。
    /// 期望 Revision 对齐（乐观并发），事务内更新 root_path 并重置可用性；游戏不存在或冲突返回 null。
    /// </summary>
    public static int? RelinkGame(SqliteConnection connection, string gameId, string newRootPath, int expectedRevision, DateTime utcNow, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                ownedTransaction?.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            ownedTransaction?.Rollback();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE games
                SET root_path = $root, availability = 'available', missing_since_utc = NULL,
                    revision = revision + 1, updated_utc = $now
                WHERE game_id = $game
                """;
            update.Parameters.AddWithValue("$root", newRootPath);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$game", gameId);
            update.ExecuteNonQuery();
        }

        ownedTransaction?.Commit();
        return currentRevision + 1;
    }

    private static PersistedCandidate ReadCandidate(SqliteDataReader reader) => new()
    {
        CandidateId = reader.GetString(0),
        JobId = reader.IsDBNull(1) ? null : reader.GetString(1),
        Kind = reader.GetString(2),
        RelativePath = reader.GetString(3),
        PhysicalPath = reader.GetString(4),
        PayloadJson = reader.GetString(5),
        ReviewState = reader.GetString(6),
        Revision = reader.GetInt32(7),
        GameId = reader.IsDBNull(8) ? null : reader.GetString(8),
        ObservedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };

    private static GameCard ReadGame(SqliteDataReader reader) => new()
    {
        GameId = reader.GetString(0),
        Title = reader.GetString(1),
        RootPath = reader.GetString(2),
        Kind = reader.GetString(3),
        Engine = reader.IsDBNull(4) ? null : reader.GetString(4),
        EntryPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        Membership = reader.GetString(6),
        Favorite = reader.GetInt64(7) == 1,
        Played = reader.GetInt64(15) == 1,
        Rating = reader.GetInt32(16),
        Revision = reader.GetInt32(8),
        AcceptedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        TranslationInherited = reader.GetInt64(11) == 1,
        TranslationOverride = reader.IsDBNull(12) ? null : reader.GetString(12),
        Availability = reader.GetString(13),
        MissingSinceUtc = reader.IsDBNull(14)
            ? null
            : DateTime.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
