using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.States;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>T17 Reconcile/Identity：可用性分级核对、games.relink 仅改 DB、副本/备份提示。</summary>
public sealed class ReconcileIdentityTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ReconcileIdentityTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t17-test",
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

    private GameCard InsertGame(string rootPath, string title = "T17 游戏")
    {
        var store = _fixture.State.Library.Store!;
        var game = new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = title,
            RootPath = rootPath,
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertGame(game);
        return game;
    }

    [Fact]
    public void Reconcile_PresentGame_MarksAvailable()
    {
        var dir = NewGameDir();
        Directory.CreateDirectory(dir);
        var game = InsertGame(dir);
        try
        {
            var report = GameLibrary.Host.Scanning.ReconcileService.CheckGames(
                _fixture.State.Library.Store!, DateTime.UtcNow);

            var reloaded = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
            Assert.Equal("available", reloaded.Availability);
            Assert.Null(reloaded.MissingSinceUtc);
            Assert.Equal(1, report.AvailableCount);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Reconcile_FirstMissing_SuspectedThenAfter60s_Missing()
    {
        var dir = NewGameDir();
        var game = InsertGame(dir);
        try
        {
            // 目录从未出现：第一次成功完整核对 → suspectedMissing（不直接 missing，ID-05）。
            var first = GameLibrary.Host.Scanning.ReconcileService.CheckGames(
                _fixture.State.Library.Store!, DateTime.UtcNow);
            var afterFirst = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
            Assert.Equal("suspectedMissing", afterFirst.Availability);
            Assert.NotNull(afterFirst.MissingSinceUtc);

            // 间隔不足 60 秒：仍是 suspectedMissing。
            GameLibrary.Host.Scanning.ReconcileService.CheckGames(
                _fixture.State.Library.Store!, afterFirst.MissingSinceUtc!.Value.AddSeconds(30));
            var afterShort = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
            Assert.Equal("suspectedMissing", afterShort.Availability);

            // 间隔 ≥60 秒 → missing；且 Revision 不因可用性写回而变化（扫描器维护字段）。
            GameLibrary.Host.Scanning.ReconcileService.CheckGames(
                _fixture.State.Library.Store!, afterFirst.MissingSinceUtc!.Value.AddSeconds(61));
            var afterLong = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
            Assert.Equal("missing", afterLong.Availability);
            Assert.Equal(afterFirst.Revision, afterLong.Revision);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Reconcile_RootOffline_DoesNotAccumulateMissing()
    {
        // 目录在另一个盘根形态下无法模拟真实换盘；用"盘根不存在"的构造路径模拟离线。
        var game = InsertGame(@"Q:\offline-root\game-dir");

        GameLibrary.Host.Scanning.ReconcileService.CheckGames(
            _fixture.State.Library.Store!, DateTime.UtcNow);
        var afterOffline = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
        Assert.Equal("offline", afterOffline.Availability);

        // 离线持续"90 秒"后仍只算 offline；恢复在线后重新计数（回到 suspectedMissing 而非 missing）。
        GameLibrary.Host.Scanning.ReconcileService.CheckGames(
            _fixture.State.Library.Store!, DateTime.UtcNow.AddSeconds(90));
        Assert.Equal("offline", _fixture.State.Library.Store!.TryGetGame(game.GameId)!.Availability);
    }

    [Fact]
    public async Task Relink_MovesDbBindingOnly_WithoutTouchingFiles()
    {
        var rootsRoot = NewGameDir();
        Directory.CreateDirectory(rootsRoot);
        _fixture.State.Roots.Add(rootsRoot);
        var oldDir = Path.Combine(rootsRoot, "old-location");
        var newDir = Path.Combine(rootsRoot, "new-location");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "Game.exe"), "stub");
        var game = InsertGame(oldDir);
        try
        {
            var envelope = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t17-{Guid.NewGuid():N}",
                gameId = game.GameId,
                newPath = newDir,
                expectedRevision = 1,
            });

            Assert.True(envelope.Ok, envelope.Error?.Message);
            Assert.Equal(oldDir, envelope.Data.GetProperty("previousRootPath").GetString());
            Assert.Equal(newDir, envelope.Data.GetProperty("rootPath").GetString());
            Assert.Equal("available", envelope.Data.GetProperty("availability").GetString());

            // 数据库绑定已迁移；磁盘上两个目录原样（未移动/删除）。
            var reloaded = _fixture.State.Library.Store!.TryGetGame(game.GameId)!;
            Assert.Equal(newDir, reloaded.RootPath);
            Assert.True(Directory.Exists(oldDir), "relink 不得移动或删除旧目录");
            Assert.True(File.Exists(Path.Combine(newDir, "Game.exe")));
        }
        finally
        {
            TryDeleteDir(rootsRoot);
        }
    }

    [Fact]
    public async Task Relink_OutsideRegisteredRoots_IsRejected()
    {
        var game = InsertGame(NewGameDir());

        var envelope = await InvokeAsync("games.relink", new
        {
            idempotencyKey = $"t17-{Guid.NewGuid():N}",
            gameId = game.GameId,
            newPath = @"D:\somewhere-else\game",
            expectedRevision = 1,
        });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, envelope.Error!.Code);
    }

    [Fact]
    public async Task Relink_ConflictingBinding_IsRejected()
    {
        var rootsRoot = NewGameDir();
        Directory.CreateDirectory(rootsRoot);
        _fixture.State.Roots.Add(rootsRoot);
        var dirA = Path.Combine(rootsRoot, "game-a");
        var dirB = Path.Combine(rootsRoot, "game-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var gameA = InsertGame(dirA, "游戏 A");
        InsertGame(dirB, "游戏 B");
        try
        {
            var envelope = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t17-{Guid.NewGuid():N}",
                gameId = gameA.GameId,
                newPath = dirB,
                expectedRevision = 1,
            });

            Assert.False(envelope.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, envelope.Error!.Code);
            Assert.Contains("已绑定", envelope.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDir(rootsRoot);
        }
    }

    [Fact]
    public async Task Relink_MissingNewPath_IsRootOffline()
    {
        var rootsRoot = NewGameDir();
        Directory.CreateDirectory(rootsRoot);
        _fixture.State.Roots.Add(rootsRoot);
        var oldDir = Path.Combine(rootsRoot, "old");
        Directory.CreateDirectory(oldDir);
        var game = InsertGame(oldDir);
        var notYet = Path.Combine(rootsRoot, "not-created");
        try
        {
            var envelope = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t17-{Guid.NewGuid():N}",
                gameId = game.GameId,
                newPath = notYet,
                expectedRevision = 1,
            });

            Assert.False(envelope.Ok);
            Assert.Equal(ErrorCodes.RootOffline, envelope.Error!.Code);
            Assert.True(envelope.Error.Retryable == true);
        }
        finally
        {
            TryDeleteDir(rootsRoot);
        }
    }

    [Fact]
    public async Task Relink_RevisionConflict_ReportsCurrent()
    {
        var rootsRoot = NewGameDir();
        Directory.CreateDirectory(rootsRoot);
        _fixture.State.Roots.Add(rootsRoot);
        var oldDir = Path.Combine(rootsRoot, "old");
        var newDir = Path.Combine(rootsRoot, "new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        var game = InsertGame(oldDir);
        try
        {
            var envelope = await InvokeAsync("games.relink", new
            {
                idempotencyKey = $"t17-{Guid.NewGuid():N}",
                gameId = game.GameId,
                newPath = newDir,
                expectedRevision = 99,
            });

            Assert.False(envelope.Ok);
            Assert.Equal(ErrorCodes.RevisionConflict, envelope.Error!.Code);
            Assert.Equal(1, envelope.Error.CurrentRevision);
        }
        finally
        {
            TryDeleteDir(rootsRoot);
        }
    }

    private static string NewGameDir() =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"t17-{Guid.NewGuid():N}");

    private static void TryDeleteDir(string path)
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
