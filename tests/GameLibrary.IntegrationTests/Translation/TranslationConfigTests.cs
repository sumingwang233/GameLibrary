using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Launching;
using GameLibrary.HostClient;
using GameLibrary.Infrastructure.Persistence;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>
/// T13 翻译配置三入口：translation.get/set、games.update（favorite）、
/// profiles set_default/remove/validate，以及 launch 的 Required 不回退。
/// </summary>
public sealed class TranslationConfigTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    private static string StubExe => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    public TranslationConfigTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "t13-test",
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

    private string InsertGame(bool toolNeedInherited)
    {
        var store = _fixture.State.Library.Store!;
        var gameId = $"game-{Guid.NewGuid():N}";
        store.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "T13 测试游戏",
            RootPath = $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\games\{gameId}",
            Kind = "GameRoot",
            Membership = "active",
            TranslationInherited = toolNeedInherited,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        return gameId;
    }

    private string CreateProfile(string gameId, string? toolId = null) =>
        _fixture.State.Launches.AddProfile(
            gameId,
            Environment.ProcessPath ?? throw new InvalidOperationException("无测试宿主进程"),
            [],
            AppContext.BaseDirectory,
            toolId).ProfileId;

    [Fact]
    public async Task TranslationGet_ToolNeedGame_ReportsInheritedRequired()
    {
        var gameId = InsertGame(toolNeedInherited: true);

        var envelope = await InvokeAsync("translation.get", new { gameId });

        Assert.True(envelope.Ok, envelope.Error?.Message);
        var data = envelope.Data;
        Assert.Equal("Auto", data.GetProperty("userOverride").GetString());
        Assert.Equal("Required", data.GetProperty("inherited").GetString());
        Assert.Equal("[toolNeed]", data.GetProperty("inheritedFrom").GetString());
        Assert.Equal("Required", data.GetProperty("effective").GetString());
        Assert.True(data.GetProperty("isRequired").GetBoolean());
    }

    [Fact]
    public async Task TranslationGet_UnknownGame_IsNotFound()
    {
        var envelope = await InvokeAsync("translation.get", new { gameId = "game-missing" });
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.NotFound, envelope.Error!.Code);
    }

    [Fact]
    public async Task TranslationSet_OverrideNotRequired_LowersEffectiveWithoutTouchingInherited()
    {
        var gameId = InsertGame(toolNeedInherited: true);
        var initial = await InvokeAsync("translation.get", new { gameId });
        var revision = initial.Data.GetProperty("revision").GetInt32();

        var set = await InvokeAsync("translation.set", new
        {
            idempotencyKey = $"t13-{Guid.NewGuid():N}",
            gameId,
            @override = "NotRequired",
            expectedRevision = revision,
        });

        Assert.True(set.Ok, set.Error?.Message);
        Assert.Equal("NotRequired", set.Data.GetProperty("userOverride").GetString());
        Assert.Equal("Required", set.Data.GetProperty("inherited").GetString());
        Assert.Equal("NotRequired", set.Data.GetProperty("effective").GetString());
        Assert.False(set.Data.GetProperty("isRequired").GetBoolean());
        Assert.Equal(revision + 1, set.Data.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task TranslationSet_RevisionConflict_ReportsCurrentRevision()
    {
        var gameId = InsertGame(toolNeedInherited: false);

        var set = await InvokeAsync("translation.set", new
        {
            idempotencyKey = $"t13-{Guid.NewGuid():N}",
            gameId,
            @override = "Required",
            expectedRevision = 999,
        });

        Assert.False(set.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, set.Error!.Code);
        Assert.Equal(1, set.Error.CurrentRevision);
    }

    [Fact]
    public async Task TranslationSet_InvalidOverride_IsRejected()
    {
        var gameId = InsertGame(false);

        var set = await InvokeAsync("translation.set", new
        {
            idempotencyKey = $"t13-{Guid.NewGuid():N}",
            gameId,
            @override = "Maybe",
            expectedRevision = 1,
        });

        Assert.False(set.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, set.Error!.Code);
    }

    [Fact]
    public async Task GamesUpdate_Favorite_TogglesAndBumpsRevision()
    {
        var gameId = InsertGame(false);

        var first = await InvokeAsync("games.update", new
        {
            idempotencyKey = $"t13fav-{Guid.NewGuid():N}",
            gameId,
            favorite = true,
            expectedRevision = 1,
        });
        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(first.Data.GetProperty("favorite").GetBoolean());
        Assert.Equal(2, first.Data.GetProperty("revision").GetInt32());

        var second = await InvokeAsync("games.update", new
        {
            idempotencyKey = $"t13fav-{Guid.NewGuid():N}",
            gameId,
            favorite = false,
            expectedRevision = 2,
        });
        Assert.True(second.Ok, second.Error?.Message);
        Assert.False(second.Data.GetProperty("favorite").GetBoolean());
    }

    [Fact]
    public async Task GamesUpdate_UnknownField_IsRejected()
    {
        var gameId = InsertGame(false);

        var envelope = await InvokeAsync("games.update", new
        {
            idempotencyKey = $"t13fav-{Guid.NewGuid():N}",
            gameId,
            favorite = true,
            expectedRevision = 1,
            hackField = "nope",
        });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, envelope.Error!.Code);
        Assert.Contains("hackField", envelope.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfilesSetDefault_ReplacesPreviousDefault()
    {
        var gameId = InsertGame(false);
        var first = CreateProfile(gameId);
        var second = CreateProfile(gameId);

        var set = await InvokeAsync("profiles.set_default", new
        {
            idempotencyKey = $"t13def-{Guid.NewGuid():N}",
            gameId,
            profileId = second,
        });

        Assert.True(set.Ok, set.Error?.Message);
        Assert.True(set.Data.GetProperty("isDefault").GetBoolean());

        var list = await InvokeAsync("profiles.list", new { gameId });
        var items = list.Data.GetProperty("items");
        var defaults = items.EnumerateArray()
            .Where(p => p.GetProperty("isDefault").GetBoolean())
            .Select(p => p.GetProperty("profileId").GetString())
            .ToList();
        Assert.Single(defaults, second);
    }

    [Fact]
    public async Task ProfilesRemove_Default_IsRejectedWithRecoveryOperation()
    {
        var gameId = InsertGame(false);
        var profileId = CreateProfile(gameId);
        var set = await InvokeAsync("profiles.set_default", new
        {
            idempotencyKey = $"t13def-{Guid.NewGuid():N}",
            gameId,
            profileId,
        });
        Assert.True(set.Ok);

        var remove = await InvokeAsync("profiles.remove", new
        {
            idempotencyKey = $"t13rm-{Guid.NewGuid():N}",
            profileId,
        });

        Assert.False(remove.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, remove.Error!.Code);
        Assert.Equal("profiles.set_default", remove.Error.RecoveryOperation);
    }

    [Fact]
    public async Task ProfilesRemove_NonDefault_Succeeds()
    {
        var gameId = InsertGame(false);
        var profileId = CreateProfile(gameId);

        var remove = await InvokeAsync("profiles.remove", new
        {
            idempotencyKey = $"t13rm-{Guid.NewGuid():N}",
            profileId,
        });

        Assert.True(remove.Ok, remove.Error?.Message);
        var get = await InvokeAsync("profiles.get", new { profileId });
        Assert.False(get.Ok);
    }

    [Fact]
    public async Task ProfilesValidate_MissingEntry_ReportsEntryMissing()
    {
        var gameId = InsertGame(false);
        var profile = _fixture.State.Launches.AddProfile(
            gameId,
            @"D:\Official\GameLibrary\artifacts\test-runs\definitely-missing-entry.exe",
            [],
            AppContext.BaseDirectory);

        var envelope = await InvokeAsync("profiles.validate", new { profileId = profile.ProfileId });

        Assert.True(envelope.Ok, envelope.Error?.Message);
        Assert.False(envelope.Data.GetProperty("available").GetBoolean());
        Assert.Contains(
            envelope.Data.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("code").GetString() == "EntryMissing");
    }

    [Fact]
    public async Task ProfilesCreate_AcceptsSwfInsideRegisteredGameLibrary()
    {
        var gameId = InsertGame(false);
        var gamesRoot = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs",
            _fixture.TestId,
            "games");
        var gameDirectory = Path.Combine(gamesRoot, gameId);
        Directory.CreateDirectory(gameDirectory);
        var swfPath = Path.Combine(gameDirectory, "game.swf");
        File.WriteAllText(swfPath, "fixture");

        var root = await InvokeAsync("roots.add", new
        {
            idempotencyKey = $"t13root-{Guid.NewGuid():N}",
            root = gamesRoot,
        });
        Assert.True(root.Ok, root.Error?.Message);

        var profile = await InvokeAsync("profiles.create", new
        {
            idempotencyKey = $"t13profile-{Guid.NewGuid():N}",
            gameId,
            executablePath = swfPath,
            cwd = gameDirectory,
            argv = Array.Empty<string>(),
            isDefault = true,
        });

        Assert.True(profile.Ok, profile.Error?.Message);
        Assert.Equal(swfPath, profile.Data.GetProperty("executablePath").GetString());
        Assert.True(profile.Data.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task LaunchPlan_RequiredGameNormalProfile_ReturnsNeedsUserActionWithPlan()
    {
        var gameId = InsertGame(toolNeedInherited: true);
        var profileId = CreateProfile(gameId);

        var plan = await InvokeAsync("launch.plan", new { gameId, profileId });

        Assert.False(plan.Ok);
        Assert.Equal(OperationStatus.NeedsUserAction, plan.Status);
        // 计划仍然返回供预览（无副作用）。
        Assert.Equal(profileId, plan.Data.GetProperty("profileId").GetString());
        Assert.Contains(
            plan.NextActions,
            a => a.OperationId == "translation.set");
    }

    [Fact]
    public async Task LaunchExecute_RequiredGameNormalProfile_IsRejectedWithoutAttempt()
    {
        var gameId = InsertGame(toolNeedInherited: true);
        var profileId = CreateProfile(gameId);

        var execute = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = $"t13launch-{Guid.NewGuid():N}",
            profileId,
        });

        Assert.False(execute.Ok);
        Assert.Equal(ErrorCodes.TranslationRouteUnavailable, execute.Error!.Code);
        Assert.Contains(execute.NextActions, a => a.OperationId == "translation.set");
        Assert.Empty(_fixture.State.Launches.History(gameId));
    }

    [Fact]
    public async Task LaunchExecute_AfterExplicitNotRequired_OriginalLaunchAllowed()
    {
        var gameId = InsertGame(toolNeedInherited: true);
        var profileId = CreateProfile(gameId);
        var set = await InvokeAsync("translation.set", new
        {
            idempotencyKey = $"t13-{Guid.NewGuid():N}",
            gameId,
            @override = "NotRequired",
            expectedRevision = 1,
        });
        Assert.True(set.Ok, set.Error?.Message);

        var execute = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = $"t13launch-{Guid.NewGuid():N}",
            profileId,
        });

        Assert.True(execute.Ok, execute.Error?.Message);
        Assert.Equal("processCreated", execute.Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task LaunchExecute_RequiredGameWithToolBoundProfile_IsAllowed()
    {
        var gameId = InsertGame(toolNeedInherited: true);
        var profileId = CreateProfile(gameId, toolId: "tool-mtool");

        var execute = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = $"t13launch-{Guid.NewGuid():N}",
            profileId,
        });

        Assert.True(execute.Ok, execute.Error?.Message);
    }

    [Fact]
    public async Task LaunchExecute_AutoRequiredGame_UsesRecipeWithOriginalGameProfile()
    {
        var root = Path.Combine(
            $@"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}",
            $"auto-mtool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var gameExe = Path.Combine(root, "Game.exe");
        var injector = Path.Combine(root, "inject.exe");
        var mtool = Path.Combine(root, "MTool.exe");
        var hook = Path.Combine(root, "SRPGHook.dll");
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var source = Path.ChangeExtension(StubExe, extension);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(root, Path.GetFileName(source)));
            }
        }
        CopyStub(Path.Combine(root, "Game"));
        CopyStub(Path.Combine(root, "inject"));
        CopyStub(Path.Combine(root, "MTool"));
        File.WriteAllText(hook, "stub");
        File.WriteAllText(Path.Combine(root, "与工具一同启动.bat"), $"""
            @echo off
            "{injector}" "{gameExe}" "{hook}"
            start "" "{mtool}" "{root}"
            """);

        var gameId = $"game-{Guid.NewGuid():N}";
        _fixture.State.Library.Store!.InsertGame(new GameCard
        {
            GameId = gameId,
            Title = "Auto MTool 测试游戏",
            RootPath = root,
            Kind = "GameRoot",
            Membership = "active",
            TranslationInherited = true,
            AcceptedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        var profile = _fixture.State.Launches.AddProfile(gameId, gameExe, [], root, isDefault: true);

        var resolution = TranslationLaunchRouteResolver.Resolve(
            _fixture.State.Library.Store.TryGetGame(gameId)!, profile);
        Assert.True(resolution.IsRequired);
        Assert.NotNull(resolution.Route);
        Assert.Equal("mtool", resolution.Route!.ToolId);
        Assert.Equal(2, resolution.Route.Steps.Count);
        Assert.Equal(gameExe, resolution.Route.Steps[0].Arguments[0]);

        var plan = await InvokeAsync("launch.plan", new { gameId, profileId = profile.ProfileId });
        Assert.True(plan.Ok, plan.Error?.Message);

        var execute = await InvokeAsync("launch.execute", new
        {
            idempotencyKey = $"auto-mtool-{Guid.NewGuid():N}",
            profileId = profile.ProfileId,
        });
        Assert.True(execute.Ok, execute.Error?.Message);
        Assert.Equal("processCreated", execute.Data.GetProperty("state").GetString());

        void CopyStub(string targetWithoutExtension)
        {
            foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var source = Path.ChangeExtension(StubExe, extension);
                if (File.Exists(source))
                {
                    File.Copy(source, targetWithoutExtension + extension);
                }
            }
        }
    }
}
