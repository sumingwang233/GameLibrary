using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Launching;

/// <summary>
/// T06 启动计划/执行器（经真实管道）：profile→plan→execute→status→history，
/// 幂等键重放、全入口互斥、Profile Revision 使旧计划失效。进程一律用 TestProcessStub。
/// </summary>
public sealed class LaunchOperationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public LaunchOperationTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private static string NewRunDir(string prefix) =>
        Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"{prefix}-{Guid.NewGuid():N}");

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "launch-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters);
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    private async Task<string> CreateProfileAsync(string gameId)
    {
        var runDir = NewRunDir("launch-profile");
        Directory.CreateDirectory(runDir);
        var created = await InvokeAsync("profiles.create", new
        {
            gameId,
            executablePath = StubExe,
            argv = new[] { "--game", gameId },
            cwd = runDir,
        });
        Assert.True(created.Ok, created.Error?.Message);
        return created.Data.GetProperty("profileId").GetString()!;
    }

    [Fact]
    public async Task LaunchFlow_PlanExecuteStatus_ReachesExitedAndRecordsArgv()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var profileId = await CreateProfileAsync(gameId);

        var plan = await InvokeAsync("launch.plan", new { gameId, profileId });
        Assert.True(plan.Ok, plan.Error?.Message);
        Assert.Equal(StubExe, plan.Data.GetProperty("executablePath").GetString());
        Assert.Equal(gameId, plan.Data.GetProperty("argv")[1].GetString());
        var planId = plan.Data.GetProperty("planId").GetString()!;

        var execute = await InvokeAsync("launch.execute", new { idempotencyKey = $"key-{planId}", planId });
        Assert.True(execute.Ok, execute.Error?.Message);
        Assert.Equal("processCreated", execute.Data.GetProperty("state").GetString());
        Assert.True(execute.Data.GetProperty("processId").GetInt32() > 0);
        var attemptId = execute.Data.GetProperty("attemptId").GetString()!;

        // 观察不等待：轮询到退出（stub 立即退出，窗口很小但非零）。
        await WaitForExitAsync(attemptId);
        var status = await InvokeAsync("launch.status", new { attemptId });
        Assert.True(status.Ok, status.Error?.Message);
        Assert.Equal("exited", status.Data.GetProperty("state").GetString());
        Assert.Equal(0, status.Data.GetProperty("exitCode").GetInt32());

        var history = await InvokeAsync("launch.history", new { gameId });
        Assert.True(history.Ok);
        Assert.Equal(1, history.Data.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task LaunchExecute_SameIdempotencyKey_ReplaysSameAttemptWithoutSecondProcess()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var profileId = await CreateProfileAsync(gameId);

        var first = await InvokeAsync("launch.execute", new { idempotencyKey = "replay-key", profileId });
        Assert.True(first.Ok, first.Error?.Message);
        var attemptId = first.Data.GetProperty("attemptId").GetString()!;

        var replay = await InvokeAsync("launch.execute", new { idempotencyKey = "replay-key", profileId });
        Assert.True(replay.Ok);
        Assert.Equal(attemptId, replay.Data.GetProperty("attemptId").GetString());

        await WaitForExitAsync(attemptId);

        var history = await InvokeAsync("launch.history", new { gameId });
        Assert.Equal(1, history.Data.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task LaunchExecute_ConcurrentLaunchForSameGame_IsRejectedByMutualExclusion()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var runDir = NewRunDir("launch-mutex");
        Directory.CreateDirectory(runDir);
        var created = await InvokeAsync("profiles.create", new
        {
            gameId,
            executablePath = StubExe,
            argv = new[] { "--hold-ms", "3000" },
            cwd = runDir,
        });
        var profileId = created.Data.GetProperty("profileId").GetString()!;

        var first = await InvokeAsync("launch.execute", new { idempotencyKey = "mutex-1", profileId });
        Assert.True(first.Ok, first.Error?.Message);
        var firstAttemptId = first.Data.GetProperty("attemptId").GetString()!;

        var second = await InvokeAsync("launch.execute", new { idempotencyKey = "mutex-2", profileId });
        Assert.False(second.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, second.Error!.Code);
        Assert.Contains("互斥", second.Error.Message, StringComparison.Ordinal);

        await WaitForExitAsync(firstAttemptId);

        var third = await InvokeAsync("launch.execute", new { idempotencyKey = "mutex-3", profileId });
        Assert.True(third.Ok, third.Error?.Message);
        await WaitForExitAsync(third.Data.GetProperty("attemptId").GetString()!);
    }

    [Fact]
    public async Task LaunchExecute_OldPlanAfterProfileUpdate_FailsWithPlanStale()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var profileId = await CreateProfileAsync(gameId);

        var plan = await InvokeAsync("launch.plan", new { gameId, profileId });
        var planId = plan.Data.GetProperty("planId").GetString()!;

        var updated = await InvokeAsync("profiles.update", new
        {
            profileId,
            executablePath = StubExe,
            argv = Array.Empty<string>(),
            cwd = Path.GetDirectoryName(StubExe),
            expectedRevision = 1,
        });
        Assert.True(updated.Ok, updated.Error?.Message);
        Assert.Equal(2, updated.Data.GetProperty("revision").GetInt32());

        var execute = await InvokeAsync("launch.execute", new { idempotencyKey = "stale-key", planId });
        Assert.False(execute.Ok);
        Assert.Equal(ErrorCodes.PlanStale, execute.Error!.Code);
    }

    [Fact]
    public async Task LaunchExecute_ExeDeletedAfterPlan_FailsWithToolMissing()
    {
        var gameId = $"game-{Guid.NewGuid():N}";
        var runDir = NewRunDir("launch-vanish");
        Directory.CreateDirectory(runDir);
        var tempExe = Path.Combine(runDir, "Vanishing.exe");
        File.Copy(StubExe, tempExe);

        var created = await InvokeAsync("profiles.create", new
        {
            gameId,
            executablePath = tempExe,
            argv = Array.Empty<string>(),
            cwd = runDir,
        });
        var profileId = created.Data.GetProperty("profileId").GetString()!;
        var plan = await InvokeAsync("launch.plan", new { gameId, profileId });
        var planId = plan.Data.GetProperty("planId").GetString()!;

        // 计划生成后启动目标消失：执行时校验失败，不产生进程。
        File.Delete(tempExe);
        var execute = await InvokeAsync("launch.execute", new { idempotencyKey = "vanish-key", planId });
        Assert.False(execute.Ok);
        Assert.Equal(ErrorCodes.ToolMissing, execute.Error!.Code);

        var history = await InvokeAsync("launch.history", new { gameId });
        Assert.True(history.Ok, history.Error?.Message);
    }

    private async Task WaitForExitAsync(string attemptId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await InvokeAsync("launch.status", new { attemptId });
            if (status.Data.GetProperty("state").GetString() == "exited")
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"启动尝试 {attemptId} 未在 15 秒内观察到退出");
    }
}
