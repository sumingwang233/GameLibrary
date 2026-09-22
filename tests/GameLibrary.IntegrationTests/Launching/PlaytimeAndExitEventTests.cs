using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Launching;

/// <summary>
/// feat-1/feat-2 端到端（经真实管道，夹具已接 WirePersistence——attempt 落库与
/// launch.exited 发布走生产路径）：游戏进程退出后 attempt 主动完成（不依赖
/// 有人查询），launch.exited 事件携带 gameId/exitCode/durationSeconds 且同一
/// attempt 只发一次；games.get/games.list 返回 playtimeMinutes/lastPlayedUtc。
/// </summary>
public sealed class PlaytimeAndExitEventTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public PlaytimeAndExitEventTests(PipeServerFixture fixture)
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
            clientName: "playtime-test",
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

    private async Task<string> CreateManualGameAsync()
    {
        // 桩所在测试输出目录注册为库根（幂等）：games.create 的 sourcePath 用原位桩 exe
        // （.NET 应用单拷 exe 会缺 runtimeconfig 起不来，退出码非 0）。
        await InvokeAsync("roots.add", new { root = AppContext.BaseDirectory });
        await InvokeAsync("roots.add", new { root = @"D:\Official\GameLibrary\artifacts\test-runs" });

        var created = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"create-{Guid.NewGuid():N}",
            sourcePath = StubExe,
            title = "游玩统计测试",
        });
        Assert.True(created.Ok, created.Error?.Message);
        return created.Data.GetProperty("gameId").GetString()!;
    }

    [Fact]
    public async Task GameExit_PublishesLaunchExitedOnce_AndFeedsPlaytimeFields()
    {
        var gameId = await CreateManualGameAsync();

        // 启动前：从未玩过——games.get 返回 0/null，字段形状先立住。
        var before = await InvokeAsync("games.get", new { gameId });
        Assert.True(before.Ok, before.Error?.Message);
        Assert.Equal(0, before.Data.GetProperty("playtimeMinutes").GetInt64());
        Assert.Equal(JsonValueKind.Null, before.Data.GetProperty("lastPlayedUtc").ValueKind);

        var gameDir = NewRunDir("playtime-launch");
        Directory.CreateDirectory(gameDir);
        var createdProfile = await InvokeAsync("profiles.create", new
        {
            idempotencyKey = $"profile-{gameId}",
            gameId,
            executablePath = StubExe,
            argv = Array.Empty<string>(),
            cwd = gameDir,
        });
        Assert.True(createdProfile.Ok, createdProfile.Error?.Message);
        var profileId = createdProfile.Data.GetProperty("profileId").GetString()!;

        var execute = await InvokeAsync("launch.execute", new { idempotencyKey = $"exec-{gameId}", profileId });
        Assert.True(execute.Ok, execute.Error?.Message);
        Assert.Equal("processCreated", execute.Data.GetProperty("state").GetString());
        var attemptId = execute.Data.GetProperty("attemptId").GetString()!;

        // 主动完成：不主动调用 launch.status，仅轮询事件流——验证 Exited 订阅路径。
        var exitEvent = await WaitForLaunchExitedAsync(attemptId);
        Assert.Equal(gameId, exitEvent.GetProperty("gameId").GetString());
        Assert.Equal(0, exitEvent.GetProperty("exitCode").GetInt32());
        Assert.True(exitEvent.GetProperty("durationSeconds").GetInt64() >= 0, "会话时长应非负");

        // 事件已落地后，attempt 状态经任意查询路径都是终态。
        var status = await InvokeAsync("launch.status", new { attemptId });
        Assert.Equal("exited", status.Data.GetProperty("state").GetString());

        // games.get：lastPlayedUtc 已有值（本次会话完成落库）；stub 秒退，分钟数为 0。
        var after = await InvokeAsync("games.get", new { gameId });
        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(0, after.Data.GetProperty("playtimeMinutes").GetInt64());
        Assert.Equal(JsonValueKind.String, after.Data.GetProperty("lastPlayedUtc").ValueKind);

        // games.list：同一口径返回两个字段。
        var list = await InvokeAsync("games.list", new { search = "游玩统计测试" });
        Assert.True(list.Ok);
        var item = list.Data.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("gameId").GetString() == gameId);
        Assert.Equal(0, item.GetProperty("playtimeMinutes").GetInt64());
        Assert.Equal(JsonValueKind.String, item.GetProperty("lastPlayedUtc").ValueKind);

        // 同一 attempt 只发一次 launch.exited（Exited 回调与惰性刷新并发也不得双发）。
        await Task.Delay(500);
        var allEvents = await InvokeAsync("events.read", new { limit = 4096 });
        Assert.True(allEvents.Ok);
        var matches = allEvents.Data.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "launch.exited"
                && e.GetProperty("payload").GetProperty("attemptId").GetString() == attemptId)
            .ToArray();
        Assert.Single(matches);
    }

    private async Task<JsonElement> WaitForLaunchExitedAsync(string attemptId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var events = await InvokeAsync("events.read", new { limit = 256 });
            Assert.True(events.Ok, events.Error?.Message);
            var match = events.Data.GetProperty("items").EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("type").GetString() == "launch.exited"
                    && e.GetProperty("payload").GetProperty("attemptId").GetString() == attemptId);
            if (match.ValueKind != JsonValueKind.Undefined)
            {
                return match.GetProperty("payload").Clone();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"启动尝试 {attemptId} 未在 15 秒内产生 launch.exited 事件");
    }
}
