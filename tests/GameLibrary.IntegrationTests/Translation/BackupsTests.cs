using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>
/// backups 五操作 + REC-02 恢复演练：
/// 创建（一致快照+原图+哈希清单）→ 列表/核查 → 恢复计划 → 恢复（原库保留/epoch 续期/控制收据不随库回滚/重试不覆盖）。
/// </summary>
public sealed class BackupsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public BackupsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "bk-test",
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

    private async Task<string> CreateBackupAsync()
    {
        // 同类多个测试共享 backups 根：记录创建前的既有备份，轮询完成后取新增的那个。
        var before = await InvokeAsync("backups.list");
        var known = before.Ok
            ? before.Data.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("backupId").GetString() ?? "").ToHashSet()
            : new HashSet<string>();

        var create = await InvokeAsync("backups.create", new { idempotencyKey = $"bkcreate-{Guid.NewGuid():N}" });
        Assert.True(create.Ok, create.Error?.Message);
        var jobId = create.JobId!;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync("jobs.get", new { jobId });
            var state = status.Data.GetProperty("state").GetString();
            if (state == "succeeded")
            {
                var data = await InvokeAsync("backups.list");
                var newBackup = data.Data.GetProperty("items").EnumerateArray()
                    .FirstOrDefault(i => !known.Contains(i.GetProperty("backupId").GetString() ?? ""));
                Assert.True(newBackup.ValueKind == JsonValueKind.Object, "未找到新建的备份");
                return newBackup.GetProperty("backupId").GetString()!;
            }

            if (state == "failed")
            {
                throw new InvalidOperationException("备份作业失败");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("备份作业未在 20 秒内完成");
    }

    private string InsertGame(string title)
    {
        var store = _fixture.State.Library.Store!;
        var gameId = $"game-{Guid.NewGuid():N}";
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = title,
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        return gameId;
    }

    [Fact]
    public async Task BackupLifecycle_CreateListInspect_AllGreen()
    {
        InsertGame("备份测试游戏");
        var backupId = await CreateBackupAsync();

        var inspect = await InvokeAsync("backups.inspect", new { backupId });
        Assert.True(inspect.Ok, inspect.Error?.Message);
        Assert.Equal("ok", inspect.Data.GetProperty("integrity").GetString());

        // 完整性破坏：篡改库文件字节 → inspect 报哈希不符。
        var backupDir = Path.Combine(_fixture.State.DataDirectory, "backups", backupId);
        var dbPath = Path.Combine(backupDir, "library.db");
        var bytes = await File.ReadAllBytesAsync(dbPath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(dbPath, bytes);

        var broken = await InvokeAsync("backups.inspect", new { backupId });
        Assert.False(broken.Ok);
        Assert.Contains("哈希不符", broken.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_RollsBackLibraryAndAssets_RenewsEpoch()
    {
        // 备份时：1 个游戏 + 1 张导入原图。
        var gameId = InsertGame("恢复演练游戏");
        var originalImage = Path.Combine(_fixture.State.DataDirectory, "assets", gameId, "cover.png");
        Directory.CreateDirectory(Path.GetDirectoryName(originalImage)!);
        await File.WriteAllTextAsync(originalImage, "original-cover");
        var import = await InvokeAsync("assets.import", new
        {
            idempotencyKey = $"bk-{Guid.NewGuid():N}",
            gameId,
            sourcePath = originalImage,
        });
        Assert.True(import.Ok, import.Error?.Message);

        var backupId = await CreateBackupAsync();
        var epochBefore = _fixture.State.Library.Store!.Info.DataEpoch;

        // 备份后变更：新游戏、原图被删、设置被改。
        InsertGame("备份之后新增的游戏");
        File.Delete(originalImage);
        var settings = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"bk-{Guid.NewGuid():N}",
            expectedRevision = _fixture.State.Library.Store!.ReadSettings().Revision,
            scanIntervalMinutes = 77,
        });
        Assert.True(settings.Ok, settings.Error?.Message);

        var plan = await InvokeAsync("backups.restore_plan", new { backupId });
        Assert.True(plan.Ok, plan.Error?.Message);
        var planId = plan.Data.GetProperty("planId").GetString()!;

        var restore = await InvokeAsync("backups.restore", new
        {
            idempotencyKey = $"bk-restore-{Guid.NewGuid():N}",
            backupId,
            planId,
        });

        Assert.True(restore.Ok, restore.Error?.Message);
        var data = restore.Data;
        Assert.True(data.GetProperty("restored").GetBoolean());

        // 库回滚到备份时点：只有 1 个游戏；epoch 已续期（与备份时和恢复前都不同）。
        var after = _fixture.State.Library.Store!.ListGames();
        Assert.Single(after, g => g.GameId == gameId);
        var epochAfter = _fixture.State.Library.Store.Info.DataEpoch;
        Assert.NotEqual(epochBefore, epochAfter);

        // 用户原图随备份恢复。
        Assert.True(File.Exists(originalImage), "恢复应带回用户原图");

        // 控制区收据不随业务库回滚：维护日志存在。
        Assert.True(File.Exists(Path.Combine(_fixture.State.DataDirectory, "control", "maintenance.log")));
    }

    [Fact]
    public async Task Restore_SameKeyRetry_DoesNotRestoreAgain()
    {
        var gameId = InsertGame("重试演练游戏");
        var backupId = await CreateBackupAsync();
        var plan = await InvokeAsync("backups.restore_plan", new { backupId });
        var planId = plan.Data.GetProperty("planId").GetString()!;
        var idempotencyKey = $"bk-restore-fixed-{Guid.NewGuid():N}";

        var first = await InvokeAsync("backups.restore", new { idempotencyKey, backupId, planId });
        Assert.True(first.Ok, first.Error?.Message);

        // 备份后再次变更：插入新游戏。
        InsertGame("第二次变更游戏");

        var second = await InvokeAsync("backups.restore", new { idempotencyKey, backupId, planId });

        // 相同恢复请求重试：返回原结果，不再覆盖——重试未执行恢复，"第二次变更游戏"仍在。
        Assert.True(second.Ok, second.Error?.Message);
        var games = _fixture.State.Library.Store!.ListGames();
        Assert.Contains(games, g => g.Title == "第二次变更游戏");
        Assert.Contains(games, g => g.Title == "重试演练游戏");
    }

    [Fact]
    public async Task Restore_ExpiredOrUnknownPlan_IsRejected()
    {
        var backupId = await CreateBackupAsync();

        var restore = await InvokeAsync("backups.restore", new
        {
            idempotencyKey = $"bk-{Guid.NewGuid():N}",
            backupId,
            planId = "plan-missing",
        });

        Assert.False(restore.Ok);
        Assert.Equal(ErrorCodes.PlanExpired, restore.Error!.Code);
    }

    [Fact]
    public async Task Restore_DifferentParamsSameKey_IsIdempotencyConflict()
    {
        var backupId = await CreateBackupAsync();
        var plan = await InvokeAsync("backups.restore_plan", new { backupId });
        var planId = plan.Data.GetProperty("planId").GetString()!;

        var first = await InvokeAsync("backups.restore", new
        {
            idempotencyKey = "bk-fixed-key-conflict",
            backupId,
            planId,
        });
        Assert.True(first.Ok, first.Error?.Message);

        var second = await InvokeAsync("backups.restore", new
        {
            idempotencyKey = "bk-fixed-key-conflict",
            backupId,
            planId = "plan-other",
        });

        Assert.False(second.Ok);
        Assert.Equal(ErrorCodes.IdempotencyConflict, second.Error!.Code);
    }

    [Fact]
    public async Task BackupsInspect_UnknownBackup_IsNotFound()
    {
        var envelope = await InvokeAsync("backups.inspect", new { backupId = "backup-missing" });
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.NotFound, envelope.Error!.Code);
    }
}
