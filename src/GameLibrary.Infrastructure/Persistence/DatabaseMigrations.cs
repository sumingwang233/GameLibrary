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
        new DatabaseMigration(4, """
            CREATE TABLE game_fields (
                game_id TEXT NOT NULL,
                field_key TEXT NOT NULL CHECK (field_key IN ('title', 'summary')),
                value TEXT,
                source TEXT NOT NULL CHECK (source IN ('user', 'auto')),
                revision INTEGER NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (game_id, field_key)
            );
            CREATE TABLE game_assets (
                asset_id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('cover')),
                file_path TEXT NOT NULL,
                is_current INTEGER NOT NULL CHECK (is_current IN (0, 1)),
                imported_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(5, """
            CREATE TABLE verification_records (
                record_id TEXT PRIMARY KEY,
                tool_id TEXT NOT NULL,
                tool_fingerprint TEXT NOT NULL,
                engine TEXT NOT NULL,
                sample_path TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('Unknown', 'Guided', 'SemiAutomatic', 'VerifiedAutomatic')),
                game_started INTEGER NOT NULL CHECK (game_started IN (0, 1)),
                translation_confirmed INTEGER NOT NULL CHECK (translation_confirmed IN (0, 1)),
                note TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(6, "ALTER TABLE games ADD COLUMN favorite INTEGER NOT NULL DEFAULT 0"),
        new DatabaseMigration(7, """
            ALTER TABLE games ADD COLUMN translation_inherited INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE games ADD COLUMN translation_override TEXT;
            """),
        new DatabaseMigration(8, """
            CREATE TABLE library_views (
                view_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                filter_json TEXT NOT NULL,
                sort TEXT NOT NULL CHECK (sort IN ('title', 'recent')),
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(9, """
            ALTER TABLE games ADD COLUMN availability TEXT NOT NULL DEFAULT 'unknown';
            ALTER TABLE games ADD COLUMN missing_since_utc TEXT;
            """),
    ];
}
