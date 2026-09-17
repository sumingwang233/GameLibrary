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
        new DatabaseMigration(10, """
            CREATE TABLE notification_batches (
                notification_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('candidatesReady')),
                candidate_ids_json TEXT NOT NULL,
                title TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('pending', 'acknowledged', 'deferred')),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(11, """
            CREATE TABLE app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            )
            """),
        new DatabaseMigration(12, """
            CREATE TABLE event_records (
                sequence INTEGER PRIMARY KEY,
                data_epoch TEXT NOT NULL,
                type TEXT NOT NULL,
                entity_key TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL
            );
            CREATE INDEX idx_event_records_epoch ON event_records (data_epoch, sequence)
            """),
        // 13：关键业务状态持久化（v1 审查意见：库根/启动 Profile/启动历史/作业记录跨重启保留）。
        new DatabaseMigration(13, """
            CREATE TABLE library_roots (
                root_id TEXT PRIMARY KEY,
                physical_path TEXT NOT NULL UNIQUE,
                revision INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE launch_profiles (
                profile_id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                executable_path TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                tool_id TEXT,
                is_default INTEGER NOT NULL CHECK (is_default IN (0, 1)),
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX idx_launch_profiles_game ON launch_profiles (game_id);
            CREATE TABLE launch_attempts (
                attempt_id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL,
                game_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                plan_id TEXT,
                state TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                process_id INTEGER,
                process_started_utc TEXT,
                exit_code INTEGER,
                finished_utc TEXT,
                error TEXT,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX idx_launch_attempts_game ON launch_attempts (game_id, created_utc);
            CREATE TABLE job_records (
                job_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                started_utc TEXT,
                finished_utc TEXT,
                error TEXT
            )
            """),
        // 14：game_fields 重建为 (game_id, field_key, source) 主键——允许 auto/user 两层
        // 同时存在（v1 审查意见：原 PK (game_id, field_key) 会让用户覆盖吞掉自动值）。
        new DatabaseMigration(14, """
            CREATE TABLE game_fields_new (
                game_id TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                field_key TEXT NOT NULL CHECK (field_key IN ('title', 'summary')),
                value TEXT,
                source TEXT NOT NULL CHECK (source IN ('user', 'auto')),
                revision INTEGER NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (game_id, field_key, source)
            );
            INSERT INTO game_fields_new (game_id, field_key, value, source, revision, updated_utc)
                SELECT game_id, field_key, value, source, revision, updated_utc FROM game_fields;
            DROP TABLE game_fields;
            ALTER TABLE game_fields_new RENAME TO game_fields
            """),
        // 15：game_assets 补外键（先清孤立行，再重建引用）。
        new DatabaseMigration(15, """
            CREATE TABLE game_assets_new (
                asset_id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK (kind IN ('cover')),
                file_path TEXT NOT NULL,
                is_current INTEGER NOT NULL CHECK (is_current IN (0, 1)),
                imported_utc TEXT NOT NULL
            );
            INSERT INTO game_assets_new (asset_id, game_id, kind, file_path, is_current, imported_utc)
                SELECT a.asset_id, a.game_id, a.kind, a.file_path, a.is_current, a.imported_utc
                FROM game_assets a WHERE EXISTS (SELECT 1 FROM games g WHERE g.game_id = a.game_id);
            DROP TABLE game_assets;
            ALTER TABLE game_assets_new RENAME TO game_assets
            """),
        // 16：candidates.game_id 补外键（游戏删除时候选解绑为 NULL，保留候选记录）。
        new DatabaseMigration(16, """
            CREATE TABLE candidates_new (
                candidate_id TEXT PRIMARY KEY,
                job_id TEXT,
                kind TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                physical_path TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL,
                review_state TEXT NOT NULL CHECK (review_state IN ('observed', 'stabilizing', 'pendingReview', 'accepted', 'deferred', 'ignored')),
                revision INTEGER NOT NULL,
                game_id TEXT REFERENCES games(game_id) ON DELETE SET NULL,
                observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            INSERT INTO candidates_new (candidate_id, job_id, kind, relative_path, physical_path, payload_json, review_state, revision, game_id, observed_utc, updated_utc)
                SELECT candidate_id, job_id, kind, relative_path, physical_path, payload_json, review_state, revision, game_id, observed_utc, updated_utc
                FROM candidates
                WHERE game_id IS NULL OR EXISTS (SELECT 1 FROM games g WHERE g.game_id = candidates.game_id);
            DROP TABLE candidates;
            ALTER TABLE candidates_new RENAME TO candidates
            """),
        // 17：ignore_rules.game_id 补外键（身份级忽略规则随游戏删除级联清理）。
        new DatabaseMigration(17, """
            CREATE TABLE ignore_rules_new (
                ignore_id TEXT PRIMARY KEY,
                scope TEXT NOT NULL CHECK (scope IN ('ExactPath', 'Subtree', 'ConfirmedIdentity')),
                path TEXT,
                game_id TEXT REFERENCES games(game_id) ON DELETE CASCADE,
                reason TEXT,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );
            INSERT INTO ignore_rules_new (ignore_id, scope, path, game_id, reason, revision, created_utc)
                SELECT r.ignore_id, r.scope, r.path, r.game_id, r.reason, r.revision, r.created_utc
                FROM ignore_rules r
                WHERE r.game_id IS NULL OR EXISTS (SELECT 1 FROM games g WHERE g.game_id = r.game_id);
            DROP TABLE ignore_rules;
            ALTER TABLE ignore_rules_new RENAME TO ignore_rules
            """),
        // 18：标签三层——标签定义（按 类型+规范值 唯一）、游戏↔标签关联、覆盖表
        // （Suppress 阻止扫描恢复用户删除的自动标签；ForceAdd 预留）。随 tags.×8（T-collections）。
        new DatabaseMigration(18, """
            CREATE TABLE tags (
                tag_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('engine', 'user')),
                name TEXT NOT NULL,
                color TEXT,
                revision INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE (kind, name)
            );
            CREATE TABLE game_tags (
                game_id TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                tag_id TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (game_id, tag_id)
            );
            CREATE TABLE tag_overrides (
                game_id TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                tag_kind TEXT NOT NULL,
                tag_name TEXT NOT NULL,
                action TEXT NOT NULL CHECK (action IN ('suppress', 'force')),
                created_utc TEXT NOT NULL,
                PRIMARY KEY (game_id, tag_kind, tag_name)
            );
            """),
    ];
}
