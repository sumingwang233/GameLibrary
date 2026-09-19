using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>落库候选（T11）：payload 保留完整检测证据；审核状态与 Revision 在库内演进。</summary>
public sealed record PersistedCandidate
{
    public required string CandidateId { get; init; }

    public string? JobId { get; init; }

    public required string Kind { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>规范化物理路径；唯一键（重扫刷新同一候选，不重复建卡）。</summary>
    public required string PhysicalPath { get; init; }

    public required string PayloadJson { get; init; }

    public required string ReviewState { get; init; }

    public int Revision { get; init; } = 1;

    public string? GameId { get; init; }

    public required DateTime ObservedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

/// <summary>游戏卡片（T11 最小集）：accept 创建；资料/封面/标签编辑随 T14。</summary>
public sealed record GameCard
{
    public required string GameId { get; init; }

    public required string Title { get; init; }

    public required string RootPath { get; init; }

    public required string Kind { get; init; }

    public string? Engine { get; init; }

    public string? EntryPath { get; init; }

    public required string Membership { get; init; }

    public bool Favorite { get; init; }

    /// <summary>祖先 [toolNeed] 继承的 Required（accept 时落库；T17 对账后更新）。</summary>
    public bool TranslationInherited { get; init; }

    /// <summary>用户覆盖：Auto/Required/NotRequired 或 null（=Auto 未覆盖）。继承值与覆盖值分离持久化。</summary>
    public string? TranslationOverride { get; init; }

    /// <summary>路径可用性（T17）：unknown/available/suspectedMissing/missing/offline/accessError/rootUnbound。扫描器维护，不占 Revision。</summary>
    public string Availability { get; init; } = "unknown";

    /// <summary>第一次确认缺失的时间（suspectedMissing/missing 期间非空）；ID-05 的 60 秒间隔判定依据。</summary>
    public DateTime? MissingSinceUtc { get; init; }

    public int Revision { get; init; } = 1;

    public required DateTime AcceptedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

/// <summary>内置视图（T15）：视图自定义随 T15-C。</summary>
public static class BuiltInViews
{
    public static readonly IReadOnlyList<(string ViewId, string Name)> All =
    [
        ("all", "全部游戏"),
        ("favorites", "收藏"),
        ("pending", "待审核候选"),
    ];
}

/// <summary>忽略规则（T11）：scope 默认 ExactPath；撤销匹配规则是恢复候选提示的唯一途径。</summary>
public sealed record IgnoreRule
{
    public required string IgnoreId { get; init; }

    public required string Scope { get; init; }

    public string? Path { get; init; }

    public string? GameId { get; init; }

    public string? Reason { get; init; }

    public int Revision { get; init; } = 1;

    public required DateTime CreatedUtc { get; init; }
}

/// <summary>列表页充实数据（games.list 批量取回，等价于逐游戏 EffectiveField×2 + ListAssets + ListGameTags）。</summary>
public sealed record GameCardEnrichment
{
    public required string? Title { get; init; }

    public required string TitleSource { get; init; }

    public required string Summary { get; init; }

    public required string SummarySource { get; init; }

    public required string? CoverAssetId { get; init; }

    public required IReadOnlyList<(string Kind, string Name)> Tags { get; init; }
}

/// <summary>accept 结果：accepted=本次转移成功；alreadyAccepted=幂等重放（不写任何表）；conflict=状态或 Revision 不符。</summary>
public sealed record AcceptCandidateOutcome
{
    public required string Status { get; init; }

    public required PersistedCandidate Candidate { get; init; }

    public required string GameId { get; init; }
}

/// <summary>ignore 结果：ignored=本次成功；conflict=状态或 Revision 不符（不落任何写）。</summary>
public sealed record IgnoreCandidateOutcome
{
    public required string Status { get; init; }

    public required PersistedCandidate Candidate { get; init; }

    public required string IgnoreId { get; init; }
}

/// <summary>
/// 入库/忽略目录存储（T11）：候选按物理路径 upsert；审核转移由 Domain 状态机校验后在此落库。
/// Host 是唯一连接所有者；方法为同步 SQLite 调用，由宿主单写入语义串行化。
/// </summary>
public static class LibraryCatalogStore
{
    /// <summary>按物理路径 upsert；返回 true 表示候选已存在（本次为重扫刷新）。</summary>
    public static bool UpsertCandidate(SqliteConnection connection, PersistedCandidate candidate)
    {
        var existed = CandidateExists(connection, candidate.PhysicalPath);
        using var command = connection.CreateCommand();
        command.CommandText = existed
            ? """
                UPDATE candidates
                SET payload_json = $payload, job_id = $job, updated_utc = $observed
                WHERE physical_path = $phys
                """
            : """
                INSERT INTO candidates
                    (candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                     review_state, revision, game_id, observed_utc, updated_utc)
                VALUES ($id, $job, $kind, $rel, $phys, $payload, $state, 1, NULL, $observed, $observed)
                """;
        command.Parameters.AddWithValue("$id", candidate.CandidateId);
        command.Parameters.AddWithValue("$job", (object?)candidate.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", candidate.Kind);
        command.Parameters.AddWithValue("$rel", candidate.RelativePath);
        command.Parameters.AddWithValue("$phys", candidate.PhysicalPath);
        command.Parameters.AddWithValue("$payload", candidate.PayloadJson);
        command.Parameters.AddWithValue("$state", candidate.ReviewState);
        command.Parameters.AddWithValue("$observed", candidate.ObservedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return existed;
    }

    public static bool CandidateExists(SqliteConnection connection, string physicalPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM candidates WHERE physical_path = $phys";
        command.Parameters.AddWithValue("$phys", physicalPath);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>重扫命中既有 Observed 候选：Observed→Stabilizing→PendingReview（合法双跳）。</summary>
    public static void PromoteRescannedCandidate(SqliteConnection connection, string physicalPath, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE candidates
            SET review_state = 'pendingReview', revision = revision + 1, updated_utc = $now
            WHERE physical_path = $phys AND review_state = 'observed'
            """;
        command.Parameters.AddWithValue("$phys", physicalPath);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static PersistedCandidate? TryGetCandidate(SqliteConnection connection, string candidateId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                   review_state, revision, game_id, observed_utc, updated_utc
            FROM candidates WHERE candidate_id = $id
            """;
        command.Parameters.AddWithValue("$id", candidateId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCandidate(reader) : null;
    }

    public static IReadOnlyList<PersistedCandidate> ListCandidates(SqliteConnection connection)
    {
        var result = new List<PersistedCandidate>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                   review_state, revision, game_id, observed_utc, updated_utc
            FROM candidates ORDER BY observed_utc, candidate_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadCandidate(reader));
        }

        return result;
    }

    /// <summary>
    /// 按作业/审核状态筛选候选；total 为分页前的匹配总数。
    /// limit &lt;= 0 保持原有全量语义。
    /// </summary>
    public static (int Total, IReadOnlyList<PersistedCandidate> Items) QueryCandidates(
        SqliteConnection connection,
        string? jobId,
        string? state,
        int limit,
        int offset)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            where.Add("job_id = $jobId");
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            where.Add("review_state = $state");
        }

        var whereClause = where.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", where)}";
        int total;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM candidates{whereClause}";
            BindCandidateQueryParameters(countCommand, jobId, state);
            total = Convert.ToInt32(countCommand.ExecuteScalar() ?? 0L);
        }

        var items = new List<PersistedCandidate>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                       review_state, revision, game_id, observed_utc, updated_utc
                FROM candidates{whereClause}
                ORDER BY observed_utc, candidate_id
                """ + (limit > 0 ? " LIMIT $limit OFFSET $offset" : string.Empty);
            BindCandidateQueryParameters(command, jobId, state);
            if (limit > 0)
            {
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadCandidate(reader));
            }
        }

        return (total, items);
    }

    private static void BindCandidateQueryParameters(SqliteCommand command, string? jobId, string? state)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            command.Parameters.AddWithValue("$jobId", jobId);
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            command.Parameters.AddWithValue("$state", state);
        }
    }

    /// <summary>
    /// 审核转移（accept/defer/ignore）：期望 Revision 一致且状态机允许才提交，
    /// 返回更新后的候选；Revision 不一致返回 null（RevisionConflict 由调用方映射）。
    /// </summary>
    public static PersistedCandidate? TransitionCandidate(
        SqliteConnection connection,
        string candidateId,
        string fromState,
        string toState,
        int expectedRevision,
        string? gameId,
        DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        PersistedCandidate? current;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                       review_state, revision, game_id, observed_utc, updated_utc
                FROM candidates WHERE candidate_id = $id
                """;
            select.Parameters.AddWithValue("$id", candidateId);
            using var reader = select.ExecuteReader();
            current = reader.Read() ? ReadCandidate(reader) : null;
        }

        if (current is null
            || !string.Equals(current.ReviewState, fromState, StringComparison.Ordinal)
            || current.Revision != expectedRevision)
        {
            transaction.Rollback();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE candidates
                SET review_state = $state, revision = revision + 1, game_id = COALESCE($game, game_id), updated_utc = $now
                WHERE candidate_id = $id
                """;
            update.Parameters.AddWithValue("$state", toState);
            update.Parameters.AddWithValue("$game", (object?)gameId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", candidateId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return current with
        {
            ReviewState = toState,
            Revision = current.Revision + 1,
            GameId = gameId ?? current.GameId,
            UpdatedUtc = utcNow,
        };
    }

    /// <summary>
    /// accept 原子化（R43）：既有卡复用/建新卡 + 引擎自动标签 + 候选转移在单事务内提交。
    /// 此前三步各自成事务，中间失败会留下孤儿游戏卡或悬挂候选；conflict 时本方法不落任何写。
    /// </summary>
    public static AcceptCandidateOutcome AcceptCandidate(
        SqliteConnection connection,
        string candidateId,
        int expectedRevision,
        GameCard newGame,
        string engine,
        DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        PersistedCandidate current;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                       review_state, revision, game_id, observed_utc, updated_utc
                FROM candidates WHERE candidate_id = $id
                """;
            select.Parameters.AddWithValue("$id", candidateId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                throw new InvalidOperationException($"候选不存在：{candidateId}（调用方应先校验）");
            }

            current = ReadCandidate(reader);
        }

        if (current.ReviewState == "accepted" && current.GameId is not null)
        {
            transaction.Rollback();
            return new AcceptCandidateOutcome
            {
                Status = "alreadyAccepted",
                Candidate = current,
                GameId = current.GameId,
            };
        }

        if (current.ReviewState != "pendingReview" || current.Revision != expectedRevision)
        {
            transaction.Rollback();
            return new AcceptCandidateOutcome
            {
                Status = "conflict",
                Candidate = current,
                GameId = current.GameId ?? "",
            };
        }

        string gameId;
        var existing = TryGetGameByRootPath(connection, current.PhysicalPath, transaction);
        if (existing is not null)
        {
            gameId = existing.GameId;
        }
        else
        {
            gameId = newGame.GameId;
            InsertGame(connection, newGame, transaction);
        }

        TagStore.EnsureEngineTagAssigned(connection, gameId, engine, utcNow, transaction);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE candidates
                SET review_state = 'accepted', revision = revision + 1, game_id = $game, updated_utc = $now
                WHERE candidate_id = $id
                """;
            update.Parameters.AddWithValue("$game", gameId);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", candidateId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AcceptCandidateOutcome
        {
            Status = "accepted",
            Candidate = current with
            {
                ReviewState = "accepted",
                Revision = current.Revision + 1,
                GameId = gameId,
                UpdatedUtc = utcNow,
            },
            GameId = gameId,
        };
    }

    /// <summary>
    /// ignore 原子化（R48）：忽略规则 + 候选转移在单事务内提交。
    /// 此前两步各自成事务，中间失败会留下没有生效对象的规则；conflict 时本方法不落任何写。
    /// </summary>
    public static IgnoreCandidateOutcome IgnoreCandidate(
        SqliteConnection connection,
        string candidateId,
        int expectedRevision,
        IgnoreRule rule,
        DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        PersistedCandidate current;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json,
                       review_state, revision, game_id, observed_utc, updated_utc
                FROM candidates WHERE candidate_id = $id
                """;
            select.Parameters.AddWithValue("$id", candidateId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                throw new InvalidOperationException($"候选不存在：{candidateId}（调用方应先校验）");
            }

            current = ReadCandidate(reader);
        }

        if (current.ReviewState != "pendingReview" || current.Revision != expectedRevision)
        {
            transaction.Rollback();
            return new IgnoreCandidateOutcome
            {
                Status = "conflict",
                Candidate = current,
                IgnoreId = "",
            };
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ignore_rules (ignore_id, scope, path, game_id, reason, revision, created_utc)
                VALUES ($id, $scope, $path, $game, $reason, 1, $created)
                """;
            insert.Parameters.AddWithValue("$id", rule.IgnoreId);
            insert.Parameters.AddWithValue("$scope", rule.Scope);
            insert.Parameters.AddWithValue("$path", (object?)rule.Path ?? DBNull.Value);
            insert.Parameters.AddWithValue("$game", (object?)rule.GameId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$reason", (object?)rule.Reason ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", rule.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE candidates
                SET review_state = 'ignored', revision = revision + 1, updated_utc = $now
                WHERE candidate_id = $id
                """;
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", candidateId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return new IgnoreCandidateOutcome
        {
            Status = "ignored",
            Candidate = current with
            {
                ReviewState = "ignored",
                Revision = current.Revision + 1,
                UpdatedUtc = utcNow,
            },
            IgnoreId = rule.IgnoreId,
        };
    }

    public static void InsertGame(SqliteConnection connection, GameCard game, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = """
            INSERT INTO games
                (game_id, title, root_path, kind, engine, entry_path, membership, favorite, translation_inherited, revision, accepted_utc, updated_utc)
            VALUES ($id, $title, $root, $kind, $engine, $entry, 'active', $favorite, $inherited, 1, $accepted, $accepted)
            """;
        command.Parameters.AddWithValue("$id", game.GameId);
        command.Parameters.AddWithValue("$title", game.Title);
        command.Parameters.AddWithValue("$root", game.RootPath);
        command.Parameters.AddWithValue("$kind", game.Kind);
        command.Parameters.AddWithValue("$engine", (object?)game.Engine ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry", (object?)game.EntryPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", game.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$inherited", game.TranslationInherited ? 1 : 0);
        command.Parameters.AddWithValue("$accepted", game.AcceptedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>仅将游戏从可见库移除，并原子登记 ExactPath 忽略；绝不触碰游戏文件。</summary>
    public static int? RemoveGame(
        SqliteConnection connection, string gameId, int expectedRevision, IgnoreRule ignore, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
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
                transaction.Rollback();
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

        transaction.Commit();
        return expectedRevision + 1;
    }

    public static GameCard? TryGetGame(SqliteConnection connection, string gameId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
            FROM games WHERE game_id = $id
            """;
        command.Parameters.AddWithValue("$id", gameId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    public static IReadOnlyList<GameCard> ListGames(SqliteConnection connection)
    {
        var result = new List<GameCard>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
            FROM games ORDER BY accepted_utc, game_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadGame(reader));
        }

        return result;
    }

    /// <summary>
    /// 数据库侧检索（阶段三：列表分页/检索下沉到 SQL）：搜索命中用户标题或原文件夹名、
    /// 收藏过滤、标签过滤（与搜索 AND 组合）、名称/修改时间/入库时间排序、LIMIT/OFFSET 分页。
    /// total 为过滤后的总数（分页前）。limit &lt;= 0 表示不分页（兼容旧全量语义）。
    /// </summary>
    public static (int Total, IReadOnlyList<GameCard> Items) QueryGames(
        SqliteConnection connection,
        string? search,
        bool favoriteOnly,
        string? tagId,
        string? sort,
        int limit,
        int offset)
    {
        var where = new List<string> { "membership = 'active'" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            // LIKE 通配符转义；中文按字节子串匹配（策划案：预归一化内存搜索的数据库侧等价）。
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            where.Add("(title LIKE $like ESCAPE '\\' OR root_path LIKE $like ESCAPE '\\')");
        }

        if (favoriteOnly)
        {
            where.Add("favorite = 1");
        }

        if (!string.IsNullOrWhiteSpace(tagId))
        {
            where.Add("EXISTS (SELECT 1 FROM game_tags gt WHERE gt.game_id = games.game_id AND gt.tag_id = $tagId)");
        }

        var orderBy = sort switch
        {
            "title-desc" => "title COLLATE NOCASE DESC, game_id",
            "recent" or "updated-desc" => "updated_utc DESC, game_id",
            "accepted-desc" => "accepted_utc DESC, game_id",
            _ => "title COLLATE NOCASE, game_id",
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
        var tags = new Dictionary<string, List<(string Kind, string Name)>>(StringComparer.Ordinal);

        for (var chunkStart = 0; chunkStart < games.Count; chunkStart += chunkSize)
        {
            var ids = new string[Math.Min(chunkSize, games.Count - chunkStart)];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = games[chunkStart + i].GameId;
            }

            var idList = string.Join(", ", ids.Select((_, i) => $"$id{i}"));

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

    public static GameCard? TryGetGameByRootPath(SqliteConnection connection, string rootPath, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, favorite, revision, accepted_utc, updated_utc, translation_inherited, translation_override, availability, missing_since_utc
            FROM games WHERE root_path = $root AND membership = 'active'
            """;
        command.Parameters.AddWithValue("$root", rootPath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }

    /// <summary>收藏切换（games.update 受限字段）：期望 Revision 对齐 games.revision，事务内更新并递增；冲突返回 null。</summary>
    public static int? SetFavorite(SqliteConnection connection, string gameId, bool favorite, int expectedRevision, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE games SET favorite = $favorite, revision = revision + 1, updated_utc = $now
                WHERE game_id = $game
                """;
            update.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            update.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$game", gameId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return currentRevision + 1;
    }

    /// <summary>
    /// 翻译策略用户覆盖（translation.set）：仅写 translation_override 列，继承列不动。
    /// 期望 Revision 对齐 games.revision，事务内更新并递增；游戏不存在或冲突返回 null。
    /// </summary>
    public static int? SetTranslationOverride(
        SqliteConnection connection, string gameId, string? overrideValue, int expectedRevision, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
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

        transaction.Commit();
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
    public static int? RelinkGame(SqliteConnection connection, string gameId, string newRootPath, int expectedRevision, DateTime utcNow)
    {
        using var transaction = (SqliteTransaction)connection.BeginTransaction();
        int currentRevision;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT revision FROM games WHERE game_id = $game";
            select.Parameters.AddWithValue("$game", gameId);
            var result = select.ExecuteScalar();
            if (result is null)
            {
                transaction.Rollback();
                return null;
            }

            currentRevision = Convert.ToInt32(result);
        }

        if (currentRevision != expectedRevision)
        {
            transaction.Rollback();
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

        transaction.Commit();
        return currentRevision + 1;
    }

    public static void InsertIgnoreRule(SqliteConnection connection, IgnoreRule rule)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ignore_rules (ignore_id, scope, path, game_id, reason, revision, created_utc)
            VALUES ($id, $scope, $path, $game, $reason, 1, $created)
            """;
        command.Parameters.AddWithValue("$id", rule.IgnoreId);
        command.Parameters.AddWithValue("$scope", rule.Scope);
        command.Parameters.AddWithValue("$path", (object?)rule.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$game", (object?)rule.GameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)rule.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", rule.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<IgnoreRule> ListIgnoreRules(SqliteConnection connection)
    {
        var result = new List<IgnoreRule>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ignore_id, scope, path, game_id, reason, revision, created_utc
            FROM ignore_rules ORDER BY created_utc, ignore_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IgnoreRule
            {
                IgnoreId = reader.GetString(0),
                Scope = reader.GetString(1),
                Path = reader.IsDBNull(2) ? null : reader.GetString(2),
                GameId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Reason = reader.IsDBNull(4) ? null : reader.GetString(4),
                Revision = reader.GetInt32(5),
                CreatedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }

        return result;
    }

    /// <summary>撤销忽略规则；返回被该规则覆盖且处于 ignored 的候选路径（调用方负责恢复 Observed）。</summary>
    public static IReadOnlyList<string> RemoveIgnoreRule(SqliteConnection connection, string ignoreId)
    {
        var rules = ListIgnoreRules(connection);
        var rule = rules.FirstOrDefault(r => string.Equals(r.IgnoreId, ignoreId, StringComparison.Ordinal));
        if (rule is null)
        {
            return [];
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM ignore_rules WHERE ignore_id = $id";
        delete.Parameters.AddWithValue("$id", ignoreId);
        delete.ExecuteNonQuery();

        if (rule.Scope == "ExactPath" && rule.Path is not null)
        {
            return [rule.Path];
        }

        if (rule.Scope == "Subtree" && rule.Path is not null)
        {
            var covered = new List<string>();
            foreach (var candidate in ListCandidates(connection))
            {
                var candidatePath = candidate.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                if (candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidatePath, rulePath, StringComparison.OrdinalIgnoreCase))
                {
                    covered.Add(candidate.PhysicalPath);
                }
            }

            return covered;
        }

        return [];
    }

    /// <summary>候选抑制判定：ExactPath 同规范路径；Subtree 前缀；ConfirmedIdentity 按 gameId 绑定（随身份证据完善）。</summary>
    public static bool IsSuppressedByIgnoreRule(SqliteConnection connection, string physicalPath, string? boundGameId)
    {
        foreach (var rule in ListIgnoreRules(connection))
        {
            if (rule.Scope == "ExactPath"
                && rule.Path is not null
                && string.Equals(
                    physicalPath.TrimEnd(Path.DirectorySeparatorChar),
                    rule.Path.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rule.Scope == "Subtree" && rule.Path is not null)
            {
                var candidatePath = physicalPath.TrimEnd(Path.DirectorySeparatorChar);
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                if (candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (rule.Scope == "ConfirmedIdentity"
                && rule.GameId is not null
                && boundGameId is not null
                && string.Equals(rule.GameId, boundGameId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
