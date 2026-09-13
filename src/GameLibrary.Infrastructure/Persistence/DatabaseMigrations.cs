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
    ];
}
