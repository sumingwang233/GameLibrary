using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Shell;
using Xunit;

namespace GameLibrary.IntegrationTests.Manual;

/// <summary>手动入库走真实命名管道；所有样本只在隔离测试数据目录内。</summary>
public sealed class ManualGameTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ManualGameTests(PipeServerFixture fixture) => _fixture = fixture;

    private string DataDir => Path.Combine(
        @"D:\Official\GameLibrary\artifacts\test-runs", _fixture.TestId, "data");

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object parameters)
    {
        await using var client = await HostConnection.ConnectAsync(DataDir, "manual-test", CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = $"manual-{Guid.NewGuid():N}",
            OperationId = operationId,
            Parameters = json.RootElement.Clone(),
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TwoStandaloneFilesInOneDirectory_StayDistinctAndAvailable()
    {
        var root = Path.Combine(DataDir, "manual-fixture");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "First.EXE");
        var second = Path.Combine(root, "Second.exe");
        File.WriteAllText(first, "test-stub");
        File.WriteAllText(second, "test-stub");

        var addedRoot = await InvokeAsync("roots.add", new { root });
        Assert.True(addedRoot.Ok, addedRoot.Error?.Message);
        var priorCount = (await InvokeAsync("games.list", new { })).Data.GetProperty("total").GetInt32();

        var key = $"manual-create-{Guid.NewGuid():N}";
        var createFirst = await InvokeAsync("games.create", new
        {
            idempotencyKey = key,
            sourcePath = first,
            title = "第一款",
        });
        Assert.True(createFirst.Ok, createFirst.Error?.Message);
        Assert.Equal("manualFile", createFirst.Data.GetProperty("kind").GetString());
        Assert.Equal("第一款", createFirst.Data.GetProperty("title").GetString());
        Assert.Equal(first, createFirst.Data.GetProperty("entryPath").GetString());
        var firstId = createFirst.Data.GetProperty("gameId").GetString()!;
        var firstCard = await InvokeAsync("games.get", new { gameId = firstId });
        Assert.Equal("user", firstCard.Data.GetProperty("titleSource").GetString());
        Assert.Equal("available", firstCard.Data.GetProperty("availability").GetString());
        var profile = await InvokeAsync("profiles.create", new
        {
            idempotencyKey = $"manual-profile-{Guid.NewGuid():N}",
            gameId = firstId,
            executablePath = first,
            argv = Array.Empty<string>(),
            cwd = root,
            isDefault = true,
        });
        Assert.True(profile.Ok, profile.Error?.Message);
        var profiles = await InvokeAsync("profiles.list", new { gameId = firstId });
        Assert.Single(profiles.Data.GetProperty("items").EnumerateArray());

        var retried = await InvokeAsync("games.create", new
        {
            idempotencyKey = key,
            sourcePath = first,
            title = "第一款",
        });
        Assert.True(retried.Ok, retried.Error?.Message);
        Assert.Equal(createFirst.Data.GetProperty("gameId").GetString(),
            retried.Data.GetProperty("gameId").GetString());

        var createSecond = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = second,
        });
        Assert.True(createSecond.Ok, createSecond.Error?.Message);
        Assert.NotEqual(createFirst.Data.GetProperty("gameId").GetString(),
            createSecond.Data.GetProperty("gameId").GetString());
        Assert.Equal("Second", createSecond.Data.GetProperty("title").GetString());

        var duplicate = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = first.ToLowerInvariant(),
        });
        Assert.False(duplicate.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, duplicate.Error!.Code);

        var report = ReconcileService.CheckGames(_fixture.State.Library.Store!, DateTime.UtcNow);
        Assert.Equal(priorCount + 2, report.AvailableCount);
        var list = await InvokeAsync("games.list", new { });
        Assert.Equal(priorCount + 2, list.Data.GetProperty("total").GetInt32());

        var removeKey = $"manual-remove-{Guid.NewGuid():N}";
        var removeParameters = new
        {
            idempotencyKey = removeKey,
            gameId = firstId,
            expectedRevision = firstCard.Data.GetProperty("revision").GetInt32(),
        };
        var staleRemove = await InvokeAsync("games.remove", new
        {
            idempotencyKey = $"manual-stale-remove-{Guid.NewGuid():N}",
            gameId = firstId,
            expectedRevision = removeParameters.expectedRevision + 1,
        });
        Assert.False(staleRemove.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, staleRemove.Error!.Code);
        var removed = await InvokeAsync("games.remove", removeParameters);
        Assert.True(removed.Ok, removed.Error?.Message);
        Assert.False(removed.Data.GetProperty("filesDeleted").GetBoolean());
        Assert.True(File.Exists(first));
        Assert.Equal(priorCount + 1,
            (await InvokeAsync("games.list", new { })).Data.GetProperty("total").GetInt32());
        var removedCard = await InvokeAsync("games.get", new { gameId = firstId });
        Assert.Equal("removed", removedCard.Data.GetProperty("membership").GetString());
        var ignores = await InvokeAsync("ignores.list", new { });
        Assert.Contains(ignores.Data.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("path").GetString() == first
            && item.GetProperty("reason").GetString() == "games.remove");
        var replay = await InvokeAsync("games.remove", removeParameters);
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.Equal(removed.Data.GetProperty("ignoreId").GetString(),
            replay.Data.GetProperty("ignoreId").GetString());
        var rejectedLaunch = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = $"removed-launch-{Guid.NewGuid():N}",
            profileId = profile.Data.GetProperty("profileId").GetString(),
        });
        Assert.False(rejectedLaunch.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, rejectedLaunch.Error!.Code);
    }

    [Fact]
    public async Task DirectoryEntry_RequiresRegisteredRoot_AndDoesNotPretendShortcutIsExe()
    {
        var root = Path.Combine(DataDir, "manual-parent");
        var gameDir = Path.Combine(root, "Unknown Game");
        Directory.CreateDirectory(gameDir);
        var outside = Path.Combine(DataDir, "outside.exe");
        File.WriteAllText(outside, "test-stub");

        var addedRoot = await InvokeAsync("roots.add", new { root });
        Assert.True(addedRoot.Ok, addedRoot.Error?.Message);
        var create = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = gameDir,
            title = "未识别游戏",
        });
        Assert.True(create.Ok, create.Error?.Message);
        Assert.Equal("manualDirectory", create.Data.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, create.Data.GetProperty("entryPath").ValueKind);

        var extensionLikeDirectory = Path.Combine(root, "Folder.exe");
        Directory.CreateDirectory(extensionLikeDirectory);
        var directoryCard = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = extensionLikeDirectory,
        });
        Assert.True(directoryCard.Ok, directoryCard.Error?.Message);
        Assert.Equal("manualDirectory", directoryCard.Data.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null,
            directoryCard.Data.GetProperty("launchSuggestion").ValueKind);

        var outsideResult = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = outside,
        });
        Assert.False(outsideResult.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, outsideResult.Error!.Code);

        var shortcut = Path.Combine(root, "NotYetSupported.lnk");
        File.WriteAllText(shortcut, "not-a-real-link");
        var shortcutResult = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-create-{Guid.NewGuid():N}",
            sourcePath = shortcut,
        });
        Assert.False(shortcutResult.Ok);
        Assert.Equal(ErrorCodes.InvalidPath, shortcutResult.Error!.Code);
    }

    [Fact]
    public async Task WindowsShortcut_PreservesVerifiedTargetArgumentsAndWorkingDirectory()
    {
        var root = Path.Combine(DataDir, "shortcut-fixture");
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Game.exe");
        var link = Path.Combine(root, "Play.lnk");
        File.WriteAllText(exe, "test-stub");
        ShellLinkInterop.RunOnSta(() =>
        {
            var shellLink = ShellLinkInterop.CreateShellLink();
            shellLink.SetPath(exe);
            shellLink.SetArguments("--mode \"中文 路径\"");
            shellLink.SetWorkingDirectory(root);
            ((IPersistFile)shellLink).Save(link, fRemember: false);
            return true;
        });

        var addedRoot = await InvokeAsync("roots.add", new { root });
        Assert.True(addedRoot.Ok, addedRoot.Error?.Message);
        var created = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-link-{Guid.NewGuid():N}",
            sourcePath = link,
        });
        Assert.True(created.Ok, created.Error?.Message);
        Assert.Equal("manualShortcut", created.Data.GetProperty("kind").GetString());
        Assert.Equal(exe, created.Data.GetProperty("entryPath").GetString());
        var suggestion = created.Data.GetProperty("launchSuggestion");
        Assert.Equal(exe, suggestion.GetProperty("executablePath").GetString());
        Assert.Equal(root, suggestion.GetProperty("cwd").GetString());
        Assert.Equal(new[] { "--mode", "中文 路径" },
            suggestion.GetProperty("argv").EnumerateArray().Select(arg => arg.GetString()).ToArray());

        var gameId = created.Data.GetProperty("gameId").GetString();
        var profile = await InvokeAsync("profiles.create", new
        {
            idempotencyKey = $"manual-linkprofile-{gameId}",
            gameId,
            executablePath = suggestion.GetProperty("executablePath").GetString(),
            argv = suggestion.GetProperty("argv").EnumerateArray()
                .Select(arg => arg.GetString()).ToArray(),
            cwd = suggestion.GetProperty("cwd").GetString(),
            isDefault = true,
        });
        Assert.True(profile.Ok, profile.Error?.Message);
        Assert.Single((await InvokeAsync("profiles.list", new { gameId })).Data
            .GetProperty("items").EnumerateArray());

        var report = ReconcileService.CheckGames(_fixture.State.Library.Store!, DateTime.UtcNow);
        Assert.True(report.AvailableCount >= 1);

        var outsideTarget = Path.Combine(DataDir, "not-authorized.exe");
        var outsideLink = Path.Combine(root, "Outside.lnk");
        File.WriteAllText(outsideTarget, "test-stub");
        ShellLinkInterop.RunOnSta(() =>
        {
            var shellLink = ShellLinkInterop.CreateShellLink();
            shellLink.SetPath(outsideTarget);
            ((IPersistFile)shellLink).Save(outsideLink, fRemember: false);
            return true;
        });
        var rejected = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-link-outside-{Guid.NewGuid():N}",
            sourcePath = outsideLink,
        });
        Assert.False(rejected.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, rejected.Error!.Code);
    }

    [Fact]
    public async Task CreateAndRelink_ComputeAndRefreshFingerprint()
    {
        var root = Path.Combine(DataDir, "fingerprint-fixture");
        var gameDir = Path.Combine(root, "MyGame");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "a.bin"), "alpha-content");
        File.WriteAllText(Path.Combine(gameDir, "b.bin"), "beta-content");

        var addedRoot = await InvokeAsync("roots.add", new { root });
        Assert.True(addedRoot.Ok, addedRoot.Error?.Message);

        // games.create 建卡后指纹存在（手动目录卡：未知引擎回退 = 根目录最小 ≤1MiB 文件充实）。
        var created = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"manual-fp-create-{Guid.NewGuid():N}",
            sourcePath = gameDir,
            title = "指纹游戏",
        });
        Assert.True(created.Ok, created.Error?.Message);
        var gameId = created.Data.GetProperty("gameId").GetString()!;
        var detail = await InvokeAsync("games.get", new { gameId });
        Assert.True(detail.Ok, detail.Error?.Message);
        var fingerprint = detail.Data.GetProperty("fingerprint");
        Assert.Equal(GameLibrary.Domain.Identity.FingerprintPolicy.StrategyVersion,
            fingerprint.GetProperty("strategyVersion").GetInt32());
        Assert.Equal(2, fingerprint.GetProperty("entryCount").GetInt32());
        var computedFirst = fingerprint.GetProperty("computedUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(computedFirst));
        // 新建游戏无同指纹对手：similarTo 空且不含自身。
        Assert.Equal(0, detail.Data.GetProperty("similarTo").GetArrayLength());
        var revision = detail.Data.GetProperty("revision").GetInt32();

        // 改名拷贝目录（同字节内容）→ relink 重算指纹：computed_utc 更新、条目反映新根。
        await Task.Delay(50);
        var copyDir = Path.Combine(root, "MyGame-Renamed");
        Directory.CreateDirectory(copyDir);
        foreach (var file in new[] { "a.bin", "b.bin" })
        {
            File.Copy(Path.Combine(gameDir, file), Path.Combine(copyDir, file));
        }

        var relinked = await InvokeAsync("games.relink", new
        {
            idempotencyKey = $"manual-fp-relink-{gameId}",
            gameId,
            newPath = copyDir,
            expectedRevision = revision,
        });
        Assert.True(relinked.Ok, relinked.Error?.Message);

        var afterRelink = await InvokeAsync("games.get", new { gameId });
        Assert.True(afterRelink.Ok, afterRelink.Error?.Message);
        var fingerprintAfter = afterRelink.Data.GetProperty("fingerprint");
        Assert.Equal(2, fingerprintAfter.GetProperty("entryCount").GetInt32());
        var computedSecond = fingerprintAfter.GetProperty("computedUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(computedSecond));
        Assert.NotEqual(computedFirst, computedSecond);
        // relink 后仍无对手：similarTo 空且不含自身。
        Assert.Equal(0, afterRelink.Data.GetProperty("similarTo").GetArrayLength());
    }

    [Fact]
    public async Task IdenticalStandaloneLaunchers_MatchedOne_IsBlockedByGate()
    {
        // 共用启动器场景：两个独立 EXE 字节完全相同 → 单条 hashed 指纹 similarity=1.0，
        // 但 matched=1 < 2 → 宿主门控拦截（防单条目巧合误报）。
        var root = Path.Combine(DataDir, "launcher-twin-fixture");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "LauncherA.exe");
        var second = Path.Combine(root, "LauncherB.exe");
        File.WriteAllText(first, "same-launcher-bytes");
        File.WriteAllText(second, "same-launcher-bytes");

        var addedRoot = await InvokeAsync("roots.add", new { root });
        Assert.True(addedRoot.Ok, addedRoot.Error?.Message);

        var createFirst = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"launcher-first-{Guid.NewGuid():N}",
            sourcePath = first,
        });
        Assert.True(createFirst.Ok, createFirst.Error?.Message);
        var createSecond = await InvokeAsync("games.create", new
        {
            idempotencyKey = $"launcher-second-{Guid.NewGuid():N}",
            sourcePath = second,
        });
        Assert.True(createSecond.Ok, createSecond.Error?.Message);

        var secondId = createSecond.Data.GetProperty("gameId").GetString()!;
        var detail = await InvokeAsync("games.get", new { gameId = secondId });
        Assert.True(detail.Ok, detail.Error?.Message);
        // 指纹存在（单条目）但 similarTo 必须为空——matched≥2 门控生效，similarity=1.0 也不放行。
        Assert.Equal(1, detail.Data.GetProperty("fingerprint").GetProperty("entryCount").GetInt32());
        Assert.DoesNotContain(detail.Data.GetProperty("similarTo").EnumerateArray(),
            s => s.GetProperty("gameId").GetString() == createFirst.Data.GetProperty("gameId").GetString());
        Assert.Equal(0, detail.Data.GetProperty("similarTo").GetArrayLength());
    }
}
