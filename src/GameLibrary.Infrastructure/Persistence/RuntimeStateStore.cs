using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 持久化库根行。Kind（v23）：library=扫描根；manual=手动添加游戏时注册的
/// 路径包含边界根——不参与扫描枚举与候选发现（bug-5）。
/// </summary>
public sealed record PersistedRoot(string RootId, string PhysicalPath, int Revision, DateTime CreatedUtc, string Kind = "library");

/// <summary>持久化启动 Profile 行。</summary>
public sealed record PersistedProfile(
    string ProfileId,
    string GameId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? ToolId,
    bool IsDefault,
    int Revision,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>持久化启动尝试行。</summary>
public sealed record PersistedLaunchAttempt(
    string AttemptId,
    string IdempotencyKey,
    string GameId,
    string ProfileId,
    string? PlanId,
    string State,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int? ProcessId,
    DateTime? ProcessStartedUtc,
    int? ExitCode,
    DateTime? FinishedUtc,
    string? Error,
    DateTime CreatedUtc);

/// <summary>持久化作业记录行。</summary>
public sealed record PersistedJobRecord(
    string JobId,
    string Kind,
    string State,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Error);

/// <summary>
/// 运行态持久化（v1 审查意见修复）：库根、启动 Profile、启动历史与作业记录落库，
/// Host 重启后恢复。业务事实仍以各注册表为准；本层只做结构化存取，不做业务决策。
/// </summary>
public static class RuntimeStateStore
{
    // ---- 库根 ----

    public static IReadOnlyList<PersistedRoot> ReadRoots(SqliteConnection connection)
    {
        var result = new List<PersistedRoot>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_id, physical_path, revision, created_utc, kind
            FROM library_roots ORDER BY created_utc, root_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedRoot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(4)));
        }

        return result;
    }

    public static void UpsertRoot(SqliteConnection connection, PersistedRoot root, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_roots (root_id, physical_path, revision, created_utc, kind)
            VALUES ($id, $path, $rev, $created, $kind)
            ON CONFLICT(root_id) DO UPDATE SET
                physical_path = excluded.physical_path,
                revision = excluded.revision,
                kind = excluded.kind
            """;
        command.Parameters.AddWithValue("$id", root.RootId);
        command.Parameters.AddWithValue("$path", root.PhysicalPath);
        command.Parameters.AddWithValue("$rev", root.Revision);
        command.Parameters.AddWithValue("$created", root.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", root.Kind);
        command.ExecuteNonQuery();
    }

    public static void DeleteRoot(SqliteConnection connection, string rootId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_roots WHERE root_id = $id";
        command.Parameters.AddWithValue("$id", rootId);
        command.ExecuteNonQuery();
    }

    public static bool ContainsPath(string root, string path)
    {
        var prefix = root.TrimEnd('\\', '/');
        return string.Equals(prefix, path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static int RemoveRootGames(SqliteConnection connection, string rootId, string rootPath, DateTime utcNow)
    {
        var remainingRoots = ReadRoots(connection).Where(root => root.RootId != rootId).ToArray();
        bool RemovedPath(string path) => ContainsPath(rootPath, path)
            && !remainingRoots.Any(root => ContainsPath(root.PhysicalPath, path));
        var games = LibraryCatalogStore.ListGames(connection).Where(game => game.Membership == "active" && RemovedPath(game.RootPath)).ToArray();
        var candidates = LibraryCatalogStore.ListCandidates(connection).Where(candidate => RemovedPath(candidate.PhysicalPath)).ToArray();
        using var transaction = connection.BeginTransaction();
        foreach (var game in games)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE games SET membership='removed', revision=revision+1, updated_utc=$now WHERE game_id=$id";
            command.Parameters.AddWithValue("$id", game.GameId);
            command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM candidates WHERE candidate_id=$id";
            command.Parameters.AddWithValue("$id", candidate.CandidateId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM library_roots WHERE root_id=$id;
                UPDATE notification_batches SET state='acknowledged'
                WHERE state='pending' AND NOT EXISTS (
                    SELECT 1 FROM json_each(candidate_ids_json) ids
                    JOIN candidates c ON c.candidate_id=ids.value WHERE c.review_state='pendingReview');
                """;
            command.Parameters.AddWithValue("$id", rootId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return games.Length;
    }

    // ---- 启动 Profile ----

    public static IReadOnlyList<PersistedProfile> ReadProfiles(SqliteConnection connection)
    {
        var result = new List<PersistedProfile>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, game_id, executable_path, arguments_json, working_directory,
                   tool_id, is_default, revision, created_utc, updated_utc
            FROM launch_profiles ORDER BY created_utc, profile_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedProfile(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(3)) ?? [],
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) == 1,
                reader.GetInt32(7),
                DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public static void UpsertProfile(SqliteConnection connection, PersistedProfile profile, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO launch_profiles
                (profile_id, game_id, executable_path, arguments_json, working_directory,
                 tool_id, is_default, revision, created_utc, updated_utc)
            VALUES ($id, $game, $exe, $argv, $cwd, $tool, $def, $rev, $created, $updated)
            ON CONFLICT(profile_id) DO UPDATE SET
                executable_path = excluded.executable_path,
                arguments_json = excluded.arguments_json,
                working_directory = excluded.working_directory,
                tool_id = excluded.tool_id,
                is_default = excluded.is_default,
                revision = excluded.revision,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", profile.ProfileId);
        command.Parameters.AddWithValue("$game", profile.GameId);
        command.Parameters.AddWithValue("$exe", profile.ExecutablePath);
        command.Parameters.AddWithValue("$argv", System.Text.Json.JsonSerializer.Serialize(profile.Arguments));
        command.Parameters.AddWithValue("$cwd", profile.WorkingDirectory);
        command.Parameters.AddWithValue("$tool", (object?)profile.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$def", profile.IsDefault ? 1 : 0);
        command.Parameters.AddWithValue("$rev", profile.Revision);
        command.Parameters.AddWithValue("$created", profile.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", utcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void DeleteProfile(SqliteConnection connection, string profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM launch_profiles WHERE profile_id = $id";
        command.Parameters.AddWithValue("$id", profileId);
        command.ExecuteNonQuery();
    }

    // ---- 启动尝试 ----

    public static IReadOnlyList<PersistedLaunchAttempt> ReadAttempts(SqliteConnection connection)
    {
        var result = new List<PersistedLaunchAttempt>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, idempotency_key, game_id, profile_id, plan_id, state,
                   executable_path, arguments_json, working_directory, process_id,
                   process_started_utc, exit_code, finished_utc, error, created_utc
            FROM launch_attempts ORDER BY created_utc, attempt_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedLaunchAttempt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(7)) ?? [],
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                ReadNullableDateTime(reader, 10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                ReadNullableDateTime(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                DateTime.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public static void UpsertAttempt(SqliteConnection connection, PersistedLaunchAttempt attempt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO launch_attempts
                (attempt_id, idempotency_key, game_id, profile_id, plan_id, state,
                 executable_path, arguments_json, working_directory, process_id,
                 process_started_utc, exit_code, finished_utc, error, created_utc)
            VALUES ($id, $key, $game, $profile, $plan, $state, $exe, $argv, $cwd, $pid,
                    $pstart, $exit, $fin, $err, $created)
            ON CONFLICT(attempt_id) DO UPDATE SET
                state = excluded.state,
                process_id = excluded.process_id,
                process_started_utc = excluded.process_started_utc,
                exit_code = excluded.exit_code,
                finished_utc = excluded.finished_utc,
                error = excluded.error
            """;
        command.Parameters.AddWithValue("$id", attempt.AttemptId);
        command.Parameters.AddWithValue("$key", attempt.IdempotencyKey);
        command.Parameters.AddWithValue("$game", attempt.GameId);
        command.Parameters.AddWithValue("$profile", attempt.ProfileId);
        command.Parameters.AddWithValue("$plan", (object?)attempt.PlanId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", attempt.State);
        command.Parameters.AddWithValue("$exe", attempt.ExecutablePath);
        command.Parameters.AddWithValue("$argv", System.Text.Json.JsonSerializer.Serialize(attempt.Arguments));
        command.Parameters.AddWithValue("$cwd", attempt.WorkingDirectory);
        command.Parameters.AddWithValue("$pid", (object?)attempt.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pstart", attempt.ProcessStartedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$exit", (object?)attempt.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$fin", attempt.FinishedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$err", (object?)attempt.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", attempt.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    // ---- 作业记录 ----

    public static IReadOnlyList<PersistedJobRecord> ReadJobs(SqliteConnection connection)
    {
        var result = new List<PersistedJobRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, kind, state, created_utc, started_utc, finished_utc, error
            FROM job_records ORDER BY created_utc, job_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedJobRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ReadNullableDateTime(reader, 4),
                ReadNullableDateTime(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    public static void UpsertJob(SqliteConnection connection, PersistedJobRecord job)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_records (job_id, kind, state, created_utc, started_utc, finished_utc, error)
            VALUES ($id, $kind, $state, $created, $started, $finished, $error)
            ON CONFLICT(job_id) DO UPDATE SET
                state = excluded.state,
                started_utc = excluded.started_utc,
                finished_utc = excluded.finished_utc,
                error = excluded.error
            """;
        command.Parameters.AddWithValue("$id", job.JobId);
        command.Parameters.AddWithValue("$kind", job.Kind);
        command.Parameters.AddWithValue("$state", job.State);
        command.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$started", job.StartedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$finished", job.FinishedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>启动恢复：上次运行中断（非终态）的作业标记为 interrupted，不再假装仍在运行。</summary>
    public static int MarkInterruptedJobs(SqliteConnection connection, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE job_records
            SET state = 'interrupted', finished_utc = $now, error = COALESCE(error, '宿主进程重启时作业尚未完成')
            WHERE state IN ('queued', 'running', 'cancelRequested')
            """;
        command.Parameters.AddWithValue("$now", utcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    private static DateTime? ReadNullableDateTime(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column)
            ? null
            : DateTime.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
