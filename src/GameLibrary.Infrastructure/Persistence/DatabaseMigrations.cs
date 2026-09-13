namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 随程序发布的连续整数版本迁移集。每条迁移在单事务内执行（ADR-0004）；
/// 已发布迁移一经合入不得修改历史条目，只能追加。
/// </summary>
public static class DatabaseMigrations
{
    public static readonly IReadOnlyList<DatabaseMigration> All =
    [
        new DatabaseMigration(1, """
            CREATE TABLE schema_info (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                schema_version INTEGER NOT NULL,
                app_version TEXT NOT NULL,
                api_version TEXT NOT NULL,
                library_instance_id TEXT NOT NULL,
                data_epoch TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(2, """
            CREATE TABLE request_receipts (
                library_instance_id TEXT NOT NULL,
                actor TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                request_digest TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('prepared', 'completed')),
                attempt_json TEXT,
                result_json TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (library_instance_id, actor, operation_id, idempotency_key)
            )
            """),
        new DatabaseMigration(3, """
            CREATE TABLE games (
                game_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                root_path TEXT NOT NULL,
                kind TEXT NOT NULL,
                engine TEXT,
                entry_path TEXT,
                membership TEXT NOT NULL CHECK (membership IN ('active', 'removed')),
                revision INTEGER NOT NULL,
                accepted_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE candidates (
                candidate_id TEXT PRIMARY KEY,
                job_id TEXT,
                kind TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                physical_path TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL,
                review_state TEXT NOT NULL CHECK (review_state IN ('observed', 'stabilizing', 'pendingReview', 'accepted', 'deferred', 'ignored')),
                revision INTEGER NOT NULL,
                game_id TEXT,
                observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE ignore_rules (
                ignore_id TEXT PRIMARY KEY,
                scope TEXT NOT NULL CHECK (scope IN ('ExactPath', 'Subtree', 'ConfirmedIdentity')),
                path TEXT,
                game_id TEXT,
                reason TEXT,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            )
            """),
    ];
}
