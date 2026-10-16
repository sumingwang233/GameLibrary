using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Domain.Detection;
using GameLibrary.Infrastructure.Tools;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.IntegrationTests.Tools;

public sealed class MToolPreparationTests(PipeServerFixture fixture) : IClassFixture<PipeServerFixture>
{
    private static string Stub => Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.exe");

    private async Task<Envelope<JsonElement>> Invoke(string operation, object parameters)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters, ContractJson.Options))!;
        if (operation is "mtool.settings.set" or "mtool.prepare" or "tags.assign" or "roots.add" or "launch.execute")
            values["idempotencyKey"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N"));
        await using var client = await HostConnection.ConnectAsync(fixture.State.DataDirectory, "mtool-prepare-test", CancellationToken.None);
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = Guid.NewGuid().ToString("N"), OperationId = operation,
            Parameters = JsonSerializer.SerializeToElement(values, ContractJson.Options),
        }, CancellationToken.None);
    }

    private async Task<(GameCard Game, string ProfileId, string Tool)> Create(bool rpg = true, string? engine = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "GameLibrary-mtool-" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(root, "game with spaces");
        var tool = Path.Combine(root, "tool with spaces");
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(Path.Combine(tool, "loaders"));
        File.Copy(Stub, Path.Combine(gameRoot, "Game.exe"));
        foreach (var file in new[] { "MTool.exe", "loaders/inject.exe", "loaders/mzHook.dll", "loaders/mzHook32.dll", "loaders/SRPGHook.dll" })
            File.Copy(Stub, Path.Combine(tool, file));
        foreach (var directory in new[] { tool, Path.Combine(tool, "loaders") })
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "GameLibrary.TestProcessStub.*").Where(file => !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        if (rpg)
        {
            File.Copy(Stub, Path.Combine(gameRoot, "nw.dll"));
            Directory.CreateDirectory(Path.Combine(gameRoot, "www/js"));
            Directory.CreateDirectory(Path.Combine(gameRoot, "www/data"));
            File.WriteAllText(Path.Combine(gameRoot, "www/js/rmmz_core.js"), "RPG Maker MZ");
            File.WriteAllText(Path.Combine(gameRoot, "www/data/System.json"), "{}");
        }
        Assert.True((await Invoke("roots.add", new { root })).Ok);
        Assert.True((await Invoke("mtool.settings.set", new { toolRoot = tool })).Ok);
        var game = new GameCard
        {
            GameId = "game-" + Guid.NewGuid().ToString("N"), Title = "Synthetic MTool game", RootPath = gameRoot,
            Kind = "gameRoot", Membership = "active", Engine = engine ?? (rpg ? JsonSerializer.SerializeToElement(EngineId.RpgMakerMvMz, ContractJson.Options).GetString() : null),
            EntryPath = Path.Combine(gameRoot, "Game.exe"), TranslationInherited = false,
            AcceptedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        fixture.State.Library.Store!.InsertGame(game);
        var profile = fixture.State.Launches.AddProfile(game.GameId, game.EntryPath!, [], game.RootPath, isDefault: true);
        return (game, profile.ProfileId, tool);
    }

    private async Task Tag(GameCard game)
    {
        var tag = fixture.State.Library.Store!.TryGetTagByName("user", "未翻译");
        if (tag is null)
        {
            tag = new("tag-" + Guid.NewGuid().ToString("N"), "user", "未翻译", null, 1, 0, DateTime.UtcNow, DateTime.UtcNow);
            fixture.State.Library.Store.CreateTag(tag);
        }
        var assigned = await Invoke("tags.assign", new { gameId = game.GameId, tagId = tag.TagId, expectedRevision = fixture.State.Library.Store.TryGetGame(game.GameId)!.Revision });
        Assert.True(assigned.Ok, assigned.Error?.Message);
    }

    [Fact]
    public async Task AssignTag_GeneratesRecipe_WithoutLaunchingOrRemovingTag_AndPlanUsesIt()
    {
        var (game, profileId, _) = await Create();
        var attempts = fixture.State.Launches.History().Count;
        await Tag(game);
        var discovery = new MToolAdapter().Discover(game.RootPath);
        Assert.NotNull(discovery.Recipe);
        Assert.False(discovery.Recipe.IsBroken);
        Assert.Equal(game.EntryPath, discovery.Recipe.Steps[0].Arguments[0]);
        Assert.Equal("mzHook.dll", Path.GetFileName(discovery.Recipe.Steps[0].Arguments[1]));
        Assert.Equal(attempts, fixture.State.Launches.History().Count);
        Assert.Contains(fixture.State.Library.Store!.ListGameTags(game.GameId), tag => tag.Name == "未翻译");
        var policy = await Invoke("translation.get", new { gameId = game.GameId });
        Assert.Equal("Required", policy.Data.GetProperty("effective").GetString());
        Assert.True((await Invoke("launch.plan", new { gameId = game.GameId, profileId })).Ok);
        Assert.Equal("ready", (await Invoke("mtool.status", new { gameId = game.GameId })).Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReadonlyPlan_DoesNotGenerate_ExplicitPreparationKeepsSelectedProfile_AndIsIdempotent()
    {
        var (game, profileId, _) = await Create();
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var plan = await Invoke("launch.plan", new { gameId = game.GameId, profileId });
        Assert.False(plan.Ok);
        Assert.Contains(plan.NextActions!, action => action.OperationId == "mtool.prepare");
        var script = Path.Combine(game.RootPath, MToolAdapter.RecipeFileName);
        Assert.False(File.Exists(script));
        var prepared = await Invoke("mtool.prepare", new { gameId = game.GameId, profileId = (string?)null });
        Assert.True(prepared.Ok, prepared.Error?.Message);
        var original = File.ReadAllBytes(script);
        Assert.True((await Invoke("mtool.prepare", new { gameId = game.GameId, profileId })).Ok);
        Assert.Equal(original, File.ReadAllBytes(script));
        Assert.Empty(Directory.GetFiles(game.RootPath, "*.bak"));
        Assert.True((await Invoke("launch.plan", new { gameId = game.GameId, profileId })).Ok);
    }

    [Fact]
    public async Task ExecuteWithoutRecipe_PreparesThroughHostFallback_AndRunsOnlySyntheticProcesses()
    {
        var (game, profileId, _) = await Create();
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        Assert.False(File.Exists(Path.Combine(game.RootPath, MToolAdapter.RecipeFileName)));
        var result = await Invoke("launch.execute", new { profileId });
        Assert.True(result.Ok, result.Error?.Message);
        await fixture.State.Jobs.WaitForIdleAsync();
        var attempt = Assert.Single(fixture.State.Launches.History(game.GameId));
        Assert.Contains(attempt.State, new[] { "processCreated", "exited" });
        Assert.True(File.Exists(Path.Combine(game.RootPath, MToolAdapter.RecipeFileName)));
    }

    [Fact]
    public async Task X86Evidence_SelectsX86Hook_AndMissingHookDoesNotWriteRecipe()
    {
        var (game, profileId, tool) = await Create();
        foreach (var path in new[] { game.EntryPath!, Path.Combine(game.RootPath, "nw.dll"), Path.Combine(tool, "loaders", "mzHook32.dll") })
        {
            // 仅供离线 PE 头识别，绝不执行这些合成 x86 文件。
            var bytes = File.ReadAllBytes(path);
            var coff = BitConverter.ToInt32(bytes, 0x3c) + 4;
            bytes[coff] = 0x4c; bytes[coff + 1] = 0x01;
            File.WriteAllBytes(path, bytes);
        }
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var script = Path.Combine(game.RootPath, MToolAdapter.RecipeFileName);
        var hook = Path.Combine(tool, "loaders", "mzHook32.dll");
        var originalHook = File.ReadAllBytes(hook);
        File.Delete(hook);
        Assert.False((await Invoke("mtool.prepare", new { gameId = game.GameId, profileId })).Ok);
        Assert.False(File.Exists(script));
        File.WriteAllBytes(hook, originalHook);
        Assert.True((await Invoke("mtool.prepare", new { gameId = game.GameId, profileId })).Ok);
        Assert.Equal(hook, new MToolAdapter().Discover(game.RootPath).Recipe!.Steps[0].Arguments[1]);
    }

    [Fact]
    public async Task OutsideGameEntry_AndOversizeScript_AreRejectedWithoutOverwriting()
    {
        var (game, _, tool) = await Create();
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var profile = fixture.State.Launches.AddProfile(game.GameId, Path.Combine(tool, "MTool.exe"), [], tool);
        var outside = await Invoke("mtool.prepare", new { gameId = game.GameId, profileId = profile.ProfileId });
        Assert.False(outside.Ok);
        Assert.Contains("游戏目录", outside.Error!.Message);
        var script = Path.Combine(game.RootPath, MToolAdapter.RecipeFileName);
        Assert.False(File.Exists(script));
        var original = new string('x', 64 * 1024 + 1);
        File.WriteAllText(script, original);
        Assert.NotNull(new MToolAdapter().Discover(game.RootPath).UnsupportedReason);
        Assert.Throws<InvalidDataException>(() => MToolRecipeGenerator.Prepare(game.RootPath, game.EntryPath!, tool));
        Assert.Equal(original, File.ReadAllText(script));
    }

    [Fact]
    public async Task StalePlan_IsRejectedBeforeRecipeGeneration()
    {
        var (game, profileId, _) = await Create();
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var plan = fixture.State.Launches.CreatePlan(game.GameId, profileId);
        var profile = fixture.State.Launches.GetProfile(profileId)!;
        fixture.State.Launches.RestoreProfile(profile with { Revision = profile.Revision + 1 });
        var result = await Invoke("launch.execute", new { planId = plan.PlanId, profileId });
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.PlanStale, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(game.RootPath, MToolAdapter.RecipeFileName)));
        Assert.Empty(fixture.State.Launches.History(game.GameId));
    }

    [Fact]
    public async Task PlanTakesPrecedenceOverOtherProfile_PreparationAndExecutionUseSameGame()
    {
        var (game, profileId, _) = await Create();
        var (other, otherProfile, _) = await Create();
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        fixture.State.Library.Store.SetTranslationOverride(other.GameId, "Required", 1, DateTime.UtcNow);
        var plan = fixture.State.Launches.CreatePlan(game.GameId, profileId);
        var result = await Invoke("launch.execute", new { planId = plan.PlanId, profileId = otherProfile });
        Assert.True(result.Ok, result.Error?.Message);
        await fixture.State.Jobs.WaitForIdleAsync();
        Assert.True(File.Exists(Path.Combine(game.RootPath, MToolAdapter.RecipeFileName)));
        Assert.False(File.Exists(Path.Combine(other.RootPath, MToolAdapter.RecipeFileName)));
        Assert.Equal(profileId, Assert.Single(fixture.State.Launches.History(game.GameId)).ProfileId);
        Assert.Empty(fixture.State.Launches.History(other.GameId));
    }

    [Fact]
    public async Task BrokenToolPaths_AreRepairedWithOriginalBackup_WithoutRewritingGameEntry()
    {
        var (game, profileId, tool) = await Create();
        var old = Path.Combine(game.RootPath, "retired tool");
        var script = Path.Combine(game.RootPath, MToolAdapter.RecipeFileName);
        var original = $"@chcp 65001\r\n@cd /d \"%~dp0\"\r\n\"{old}/loaders/inject.exe\" \"{game.EntryPath}\" \"{old}/loaders/mzHook.dll\"\r\nstart \"\" \"{old}/MTool.exe\" \"{old}\"\r\n";
        File.WriteAllText(script, original);
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var result = await Invoke("mtool.prepare", new { gameId = game.GameId, profileId });
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(original, File.ReadAllText(result.Data.GetProperty("backupPath").GetString()!));
        var recipe = new MToolAdapter().Discover(game.RootPath).Recipe!;
        Assert.False(recipe.IsBroken);
        Assert.Equal(Path.Combine(tool, "loaders", "inject.exe"), recipe.Steps[0].ExecutablePath);
        Assert.Equal(game.EntryPath, recipe.Steps[0].Arguments[0]);
        Assert.Equal(tool, recipe.Steps[1].Arguments[0]);
    }

    [Theory]
    [InlineData("missing-cache")]
    [InlineData("unsupported-script")]
    [InlineData("unknown-engine")]
    public async Task UnsafeOrUnsupportedInputs_PreserveOriginal_AndRemainBlocked(string kind)
    {
        var (game, profileId, tool) = await Create(rpg: false);
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, "Required", 1, DateTime.UtcNow);
        var script = Path.Combine(game.RootPath, MToolAdapter.RecipeFileName);
        string? original = null;
        if (kind == "missing-cache")
        {
            var missing = Path.Combine(tool, "MTool_Game_Tmp", "sample", "MTool_Game.exe");
            File.Copy(Stub, Path.Combine(tool, "MTool_Game.exe")); // 同名诱饵不能替代专属缓存。
            original = $"@cd /d \"%~dp0\"\n\"{tool}/loaders/inject.exe\" \"{missing}\" \"{tool}/loaders/mzHook.dll\"\nstart \"\" \"{tool}/MTool.exe\" \"{tool}\"";
        }
        else if (kind == "unsupported-script") original = "call custom.bat";
        if (original is not null) File.WriteAllText(script, original);
        var result = await Invoke("mtool.prepare", new { gameId = game.GameId, profileId });
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.TranslationRouteUnavailable, result.Error!.Code);
        Assert.Equal(original, File.Exists(script) ? File.ReadAllText(script) : null);
        Assert.Empty(Directory.GetFiles(game.RootPath, "*.bak"));
        Assert.False((await Invoke("launch.plan", new { gameId = game.GameId, profileId })).Ok);
    }

    [Theory]
    [InlineData("unity", false)]
    [InlineData("rpgMakerMvMz", true)]
    public async Task UnityAndExplicitNotRequired_AreNotConfigured(string engine, bool notRequired)
    {
        var (game, profileId, _) = await Create(engine: engine);
        fixture.State.Library.Store!.SetTranslationOverride(game.GameId, notRequired ? "NotRequired" : "Required", 1, DateTime.UtcNow);
        if (notRequired) await Tag(game);
        var result = await Invoke("mtool.prepare", new { gameId = game.GameId, profileId });
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("skipped", result.Data.GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(game.RootPath, MToolAdapter.RecipeFileName)));
    }
}
