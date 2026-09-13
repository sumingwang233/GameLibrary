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

    public int Revision { get; init; } = 1;

    public required DateTime AcceptedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
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

    public static void InsertGame(SqliteConnection connection, GameCard game)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO games
                (game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc)
            VALUES ($id, $title, $root, $kind, $engine, $entry, 'active', 1, $accepted, $accepted)
            """;
        command.Parameters.AddWithValue("$id", game.GameId);
        command.Parameters.AddWithValue("$title", game.Title);
        command.Parameters.AddWithValue("$root", game.RootPath);
        command.Parameters.AddWithValue("$kind", game.Kind);
        command.Parameters.AddWithValue("$engine", (object?)game.Engine ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry", (object?)game.EntryPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$accepted", game.AcceptedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static GameCard? TryGetGame(SqliteConnection connection, string gameId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc
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
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc
            FROM games ORDER BY accepted_utc, game_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadGame(reader));
        }

        return result;
    }

    public static GameCard? TryGetGameByRootPath(SqliteConnection connection, string rootPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, title, root_path, kind, engine, entry_path, membership, revision, accepted_utc, updated_utc
            FROM games WHERE root_path = $root AND membership = 'active'
            """;
        command.Parameters.AddWithValue("$root", rootPath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
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
        Revision = reader.GetInt32(7),
        AcceptedUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
