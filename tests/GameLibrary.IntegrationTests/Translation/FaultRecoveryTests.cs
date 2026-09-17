using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>
/// T27 故障恢复演练（本步覆盖 REC-01/REC-03/REC-04）：
/// - REC-01 迁移边界：多步迁移中段失败 → 停在最后成功版本，旧库可用，快照在；
/// - REC-03 损坏分类：配置损坏回落默认（可 reset 修复）；缓存重建不触碰用户原图；
/// - REC-04 启动收据边界：prepared 无引用可继续；注册表命中返回现状；
///   进程无法证明 → UnknownOutcome 且原键不再启动。
/// REC-02（备份恢复控制日志）随 backups.restore 实现任务；REC-05 随 T19 打包验收。
/// </summary>
public sealed class FaultRecoveryTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public FaultRecoveryTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t27-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters ?? new { });
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    // ---------- REC-01 ----------

    [Fact]
    public async Task Rec01_MidSequenceMigrationFailure_StopsAtLastSuccessfulVersion()
    {
        var dir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"t27-rec01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var v1 = DatabaseMigrations.All[0];
            var v2 = new DatabaseMigration(2, "CREATE TABLE rec01_step2 (id INTEGER PRIMARY KEY)");
            var v3 = new DatabaseMigration(3, "CREATE TABLE rec01_step3 (id INTEGER PRIMARY KEY");
            var options = new SqliteLibraryStoreOptions
            {
                AppVersion = "test",
                ApiVersion = "1",
                Migrations = [v1, v2, v3],
            };

            var init = await SqliteLibraryStore.InitializeAsync(dir, options with { Migrations = [v1] }, CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            var instanceId = init.Store!.Info.LibraryInstanceId;
            await init.Store.DisposeAsync();

            var upgrade = await SqliteLibraryStore.TryOpenAsync(dir, options, CancellationToken.None);

            Assert.Equal(LibraryOpenStatus.MigrationFailed, upgrade.Status);
            Assert.Contains("快照", upgrade.Detail, StringComparison.Ordinal);

            // v2 已提交（逐步事务），v3 未执行：库停在版本 2，旧结构完整可继续。
            using (var probe = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "library.db")};Pooling=False"))
            {
                probe.Open();
                using var version = probe.CreateCommand();
                version.CommandText = "SELECT schema_version FROM schema_info WHERE id = 1";
                Assert.Equal(2L, version.ExecuteScalar());
                using var step2 = probe.CreateCommand();
                step2.CommandText = "SELECT COUNT(*) FROM rec01_step2";
                Assert.Equal(0L, step2.ExecuteScalar());
            }

            var snapshot = Directory.GetFiles(Path.Combine(dir, "backups"), "pre-migration-*.db");
            Assert.Single(snapshot);

            // 修复迁移集后（替换 v3 为合法 SQL）可继续升级，身份不变。
            var fixedOptions = new SqliteLibraryStoreOptions
            {
                AppVersion = "test",
                ApiVersion = "1",
                Migrations = [v1, v2, new DatabaseMigration(3, "CREATE TABLE rec01_step3 (id INTEGER PRIMARY KEY)")],
            };
            var reopened = await SqliteLibraryStore.TryOpenAsync(dir, fixedOptions, CancellationToken.None);
            Assert.True(reopened.IsOpened, reopened.Detail);
            Assert.Equal(3, reopened.Store!.Info.SchemaVersion);
            Assert.Equal(instanceId, reopened.Store.Info.LibraryInstanceId);
            await reopened.Store.DisposeAsync();
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    // ---------- REC-03 ----------

    [Fact]
    public async Task Rec03_CorruptSettings_FallBackToDefaults_AndRecoverable()
    {
        var store = _fixture.State.Library.Store!;
        using (var corrupt = store.DatabaseConnection.CreateCommand())
        {
            corrupt.CommandText = """
                INSERT INTO app_settings (key, value, updated_utc) VALUES
                    ('theme', '', '2020-01-01T00:00:00.0000000Z'),
                    ('scanIntervalMinutes', 'not-a-number', '2020-01-01T00:00:00.0000000Z'),
                    ('autostartEnabled', 'maybe', '2020-01-01T00:00:00.0000000Z'),
                    ('__revision', 'garbage', '2020-01-01T00:00:00.0000000Z')
                """;
            corrupt.ExecuteNonQuery();
        }

        var get = await InvokeAsync("settings.get");

        // 损坏值回落默认（revision 无法解析 → 0），不抛出、不阻塞。
        Assert.True(get.Ok, get.Error?.Message);
        Assert.Equal(0, get.Data.GetProperty("revision").GetInt32());
        Assert.Equal("dark", get.Data.GetProperty("theme").GetString());
        Assert.Equal(15, get.Data.GetProperty("scanIntervalMinutes").GetInt32());

        // settings.update 仍可用（可自愈）。
        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"t27-{Guid.NewGuid():N}",
            expectedRevision = 0,
            theme = "light",
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal("light", update.Data.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Rec03_CacheRebuild_ClearsCacheOnly_UserAssetsUntouched()
    {
        var store = _fixture.State.Library.Store!;
        var gameId = $"game-{Guid.NewGuid():N}";
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "T27 缓存游戏",
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });

        // 用户原图（assets/，永不触碰）与缓存垃圾（cache/，应被清空）。
        var userImage = Path.Combine(_fixture.State.DataDirectory, "assets", gameId, "original.png");
        Directory.CreateDirectory(Path.GetDirectoryName(userImage)!);
        await File.WriteAllTextAsync(userImage, "user-original-bytes");
        var cacheDir = Path.Combine(_fixture.State.DataDirectory, "cache", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "corrupt-thumb.bin"), "garbage");

        var rebuild = await InvokeAsync("diagnostics.cache_rebuild", new
        {
            idempotencyKey = $"t27-{Guid.NewGuid():N}",
        });

        Assert.True(rebuild.Ok, rebuild.Error?.Message);
        Assert.Equal(1, rebuild.Data.GetProperty("removedFiles").GetInt64());
        Assert.Equal(1, rebuild.Data.GetProperty("userAssetFilesUntouched").GetInt64());
        Assert.True(File.Exists(userImage), "用户原图不得被删除");
        Assert.False(File.Exists(Path.Combine(cacheDir, "corrupt-thumb.bin")));
    }

    [Fact]
    public async Task Rec03_CacheRebuild_DoesNotCountLockedFileAsRemoved()
    {
        var cacheDir = Path.Combine(_fixture.State.DataDirectory, "cache", $"locked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        var ordinary = Path.Combine(cacheDir, "ordinary.bin");
        var locked = Path.Combine(cacheDir, "locked.bin");
        await File.WriteAllBytesAsync(ordinary, [1, 2, 3]);
        await File.WriteAllBytesAsync(locked, [4, 5, 6, 7]);

        using (var lockHandle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rebuild = await InvokeAsync("diagnostics.cache_rebuild", new
            {
                idempotencyKey = $"cache-locked-{Guid.NewGuid():N}",
            });
            Assert.True(rebuild.Ok, rebuild.Error?.Message);
            Assert.Equal(1, rebuild.Data.GetProperty("removedFiles").GetInt64());
            Assert.Equal(3, rebuild.Data.GetProperty("removedBytes").GetInt64());
            Assert.True(rebuild.Data.GetProperty("skippedErrors").GetInt64() >= 1);
            Assert.True(File.Exists(locked));
            Assert.False(File.Exists(ordinary));
        }
    }

    [Fact]
    public async Task Rec03_CacheRebuild_DoesNotFollowLinkedDirectory()
    {
        var dataDir = _fixture.State.DataDirectory;
        var outside = Path.Combine(dataDir, $"outside-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "user-file.bin");
        await File.WriteAllBytesAsync(original, [1, 2, 3, 4]);
        var cacheDir = Path.Combine(dataDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var link = Path.Combine(cacheDir, $"linked-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // 此 Windows 测试宿主未授权创建 symlink；其他缓存安全断言仍继续运行。
            return;
        }

        try
        {
            var rebuild = await InvokeAsync("diagnostics.cache_rebuild", new
            {
                idempotencyKey = $"cache-link-{Guid.NewGuid():N}",
            });
            Assert.True(rebuild.Ok, rebuild.Error?.Message);
            Assert.True(rebuild.Data.GetProperty("skippedLinks").GetInt64() >= 1);
            Assert.True(File.Exists(original), "缓存清理不得沿链接删除库外用户文件");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // ---------- REC-04 ----------

    [Fact]
    public async Task Rec04_PreparedReceiptWithoutAttempt_AllowsExecution()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var store = _fixture.State.Library.Store!;
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "T27 收据游戏",
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        var profileId = _fixture.State.Launches.AddProfile(
            gameId, Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe"), [], AppContext.BaseDirectory).ProfileId;

        var receipt = new RequestReceipt
        {
            LibraryInstanceId = store.Info.LibraryInstanceId,
            Actor = "crashed-client",
            OperationId = "launch.execute",
            IdempotencyKey = "t27-prepared-noattempt",
            RequestDigest = "digest",
            Status = "prepared",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertPreparedReceipt(receipt);

        var execute = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = "t27-prepared-noattempt",
            profileId,
        });

        // 进程尚未创建即中断：允许本次执行继续（不重复启动旧的，因为旧的不存在）。
        Assert.True(execute.Ok, execute.Error?.Message);
        Assert.Equal("processCreated", execute.Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Rec04_PreparedReceiptWithDeadProcess_UnknownOutcomeAndKeyBlocked()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var store = _fixture.State.Library.Store!;
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "T27 死进程游戏",
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        var profileId = _fixture.State.Launches.AddProfile(
            gameId, Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe"), [], AppContext.BaseDirectory).ProfileId;

        // 构造"进程已创建但收据未完成"的崩溃残迹：prepared + attempt 引用指向已退出的 pid。
        // 摘要必须与重试请求一致（同键同参才进入恢复路径；同键异参是 IdempotencyConflict，另一契约分支）。
        var retryParameters = JsonSerializer.Serialize(new { idempotencyKey = "t27-dead-process", profileId });
        var retryDigest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retryParameters)));
        var attemptRef = JsonSerializer.Serialize(new
        {
            attemptId = "attempt-ghost",
            processId = int.MaxValue,
            processStartedUtc = DateTime.UtcNow.ToString("O"),
            executablePath = Environment.ProcessPath ?? "unknown",
        });
        var receipt = new RequestReceipt
        {
            LibraryInstanceId = store.Info.LibraryInstanceId,
            Actor = "crashed-client",
            OperationId = "launch.execute",
            IdempotencyKey = "t27-dead-process",
            RequestDigest = retryDigest,
            Status = "prepared",
            AttemptJson = attemptRef,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertPreparedReceipt(receipt);

        var before = _fixture.State.Launches.History(gameId).Count;
        // 收据按 (instance, actor, operation, key) 唯一：请求的 clientName 必须与崩溃客户端一致。
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "crashed-client",
            CancellationToken.None);
        var execute = await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = "req-t27-dead",
                OperationId = "launch.execute",
                Parameters = JsonDocument.Parse(retryParameters).RootElement.Clone(),
            },
            CancellationToken.None);

        Assert.False(execute.Ok);
        Assert.Equal(ErrorCodes.UnknownOutcome, execute.Error!.Code);
        // 原键重试不得再次启动：无新 attempt 产生。
        Assert.Equal(before, _fixture.State.Launches.History(gameId).Count);
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
