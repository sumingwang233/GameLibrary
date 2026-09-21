using GameLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLibrary.IntegrationTests.Persistence;

/// <summary>
/// v1 审查修复回归：
/// 1) game_fields 分层——auto/user 两行并存，用户覆盖不再吞掉自动值，reset 恢复"刷新后的"自动值；
/// 2) 外键真实生效（删除游戏级联清理资料/封面）；
/// 3) settings.reset 的 Revision 保持单调（不归零回退）；
/// 4) 库根/Profile/启动尝试/作业记录运行态持久化读写。
/// </summary>
public sealed class FieldLayeringAndRuntimeStateTests
{
    private static string FreshDataDir(string prefix)
    {
        var path = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SqliteLibraryStoreOptions Options() =>
        new()
        {
            AppVersion = "0.1.0-dev",
            ApiVersion = "1",
            Migrations = DatabaseMigrations.All,
        };

    private static void Cleanup(string dataDir)
    {
        try
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static GameCard SampleGame(string id, string title) => new()
    {
        GameId = id,
        Title = title,
        RootPath = $@"C:\games\{id}",
        Kind = "gameRoot",
        Membership = "active",
        AcceptedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task FieldLayering_UserOverwriteKeepsAutoRow_ResetRestoresRefreshedAuto()
    {
        var dataDir = FreshDataDir("layering");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            await using var store = init.Store!;
            var utcNow = DateTime.UtcNow;
            store.InsertGame(SampleGame("game-1", "文件夹名"));

            // 自动层初值（≠ 文件夹名：模拟元数据写入）。
            store.WriteAutoField("game-1", "title", "自动标题A", utcNow);
            var autoOnly = store.EffectiveField("game-1", "title", "fallback");
            Assert.Equal(("自动标题A", "auto"), autoOnly);

            // 用户覆盖：user 层生效，auto 层保留。
            var rev1 = store.SetGameField("game-1", "title", "用户标题", "user", 1, utcNow);
            Assert.NotNull(rev1);
            var withUser = store.EffectiveField("game-1", "title", "fallback");
            Assert.Equal(("用户标题", "user"), withUser);

            // 元数据刷新：只更新 auto 层；user 覆盖不受影响。
            store.WriteAutoField("game-1", "title", "自动标题B", utcNow);
            var stillUser = store.EffectiveField("game-1", "title", "fallback");
            Assert.Equal(("用户标题", "user"), stillUser);

            // reset：恢复的是"刷新后的"自动值 B（旧实现只能退回文件夹名或被吞的旧自动值）。
            var rev2 = store.ResetGameField("game-1", "title", "文件夹名", rev1!.Value, utcNow);
            Assert.NotNull(rev2);
            var afterReset = store.EffectiveField("game-1", "title", "fallback");
            Assert.Equal(("自动标题B", "auto"), afterReset);
            var game = store.TryGetGame("game-1");
            Assert.Equal("自动标题B", game!.Title);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task ForeignKeys_DeletingGameCascadesFieldsAndAssets_NoOrphans()
    {
        var dataDir = FreshDataDir("fk");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            await using var store = init.Store!;
            var utcNow = DateTime.UtcNow;
            store.InsertGame(SampleGame("game-1", "游戏一"));
            store.InsertGame(SampleGame("game-2", "游戏二"));
            store.ImportAsset("game-1", $@"{dataDir}\cover.png", utcNow);
            store.SetGameField("game-1", "title", "用户标题", "user", 1, utcNow);

            // 直接删除 game-1 行（模拟一致性修复/未来删除能力），验证级联。
            store.WriteExclusive((connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM games WHERE game_id = 'game-1'";
                command.ExecuteNonQuery();
            });

            var violations = store.ReadExclusive<string[]>((connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_key_check";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    rows.Add(reader.GetString(0));
                }

                return rows.ToArray();
            });
            Assert.Empty(violations);

            Assert.Empty(store.ListAssets("game-1"));
            Assert.Null(store.TryGetGame("game-1"));
            Assert.NotNull(store.TryGetGame("game-2"));
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task SettingsReset_RevisionStaysMonotonic()
    {
        var dataDir = FreshDataDir("settings-rev");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            await using var store = init.Store!;
            var utcNow = DateTime.UtcNow;

            var rev1 = store.WriteSettingsKeys([("theme", "light")], utcNow);
            var rev2 = store.WriteSettingsKeys([("theme", "dark")], utcNow);
            Assert.True(rev2 > rev1);

            var resetRevision = store.ResetSettings(utcNow);
            Assert.True(resetRevision > rev2, $"reset 后 Revision {resetRevision} 必须高于 reset 前 {rev2}（单调不回退）");

            var rev3 = store.WriteSettingsKeys([("theme", "light")], utcNow);
            Assert.True(rev3 > resetRevision);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task RemoveRoot_ClearsOnlyUncoveredGamesAndCandidates_WithoutTouchingFiles()
    {
        var dataDir = FreshDataDir("remove-root");
        try
        {
            var rootPath = Path.Combine(dataDir, "[游戏]%_");
            var nested = Path.Combine(rootPath, "保留");
            Directory.CreateDirectory(rootPath);
            var executable = Path.Combine(rootPath, "Game.exe");
            File.WriteAllText(executable, "game-file");
            var initialized = await SqliteLibraryStore.InitializeAsync(Path.Combine(dataDir, "data"), Options(), CancellationToken.None);
            await using var store = initialized.Store!;
            var now = DateTime.UtcNow;
            store.UpsertRoot(new PersistedRoot("parent", rootPath, 1, now), now);
            store.UpsertRoot(new PersistedRoot("child", nested, 1, now), now);
            store.InsertGame(SampleGame("removed", "移除") with { RootPath = executable });
            store.InsertGame(SampleGame("kept", "保留") with { RootPath = Path.Combine(nested, "Game.exe") });
            store.InsertGame(SampleGame("sibling", "相似前缀") with { RootPath = rootPath + "other\\Game.exe" });
            var candidate = new PersistedCandidate
            {
                CandidateId = "candidate",
                JobId = "scan",
                Kind = "fileGame",
                RelativePath = "Game.exe",
                PhysicalPath = executable,
                PayloadJson = "{}",
                ReviewState = "pendingReview",
                ObservedUtc = now,
                UpdatedUtc = now,
            };
            store.UpsertCandidate(candidate);
            store.EnsureCandidateBatch(now);
            Assert.Equal(1, store.RemoveRootGames("parent", rootPath, now));
            Assert.Equal("removed", store.TryGetGame("removed")!.Membership);
            Assert.Equal("active", store.TryGetGame("kept")!.Membership);
            Assert.Equal("active", store.TryGetGame("sibling")!.Membership);
            Assert.Empty(store.ListCandidates());
            Assert.Single(store.ReadRoots());
            Assert.False(store.UpsertRegisteredCandidate(candidate).Stored);
            Assert.Equal("game-file", File.ReadAllText(executable));
            store.UpsertRoot(new PersistedRoot("again", rootPath, 1, now), now);
            Assert.True(store.UpsertRegisteredCandidate(candidate).Stored);
            var accepted = store.AcceptCandidate("candidate", 1, SampleGame("new-game", "重新添加") with { RootPath = executable }, "", now);
            Assert.Equal("accepted", accepted.Status);
            Assert.Equal("active", store.TryGetGame(accepted.GameId)!.Membership);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }

    [Theory]
    [InlineData(@"F:\", @"F:\游戏\Game.exe", true)]
    [InlineData(@"F:\Games", @"f:\games\a.exe", true)]
    [InlineData(@"F:\Games", @"F:\GamesOther\a.exe", false)]
    public void RootContainment_RespectsDirectoryBoundaries(string root, string path, bool expected)
        => Assert.Equal(expected, RuntimeStateStore.ContainsPath(root, path));

    [Fact]
    public async Task RuntimeState_RootsProfilesAttemptsJobs_RoundTrip()
    {
        var dataDir = FreshDataDir("runtime-state");
        try
        {
            var init = await SqliteLibraryStore.InitializeAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(init.IsOpened, init.Detail);
            await using var store = init.Store!;
            var utcNow = DateTime.UtcNow;

            store.UpsertRoot(new PersistedRoot("root-1", @"C:\games", 1, utcNow), utcNow);
            store.InsertGame(SampleGame("game-1", "游戏一"));
            store.UpsertProfile(new PersistedProfile(
                "profile-1", "game-1", @"C:\games\game.exe", ["--x"], @"C:\games",
                null, true, 1, utcNow, utcNow), utcNow);
            store.UpsertLaunchAttempt(new PersistedLaunchAttempt(
                "attempt-1", "key-1", "game-1", "profile-1", null,
                "processCreated", @"C:\games\game.exe", [], @"C:\games",
                1234, utcNow, null, null, null, utcNow));
            store.UpsertJobRecord(new PersistedJobRecord("job-1", "scan", "running", utcNow, utcNow, null, null));

            // 模拟重启：重新打开同一个库。
            await store.DisposeAsync();
            var reopened = await SqliteLibraryStore.TryOpenAsync(dataDir, Options(), CancellationToken.None);
            Assert.True(reopened.IsOpened, reopened.Detail);
            await using var store2 = reopened.Store!;

            var interrupted = store2.MarkInterruptedJobs(DateTime.UtcNow);
            Assert.Equal(1, interrupted);

            var roots = store2.ReadRoots();
            var root = Assert.Single(roots);
            Assert.Equal("root-1", root.RootId);
            Assert.Equal(@"C:\games", root.PhysicalPath);

            var profile = Assert.Single(store2.ReadProfiles());
            Assert.Equal("profile-1", profile.ProfileId);
            Assert.True(profile.IsDefault);
            Assert.Equal(["--x"], profile.Arguments);

            var attempt = Assert.Single(store2.ReadLaunchAttempts());
            Assert.Equal("attempt-1", attempt.AttemptId);
            Assert.Equal(1234, attempt.ProcessId);

            var job = Assert.Single(store2.ReadJobRecords());
            Assert.Equal("interrupted", job.State);
        }
        finally
        {
            Cleanup(dataDir);
        }
    }
}
