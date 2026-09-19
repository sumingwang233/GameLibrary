namespace GameLibrary.Infrastructure.Persistence;

/// <summary>
/// 打开/初始化库的行为参数。app/api 版本由宿主传入（Infrastructure 不依赖 Contracts）；
/// Migrations 默认为随程序发布的迁移集，测试可注入子集模拟旧程序。
/// </summary>
public sealed record SqliteLibraryStoreOptions
{
    public required string AppVersion { get; init; }

    public required string ApiVersion { get; init; }

    public IReadOnlyList<DatabaseMigration> Migrations { get; init; } = DatabaseMigrations.All;

    public int MaxSupportedSchemaVersion => Migrations.Max(m => m.Version);
}

public sealed record DatabaseMigration(int Version, string Sql, bool RequiresVacuum = false);
