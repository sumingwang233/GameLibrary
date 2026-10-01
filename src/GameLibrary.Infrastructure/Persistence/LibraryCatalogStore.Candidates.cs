using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

public static partial class LibraryCatalogStore
{
    /// <summary>按物理路径 upsert；返回 true 表示候选已存在（本次为重扫刷新）。</summary>
    public static bool UpsertCandidate(SqliteConnection connection, PersistedCandidate candidate)
    {
        var existed = CandidateExists(connection, candidate.PhysicalPath);
        using var command = connection.CreateCommand();
        command.CommandText = existed
            ? """
                UPDATE candidates
                SET review_state = CASE WHEN json_extract($payload, '$.flash.requiresReview') = 1
                        AND coalesce(json_extract(payload_json, '$.flash.inventory'), '') <> json_extract($payload, '$.flash.inventory')
                        AND review_state NOT IN ('ignored', 'unavailable') THEN 'pendingReview' ELSE review_state END,
                    revision = revision + CASE WHEN json_extract($payload, '$.flash.requiresReview') = 1
                        AND coalesce(json_extract(payload_json, '$.flash.inventory'), '') <> json_extract($payload, '$.flash.inventory') THEN 1 ELSE 0 END,
                    payload_json = $payload, job_id = $job, updated_utc = $observed
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

    public static bool IsDefinitelyMissing(string path)
    {
        // 拔盘、无权限和 I/O 故障不等于文件被删除。
        if (!Directory.Exists(Path.GetPathRoot(path))) return false;
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
        DateTime utcNow, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
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
            ownedTransaction?.Rollback();
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

        ownedTransaction?.Commit();
        return current with
        {
            ReviewState = toState,
            Revision = current.Revision + 1,
            GameId = gameId ?? current.GameId,
            UpdatedUtc = utcNow,
        };
    }

    /// <summary>
    /// accept 原子化（R43）：既有卡复用/建新卡 + 引擎自动标签 + 候选转移 + 匹配指纹在单事务内提交。
    /// 此前三步各自成事务，中间失败会留下孤儿游戏卡或悬挂候选；conflict 时本方法不落任何写。
    /// 指纹（ADR-0001 第四键）在 handler 内、store 锁外计算后经 <paramref name="fingerprint"/> 并入本事务——
    /// 建卡/标签/候选转移/指纹要么全提交要么全不落，无中间崩溃窗口。
    /// </summary>
    public static AcceptCandidateOutcome AcceptCandidate(
        SqliteConnection connection,
        string candidateId,
        int expectedRevision,
        GameCard newGame,
        string engine,
        DateTime utcNow,
        GameFingerprintData? fingerprint = null, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
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
                ownedTransaction?.Rollback();
                throw new InvalidOperationException($"候选不存在：{candidateId}（调用方应先校验）");
            }

            current = ReadCandidate(reader);
        }

        if (current.ReviewState == "accepted" && current.GameId is not null)
        {
            ownedTransaction?.Rollback();
            return new AcceptCandidateOutcome
            {
                Status = "alreadyAccepted",
                Candidate = current,
                GameId = current.GameId,
            };
        }

        if (current.ReviewState != "pendingReview" || current.Revision != expectedRevision)
        {
            ownedTransaction?.Rollback();
            return new AcceptCandidateOutcome
            {
                Status = "conflict",
                Candidate = current,
                GameId = current.GameId ?? "",
            };
        }

        string gameId;
        var bound = current.GameId is null ? null : TryGetGame(connection, current.GameId);
        var existing = bound is not null && string.Equals(bound.RootPath, current.PhysicalPath, StringComparison.OrdinalIgnoreCase)
            ? bound : TryGetGameByRootPath(connection, current.PhysicalPath, transaction, includeRemoved: true);
        if (existing is not null)
        {
            gameId = existing.GameId;
            // 移除后的游戏只有再次明确接受候选才复活，沿用 ID 与所有元数据。
            if (existing.Membership == "removed")
            {
                using var restore = connection.CreateCommand();
                restore.Transaction = transaction;
                restore.CommandText = "UPDATE games SET membership='active', revision=revision+1, updated_utc=$now WHERE game_id=$id";
                restore.Parameters.AddWithValue("$id", gameId);
                restore.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
                restore.ExecuteNonQuery();
            }
        }
        else
        {
            gameId = newGame.GameId;
            InsertGame(connection, newGame, transaction);
        }

        TagStore.EnsureEngineTagAssigned(connection, gameId, engine, utcNow, transaction);

        if (fingerprint is not null)
        {
            UpsertGameFingerprint(connection, gameId, fingerprint, transaction);
        }

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

        ownedTransaction?.Commit();
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
        DateTime utcNow, SqliteTransaction? parentTransaction = null)
    {
        using var ownedTransaction = parentTransaction is null ? connection.BeginTransaction() : null;
        var transaction = parentTransaction ?? ownedTransaction!;
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
                ownedTransaction?.Rollback();
                throw new InvalidOperationException($"候选不存在：{candidateId}（调用方应先校验）");
            }

            current = ReadCandidate(reader);
        }

        if (current.ReviewState != "pendingReview" || current.Revision != expectedRevision)
        {
            ownedTransaction?.Rollback();
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

        ownedTransaction?.Commit();
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
}
