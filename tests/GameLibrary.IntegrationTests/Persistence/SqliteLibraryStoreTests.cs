using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>库存储基础（T10）：建库/打开/PRAGMA/版本判定/损坏不建空库。</summary>
public sealed class SqliteLibraryStoreTests
{
    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options(
        IReadOnlyList<DatabaseMigration>? migrations = null) =>
        new()
        {
            AppVersion = "0.1.0-dev",
            ApiVersion = "1",
            Migrations = migrations ?? DatabaseMigrations.All,
        };

    private static void Cleanup(string dataDir)
    {
        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_CreatesLibraryWithIdentityAndVersion()
    {
        var dataDir = FreshDataDir("store-init");
        try
        {
            var result = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);

            Assert.True(result.IsOpened, result.Detail);
            Assert.Equal(DatabaseMigrations.All.Max(m => m.Version), result.Store!.Info.SchemaVersion);
            Assert.NotEmpty(result.Store.Info.LibraryInstanceId);
            Assert.NotEmpty(result.Store.Info.DataEpoch);
            Assert.True(File.Exists(Path.Combine(dataDir, "library.db")));
            Assert.True(Directory.Exists(Path.Combine(dataDir, "backups")));
            await result.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Initialize_OnExistingLibrary_ReturnsAlreadyInitialized()
    {
        var dataDir = FreshDataDir("store-dup");
        try
        {
            var first = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(first.IsOpened);
            var firstInfo = first.Store!.Info;
            await first.Store.DisposeAsync();

            var second = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.AlreadyInitialized, second.Status);
            Assert.Null(second.Store);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Initialize_OverZeroByteResidue_Recreates()
    {
        var dataDir = FreshDataDir("store-zero");
        try
        {
            var dbPath = Path.Combine(dataDir, "library.db");
            await File.WriteAllBytesAsync(dbPath, []);

            var result = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);

            Assert.True(result.IsOpened, result.Detail);
            await result.Store!.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TryOpen_MissingLibrary_ReturnsNeedsInitialization()
    {
        var dataDir = FreshDataDir("store-missing");

        var result = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);
        Assert.Equal(LibraryOpenStatus.NeedsInitialization, result.Status);
        Assert.False(File.Exists(Path.Combine(dataDir, "library.db")));

        Cleanup(dataDir);
    }

    [Fact]
    public async Task TryOpen_ExistingLibrary_PreservesIdentityAndUsesWal()
    {
        var dataDir = FreshDataDir("store-open");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            var initInfo = init.Store!.Info;
            await init.Store.DisposeAsync();

            var opened = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);

            Assert.True(opened.IsOpened, opened.Detail);
            Assert.Equal(initInfo.LibraryInstanceId, opened.Store!.Info.LibraryInstanceId);
            Assert.Equal(initInfo.DataEpoch, opened.Store.Info.DataEpoch);

            await using var probe = new SqliteConnection($"Data Source={Path.Combine(dataDir, "library.db")};Pooling=False");
            await probe.OpenAsync();
            await using var command = probe.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string?)command.ExecuteScalar());

            await opened.Store.DisposeAsync();
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TryOpen_ZeroByteLibrary_ReturnsRecoveryRequiredWithoutRecreating()
    {
        var dataDir = FreshDataDir("store-zerobyte");
        try
        {
            var dbPath = Path.Combine(dataDir, "library.db");
            await File.WriteAllBytesAsync(dbPath, []);

            var result = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.RecoveryRequired, result.Status);
            Assert.Equal(0, new FileInfo(dbPath).Length);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TryOpen_CorruptLibrary_ReturnsRecoveryRequiredAndKeepsBytes()
    {
        var dataDir = FreshDataDir("store-corrupt");
        try
        {
            var dbPath = Path.Combine(dataDir, "library.db");
            var garbage = new byte[512];
            new Random(42).NextBytes(garbage);
            await File.WriteAllBytesAsync(dbPath, garbage);

            var result = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.RecoveryRequired, result.Status);
            Assert.Equal(garbage.Length, new FileInfo(dbPath).Length);
            Assert.Equal(garbage, await File.ReadAllBytesAsync(dbPath));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TryOpen_SchemaTooNew_RejectsWithoutWriting()
    {
        var dataDir = FreshDataDir("store-toonew");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            await init.Store!.DisposeAsync();
            await ExecuteDirectAsync(
                Path.Combine(dataDir, "library.db"),
                "UPDATE schema_info SET schema_version = 99 WHERE id = 1");

            var result = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.SchemaTooNew, result.Status);
            Assert.Equal(99, await ScalarDirectAsync(Path.Combine(dataDir, "library.db"), "SELECT schema_version FROM schema_info"));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    private static async Task ExecuteDirectAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarDirectAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
