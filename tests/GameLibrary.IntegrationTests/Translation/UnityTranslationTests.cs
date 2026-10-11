using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Catalog;
using GameLibrary.Host;
using GameLibrary.Host.Hosting;
using GameLibrary.Host.Launching;
using GameLibrary.Host.Observability;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using Xunit;

namespace GameLibrary.IntegrationTests.Translation;

public sealed class UnityTranslationTests
{
    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public async Task Unity6Interop_ChangesOnlyVerifiedXrefCall_IsIdempotent_RejectsUnknownBytes(string architecture)
    {
        var files = await new UnityTranslationPayload().Il2CppAsync(architecture, true, CancellationToken.None);
        var original = files[UnityTranslationAssembly.InteropPath];
        Assert.Equal(UnityTranslationAssembly.OriginalInteropHash, Convert.ToHexString(SHA256.HashData(original)));
        var patched = UnityTranslationAssembly.PatchInterop(original);
        Assert.Equal(UnityTranslationAssembly.PatchedInteropHash, Convert.ToHexString(SHA256.HashData(patched)));
        Assert.Equal(1, original.Zip(patched).Count(pair => pair.First != pair.Second));
        Assert.Equal(patched, UnityTranslationAssembly.PatchInterop(patched));
        var unknown = original.ToArray(); unknown[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => UnityTranslationAssembly.PatchInterop(unknown));
    }

    [Fact]
    public async Task Unity6Startup_RepairsOwnedConfirmedInteropWithoutTag_AndKeepsOriginalRestoreBoundary()
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var game = fixture.AddGame("unity6-owned");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
        File.Copy(native, exe);
        File.Copy(native, Path.Combine(game.RootPath, "GameAssembly.dll"));
        var data = Path.Combine(game.RootPath, "Game_Data");
        Directory.CreateDirectory(Path.Combine(data, "il2cpp_data"));
        File.WriteAllText(Path.Combine(data, "globalgamemanagers"), "\0\0\0\u00146000.0.59f2\0");
        var profile = fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var payload = new UnityTranslationPayload();
        var id = Guid.NewGuid().ToString("N");
        var transaction = new UnityTranslationTransaction(new UnityTranslationVault(Path.GetFullPath(fixture.Host.DataDirectory).ToUpperInvariant(), fixture.VaultBase), id, game.RootPath);
        foreach (var pair in await payload.Il2CppAsync("x64", true, CancellationToken.None))
            transaction.Write(Path.Combine(game.RootPath, pair.Key), pair.Value);
        var layout = UnityTranslationInspection.Inspect(exe);
        transaction.Write(Path.Combine(layout.Translators, "DeepSeekTranslate.dll"), await payload.EndpointAsync(CancellationToken.None));
        var config = new UnityTranslationIni("[Behaviour]\nOverrideFont=Custom CJK\nFallbackFontTextMeshPro=custom.bundle\n");
        transaction.Write(layout.Config, config.Configure(new(), fixture.Key));
        transaction.Commit();
        Assert.False(UnityTranslationFonts.NeedsRepair(layout, UnityTranslationIni.Read(layout.Config)));
        fixture.Store.UnassignTag(game.GameId, fixture.TagId);
        fixture.WriteState(new() { GameId = game.GameId, Title = game.Title, AttemptId = "old-confirmed", DataEpoch = fixture.Store.Info.DataEpoch,
            State = "confirmed", BackupId = id, InstallRoot = game.RootPath, BoundTagId = fixture.TagId, ProfileId = profile.ProfileId, ExecutablePath = exe });
        fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        var repaired = fixture.ReadState(game.GameId)!;
        Assert.True(repaired.State == "configured", repaired.Reason);
        Assert.Equal(id, repaired.BackupId);
        Assert.Equal(UnityTranslationAssembly.PatchedInteropHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game.RootPath, UnityTranslationAssembly.InteropPath)))));
        fixture.RestartService(); fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal(repaired.AttemptId, fixture.ReadState(game.GameId)?.AttemptId);
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.False(File.Exists(Path.Combine(game.RootPath, UnityTranslationAssembly.InteropPath)));
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteDualMonoLoaders_DisablesOnlyReiCall_RestoresAndRetries_OrRollsBack(bool invalidEndpoint)
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("dual-mono");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data, invalidEndpoint: invalidEndpoint, dualLoader: true);
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var layout = UnityTranslationInspection.Inspect(exe);
        Assert.Null(layout.Reason);
        Assert.Equal("bepinex", layout.Loader);
        Assert.True(layout.ReiConflict);
        Assert.False(UnityTranslationInspection.IsConfiguredChineseTranslator(layout));
        var original = File.ReadAllBytes(layout.Bootstrap);
        var mod = Path.Combine(game.RootPath, "BepInEx", "plugins", "other-mod.dat");
        File.WriteAllText(mod, "preserve existing mod");
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        var state = fixture.ReadState(game.GameId)!;
        if (invalidEndpoint)
        {
            Assert.Equal("failed", state.State);
            Assert.Equal(original, File.ReadAllBytes(layout.Bootstrap));
            Assert.True(UnityTranslationInspection.Inspect(exe).ReiConflict);
            Assert.False(File.Exists(layout.Config));
            return;
        }
        Assert.True(state.State == "configured", state.Reason);
        Assert.False(UnityTranslationInspection.HasBootstrap(layout.Bootstrap));
        Assert.False(UnityTranslationInspection.Inspect(exe).ReiConflict);
        Assert.Equal(original, File.ReadAllBytes(layout.Bootstrap + ".untrusted.bak"));
        Assert.Equal("preserve existing mod", File.ReadAllText(mod));
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.Equal(original, File.ReadAllBytes(layout.Bootstrap));
        fixture.RestartService();
        Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("configured", fixture.ReadState(game.GameId)?.State);
        Assert.False(UnityTranslationInspection.HasBootstrap(layout.Bootstrap));
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData("5.0.0", "2023.2.3f1", false)]
    [InlineData("5.4.5", "6000.1.6f1", false)]
    [InlineData("5.0.0", "2023.2.3f1", true)]
    public async Task ModernTmp_UsesNativeChineseFont_UpgradesOnlyPinnedReiFiles_WithRestore(string version, string unity, bool unknownCore)
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("modern-tmp");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data, splitDependency: true, systemTmp: true);
        File.WriteAllText(Path.Combine(data, "globalgamemanagers"), "\0\0\0\u0014" + unity + "\0");
        var layout = UnityTranslationInspection.Inspect(exe);
        var payload = new UnityTranslationPayload();
        var old = await payload.RuntimeAsync(CancellationToken.None, version);
        foreach (var pair in old) File.WriteAllBytes(Path.Combine(layout.Managed, pair.Key), pair.Value);
        File.WriteAllBytes(layout.Bootstrap, await payload.PatchAsync(layout, old, CancellationToken.None));
        var endpoint = Path.Combine(layout.Translators, "DeepSeekTranslate.dll");
        File.WriteAllBytes(endpoint, await payload.EndpointAsync(CancellationToken.None));
        var bundle = Path.Combine(game.RootPath, "legacy-font");
        File.WriteAllText(bundle, "UnityFS\0\0\0\0\u00065.x.x\u00002019.1.0f2\0");
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Config)!);
        var originalConfig = Encoding.UTF8.GetBytes(version == "5.4.5"
            ? "[Behaviour]\nOverrideFont=legacy-font\nOverrideFontTextMeshPro=legacy-font\n[OtherMod]\nKeep=yes\n"
            : "[Behaviour]\nOverrideFont=Microsoft YaHei\nFallbackFontTextMeshPro=GameLibraryFonts/xiaolai-2023.bundle\n[OtherMod]\nKeep=yes\n");
        File.WriteAllBytes(layout.Config, originalConfig);
        if (unknownCore)
        {
            var changed = File.ReadAllBytes(layout.Core); changed[^1] ^= 1; File.WriteAllBytes(layout.Core, changed);
        }
        var originalCore = File.ReadAllBytes(layout.Core);
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        var state = fixture.ReadState(game.GameId)!;
        if (unknownCore)
        {
            Assert.Equal("blocked", state.State);
            Assert.Equal(originalCore, File.ReadAllBytes(layout.Core));
            Assert.Equal(originalConfig, File.ReadAllBytes(layout.Config));
            return;
        }
        Assert.True(state.State == "configured", state.Reason);
        var ini = UnityTranslationIni.Read(layout.Config);
        Assert.Equal(UnityTranslationFonts.SystemFont, ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        Assert.Equal("", ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        Assert.Equal("yes", ini.Get("OtherMod", "Keep"));
        Assert.Equal(new Version(5, 6, 2, 0), UnityTranslationFonts.PluginVersion(layout.Core));
        Assert.False(UnityTranslationFonts.NeedsRepair(UnityTranslationInspection.Inspect(exe), ini));
        Assert.True(File.Exists(bundle));
        Assert.Empty(fixture.Host.Launches.History());
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.Equal(originalCore, File.ReadAllBytes(layout.Core));
        Assert.Equal(originalConfig, File.ReadAllBytes(layout.Config));
        fixture.RestartService();
        Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("configured", fixture.ReadState(game.GameId)?.State);
        var custom = UnityTranslationIni.Read(layout.Config);
        custom.Set("Behaviour", "OverrideFontTextMeshPro", "user-font.bundle");
        Assert.False(UnityTranslationFonts.NeedsRepair(UnityTranslationInspection.Inspect(exe), custom));
        Assert.False(UnityTranslationFonts.Configure(UnityTranslationInspection.Inspect(exe), custom, out _));
        Assert.Equal("user-font.bundle", custom.Get("Behaviour", "OverrideFontTextMeshPro"));
    }

    [Fact]
    public async Task MonoFonts_MigrateManagedFallbackOnly_PreserveCustomFonts_AndKeepIl2CppFallback()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("mono-fonts");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        var layout = UnityTranslationInspection.Inspect(exe);
        File.WriteAllText(Path.Combine(layout.Data, "globalgamemanagers"), "\0\0\0\u00142020.2.1f1\0");
        var bundle = UnityTranslationFonts.BundlePath("2020");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(layout.Root, bundle))!);
        // Font contents are irrelevant to this configuration migration; payload hash tests cover the download.
        File.WriteAllText(Path.Combine(layout.Root, bundle), "already installed");
        var ini = new UnityTranslationIni("[Behaviour]\nOverrideFont=Custom CJK\nFallbackFontTextMeshPro=" + bundle);
        Assert.True(UnityTranslationFonts.NeedsRepair(layout, ini));
        Assert.True(UnityTranslationFonts.Configure(layout, ini, out _));
        Assert.Equal(bundle, ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        Assert.Equal("", ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        Assert.False(UnityTranslationFonts.NeedsRepair(layout, ini));
        File.Delete(Path.Combine(layout.Root, bundle));
        Assert.True(UnityTranslationFonts.NeedsRepair(layout, ini));
        Assert.True(UnityTranslationFonts.Configure(layout, ini, out _));
        ini.Set("Behaviour", "OverrideFontTextMeshPro", "my-font.bundle");
        Assert.False(UnityTranslationFonts.NeedsRepair(layout, ini));
        Assert.False(UnityTranslationFonts.Configure(layout, ini, out _));
        Assert.Equal("my-font.bundle", ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        ini = new UnityTranslationIni("[Behaviour]\nOverrideFont=Custom CJK");
        Assert.True(UnityTranslationFonts.Configure(layout with { Runtime = "il2cpp" }, ini, out _));
        Assert.Null(ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        Assert.Equal(bundle, ini.Get("Behaviour", "FallbackFontTextMeshPro"));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("configure")]
    public async Task ExistingChineseMonoPlugin_MissingFontIsRepairedAndBackedUp_InsteadOfShortcutConfirmation(string trigger)
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var engine = JsonSerializer.SerializeToElement(GameLibrary.Domain.Detection.EngineId.Unity, ContractJson.Options).GetString()!;
        var game = fixture.AddGame("existing-chinese-font", engine);
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var layout = UnityTranslationInspection.Inspect(exe);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Config)!);
        File.WriteAllBytes(layout.Config, new UnityTranslationIni("[OtherMod]\nValue=keep").Configure(new(), fixture.Key));
        var core = File.ReadAllBytes(layout.Core);
        if (trigger == "startup") fixture.Service.Start();
        else
        {
            fixture.Store.UnassignTag(game.GameId, fixture.TagId);
            Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
        }
        await fixture.Host.Jobs.WaitForIdleAsync();
        var repaired = fixture.ReadState(game.GameId)!;
        Assert.True(repaired.State == "configured", repaired.Reason);
        Assert.NotNull(repaired.BackupId);
        var ini = UnityTranslationIni.Read(layout.Config);
        Assert.Equal(UnityTranslationFonts.SystemFont, ini.Get("Behaviour", "OverrideFont"));
        Assert.Equal("keep", ini.Get("OtherMod", "Value"));
        Assert.Equal(core, File.ReadAllBytes(layout.Core));
        if (trigger == "startup") Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData("required")]
    [InlineData("accept")]
    [InlineData("accept-batch")]
    [InlineData("explicit")]
    [InlineData("discovery")]
    [InlineData("wizard")]
    [InlineData("ambiguous")]
    [InlineData("discarded")]
    [InlineData("not-required")]
    public async Task AutomaticEntry_ConfiguresWithoutDefaultOrTag_AndPreservesSelectionBoundaries(string trigger)
    {
        await using var fixture = await Fixture.Create();
        if (trigger != "wizard") Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var engine = JsonSerializer.SerializeToElement(GameLibrary.Domain.Detection.EngineId.Unity, ContractJson.Options).GetString()!;
        var game = fixture.AddGame("suggested-entry", engine);
        fixture.Store.UnassignTag(game.GameId, fixture.TagId);
        if (trigger is "required" or "accept" or "accept-batch" or "not-required") fixture.Store.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE games SET translation_inherited=1, translation_override=$override WHERE game_id=$id";
            command.Parameters.AddWithValue("$override", trigger == "not-required" ? "NotRequired" : DBNull.Value);
            command.Parameters.AddWithValue("$id", game.GameId);
            command.ExecuteNonQuery();
        });
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        // Real filesystem discovery and scoring, without inserting a default or executing the EXE.
        var suggestions = new LaunchSuggestionService(fixture.Host);
        if (trigger is not ("discovery" or "accept" or "accept-batch")) await suggestions.DiscoverAsync(game.GameId, CancellationToken.None);
        var profile = fixture.Host.Launches.ListProfiles(game.GameId).SingleOrDefault();
        if (trigger is not ("discovery" or "accept" or "accept-batch"))
        {
            Assert.NotNull(profile);
            Assert.Equal("automatic", profile.Source);
            Assert.Equal("suggested", profile.ValidationStatus);
            Assert.False(profile.IsDefault);
        }
        if (trigger == "ambiguous") fixture.Host.Launches.AddSuggestions(game.GameId, game.RootPath,
            [new GameLibrary.Domain.Detection.EntryCandidate("Other.exe", profile!.SuggestionScore, [])]);
        if (trigger == "discarded") fixture.Host.Launches.RestoreProfile(profile! with { ValidationStatus = "discarded" });
        if (trigger is "accept" or "accept-batch")
        {
            fixture.Store.UpsertCandidate(new() { CandidateId = "new-accept", PhysicalPath = game.RootPath, RelativePath = "suggested-entry", Kind = "game",
                PayloadJson = JsonSerializer.Serialize(new { engines = new[] { new { engine = GameLibrary.Domain.Detection.EngineId.Unity } },
                    entryCandidates = new[] { new { relativePath = "Game.exe" } }, classification = new { requiredByToolNeed = true } }, ContractJson.Options),
                ReviewState = "pendingReview", ObservedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            var result = new GameLibrary.Host.Hosting.OperationDispatcher(fixture.Host).Dispatch(new IpcRequest
            { RequestId = Guid.NewGuid().ToString("N"), OperationId = trigger == "accept" ? "candidates.accept" : "candidates.review_batch",
                Parameters = trigger == "accept"
                    ? JsonSerializer.SerializeToElement(new { candidateId = "new-accept", expectedRevision = 1, idempotencyKey = Guid.NewGuid().ToString("N") }, ContractJson.Options)
                    : JsonSerializer.SerializeToElement(new { action = "accept", items = new[] { new { candidateId = "new-accept", expectedRevision = 1 } },
                        idempotencyKey = Guid.NewGuid().ToString("N") }, ContractJson.Options) });
            Assert.True(result.Ok, result.Error?.Message);
        }
        else if (trigger is "required" or "not-required") fixture.Service.RequestForTaggedGame(game.GameId);
        else Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        if (trigger == "wizard")
        {
            Assert.Equal("needs_settings", fixture.ReadState(game.GameId)?.State);
            Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
            await fixture.Host.Jobs.WaitForIdleAsync();
        }
        if (trigger is "ambiguous" or "discarded" or "not-required")
        {
            if (trigger != "not-required") Assert.Equal("blocked", fixture.ReadState(game.GameId)?.State);
            else Assert.Null(fixture.ReadState(game.GameId));
            Assert.False(File.Exists(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini")));
        }
        else
        {
            var state = fixture.ReadState(game.GameId)!;
            Assert.True(state.State == "configured", state.Reason);
            Assert.Equal((profile ?? Assert.Single(fixture.Host.Launches.ListProfiles(game.GameId))).ProfileId, state.ProfileId);
            Assert.Equal("zh", UnityTranslationIni.Read(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini")).Get("General", "Language"));
        }
        Assert.Null(fixture.Host.Launches.GetDefaultProfile(game.GameId));
        Assert.Empty(fixture.Host.Launches.History());
        suggestions.Dispose();
    }

    [Fact]
    public async Task FontRepair_UsesBoundedUnityVersion_PreservesCustomFontsAndPreviousInstallationOnFailure()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("font-repair");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        var layout = UnityTranslationInspection.Inspect(exe);
        File.WriteAllBytes(Path.Combine(layout.Data, "globalgamemanagers"), Encoding.ASCII.GetBytes("\0\0\0\u00142020.2.1f1\0"));
        Assert.Equal("2020", UnityTranslationFonts.Generation(layout));
        var ini = new UnityTranslationIni("[Behaviour]\nOverrideFont=Custom CJK\nFallbackFontTextMeshPro=custom.bundle\n[OtherMod]\nValue=keep\n");
        Assert.False(UnityTranslationFonts.Configure(layout, ini, out _));
        Assert.Equal("Custom CJK", ini.Get("Behaviour", "OverrideFont"));
        Assert.Equal("custom.bundle", ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        ini = new UnityTranslationIni("");
        Assert.True(UnityTranslationFonts.NeedsRepair(layout, ini));
        Assert.True(UnityTranslationFonts.Configure(layout, ini, out var generation));
        Assert.Equal("2020", generation);
        Assert.Equal("GameLibraryFonts/xiaolai-2020.bundle", ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        Assert.Equal("", ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        var vault = new UnityTranslationVault("font-test", fixture.VaultBase);
        var id = Guid.NewGuid().ToString("N");
        var original = Encoding.UTF8.GetBytes("original configuration");
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Config)!);
        File.WriteAllBytes(layout.Config, original);
        var transaction = new UnityTranslationTransaction(vault, id, layout.Root);
        transaction.Write(layout.Config, ini.Configure(new(), fixture.Key));
        var plugin = Path.Combine(layout.Root, "owned-plugin.dll");
        transaction.Write(plugin, [1, 2, 3]);
        transaction.Commit();
        var runtimeIni = File.ReadAllText(layout.Config) + "\n[OtherMod]\nValue=plugin-generated\n";
        File.WriteAllText(layout.Config, runtimeIni);
        transaction = new(vault, id, layout.Root);
        var merged = UnityTranslationIni.Read(layout.Config);
        merged.Set("Behaviour", "FallbackFontTextMeshPro", "updated.bundle");
        transaction.WriteConfiguration(layout.Config, merged.Configure(new(), fixture.Key), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(layout.Config))));
        var font = Path.Combine(layout.Root, "GameLibraryFonts", "new.bundle");
        transaction.Write(font, [4, 5, 6]);
        transaction.RollbackAttempt();
        Assert.Equal(runtimeIni, File.ReadAllText(layout.Config));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(plugin));
        Assert.False(File.Exists(font));
        transaction = new(vault, id, layout.Root);
        transaction.WriteConfiguration(layout.Config, merged.Configure(new(), fixture.Key), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(layout.Config))));
        transaction.Commit();
        transaction.Restore();
        Assert.Equal(original, File.ReadAllBytes(layout.Config));
        Assert.False(File.Exists(plugin));
        Assert.Contains("SIL OPEN FONT LICENSE", Encoding.UTF8.GetString(UnityTranslationPayload.FontNotice()));
    }

    [Theory]
    [InlineData("xiaolai 2020", "UnityFS\0\0\0\02020.3.0f1\0", true)]
    [InlineData("../xiaolai 2020", "UnityFS\0\0\0\02020.3.0f1\0", false)]
    [InlineData("xiaolai 2020", "UnityFS\0\0\0\02019.4.0f1\0", false)]
    public void FontArchives_ValidatePathAndUnityGeneration(string path, string contents, bool valid)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var output = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false))) output.Write(contents);
        if (valid) Assert.StartsWith("UnityFS\0", Encoding.ASCII.GetString(UnityTranslationPayload.ExtractFont(buffer.ToArray(), "2020")));
        else Assert.Throws<InvalidDataException>(() => UnityTranslationPayload.ExtractFont(buffer.ToArray(), "2020"));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("tag")]
    [InlineData("configure")]
    public async Task ExistingEnabledChinesePlugin_RemovesOnlyBoundTag_WithoutRunningOrChangingFiles(string trigger)
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("already-translated");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        fixture.Store.CreateTag(new("other-tag", "user", "keep", null, 1, 0, DateTime.UtcNow, DateTime.UtcNow));
        fixture.Store.AssignTag(game.GameId, "other-tag", DateTime.UtcNow);
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var original = "[Behaviour]\nOverrideFont=Custom CJK\nOverrideFontTextMeshPro=custom.bundle\n[General]\nLanguage=zh\n[Service]\nEndpoint=DeepSeekTranslate\n[DeepSeek]\nApiKey=" + fixture.Key;
        File.WriteAllText(config, original);
        if (trigger == "startup") fixture.Service.Start();
        else if (trigger == "tag") fixture.Service.RequestForTaggedGame(game.GameId);
        else { Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok); await fixture.Host.Jobs.WaitForIdleAsync(); }
        Assert.Equal("confirmed", fixture.ReadState(game.GameId)?.State);
        Assert.DoesNotContain(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        Assert.Contains(("user", "keep"), fixture.Store.ListGameTags(game.GameId));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Fact]
    public async Task Startup_RepairsOwnedChineseConfigMissingFonts_WithoutTag_AndPreservesRuntimeDefaults()
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var game = fixture.AddGame("owned-chinese-fonts");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        File.WriteAllText(Path.Combine(game.RootPath, "Game_Data", "globalgamemanagers"), "\0\0\0\u00142020.2.1f1\0");
        var profile = fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var layout = UnityTranslationInspection.Inspect(exe);
        var backupId = Guid.NewGuid().ToString("N");
        var vault = new UnityTranslationVault(Path.GetFullPath(fixture.Host.DataDirectory).ToUpperInvariant(), fixture.VaultBase);
        var transaction = new UnityTranslationTransaction(vault, backupId, layout.Root);
        transaction.Write(layout.Config, new UnityTranslationIni("").Configure(new(), fixture.Key));
        transaction.Commit();
        File.AppendAllText(layout.Config, "\n[OtherMod]\nRuntimeDefault=keep\n");
        fixture.Store.UnassignTag(game.GameId, fixture.TagId);
        fixture.WriteState(new() { GameId = game.GameId, Title = game.Title, AttemptId = "previous", DataEpoch = fixture.Store.Info.DataEpoch,
            State = "confirmed", BackupId = backupId, InstallRoot = game.RootPath, BoundTagId = fixture.TagId,
            ProfileId = profile.ProfileId, ExecutablePath = exe });
        fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        var repaired = fixture.ReadState(game.GameId)!;
        Assert.True(repaired.State == "configured", repaired.Reason);
        Assert.Equal(backupId, repaired.BackupId);
        var ini = UnityTranslationIni.Read(layout.Config);
        Assert.Equal("keep", ini.Get("OtherMod", "RuntimeDefault"));
        Assert.Equal("GameLibraryFonts/xiaolai-2020.bundle", ini.Get("Behaviour", "OverrideFontTextMeshPro"));
        Assert.Equal("", ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        Assert.True(File.Exists(Path.Combine(layout.Root, ini.Get("Behaviour", "OverrideFontTextMeshPro")!)));
        fixture.RestartService();
        fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal(repaired.AttemptId, fixture.ReadState(game.GameId)?.AttemptId);
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData("language")]
    [InlineData("disabled")]
    [InlineData("endpoint")]
    [InlineData("key")]
    [InlineData("declined")]
    [InlineData("invalid-endpoint")]
    public async Task ExistingUnverifiedOrDeclinedPlugin_KeepsUntranslatedTag(string reason)
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("not-ready");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"), invalidEndpoint: reason == "invalid-endpoint");
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, $"[General]\nLanguage={(reason == "language" ? "en" : "zh")}\n[Service]\nEndpoint={(reason == "endpoint" ? "MissingEndpoint" : "DeepSeekTranslate")}\n[DeepSeek]\nApiKey={(reason == "key" ? "" : fixture.Key)}\n[Behaviour]\nEnableTranslation={(reason == "disabled" ? "False" : "True")}\n");
        if (reason == "declined") fixture.WriteState(new() { GameId = game.GameId, Title = game.Title, AttemptId = "declined", DataEpoch = fixture.Store.Info.DataEpoch, State = "declined" });
        fixture.Service.RequestForTaggedGame(game.GameId);
        Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        Assert.NotEqual("confirmed", fixture.ReadState(game.GameId)?.State);
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData("unity")]
    [InlineData("Unity")]
    [InlineData("UNITY")]
    public async Task AutomaticSetup_AcceptsUnityEngineCasing_OnStartupAndTagAssignment(string engine)
    {
        await using var fixture = await Fixture.Create();
        var existing = fixture.AddGame("existing", engine);
        fixture.Service.Start();
        Assert.Equal("needs_settings", fixture.ReadState(existing.GameId)?.State);

        var newlyTagged = fixture.AddGame("newly-tagged", engine);
        fixture.Service.RequestForTaggedGame(newlyTagged.GameId);
        Assert.Equal("needs_settings", fixture.ReadState(newlyTagged.GameId)?.State);
        Assert.Empty(fixture.Host.Launches.History());
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "deepseek", true)]
    [InlineData("http://127.0.0.1:8080/v1", "custom", true)]
    [InlineData("http://remote.example/v1", "custom", false)]
    [InlineData("https://user:password@example.com/v1", "custom", false)]
    [InlineData("https://example.com/v1?api_key=test", "custom", false)]
    [InlineData("https://example.com/v1", "deepseek", false)]
    public void ProviderValidation_IsCredentialFreeAndHttpsOrLoopback(string url, string provider, bool valid)
    {
        var settings = new UnityTranslationSettings { Provider = provider, BaseUrl = url };
        if (valid) Assert.EndsWith("/chat/completions", settings.Validate().BaseUrl);
        else Assert.Throws<InvalidDataException>(() => settings.Validate());
    }

    [Fact]
    public void Ini_PreservesUnknownValuesAndModel_DisablesProviderSpecificFlagsForCustom()
    {
        var ini = new UnityTranslationIni("[OtherMod]\nValue=keep\n[DeepSeek]\nModel=older-model\nDebug=True\n");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var official = new UnityTranslationIni(Encoding.UTF8.GetString(ini.Configure(new(), key)));
        Assert.Equal("older-model", official.Get("DeepSeek", "Model"));
        Assert.Equal("keep", official.Get("OtherMod", "Value"));
        Assert.Equal("False", official.Get("DeepSeek", "Debug"));
        var custom = new UnityTranslationIni(Encoding.UTF8.GetString(official.Configure(new UnityTranslationSettings
        { Provider = "custom", BaseUrl = "http://127.0.0.1/v1/chat/completions", Model = "local-model" }, key)));
        Assert.Equal("False", custom.Get("DeepSeek", "DisableThinking"));
        Assert.Equal("False", custom.Get("DeepSeek", "AddEndingAssistantPrompt"));
        Assert.Equal("local-model", custom.Get("DeepSeek", "Model"));
        Assert.Equal("", custom.Get("Service", "FallbackEndpoint"));
    }

    [Fact]
    public void ArchiveExtraction_RejectsTraversalAndExtraFiles()
    {
        foreach (var entry in new[] { "../SetupReiPatcherAndAutoTranslator.exe", "dir/SetupReiPatcherAndAutoTranslator.exe", "untrusted.dll" })
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry(entry);
            Assert.Throws<InvalidDataException>(() => UnityTranslationPayload.ExtractSetup(buffer.ToArray()));
        }
    }

    [Fact]
    public async Task Transaction_EncryptsOriginalIni_RestoresOnlyOwnedFiles_RejectsLaterMods()
    {
        await using var fixture = await Fixture.Create();
        var root = Path.Combine(fixture.DirectoryPath, "game");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var original = Encoding.UTF8.GetBytes("[DeepSeek]\nApiKey=" + fixture.Key);
        File.WriteAllBytes(config, original);
        var save = Path.Combine(root, "save.dat");
        File.WriteAllText(save, "player-save");
        var vault = new UnityTranslationVault("synthetic", fixture.VaultBase);
        var transaction = new UnityTranslationTransaction(vault, Guid.NewGuid().ToString("N"), root);
        transaction.Capture(config);
        transaction.Write(config, Encoding.UTF8.GetBytes("replacement"));
        var plugin = Path.Combine(root, "plugin.dll");
        transaction.Write(plugin, [1, 2, 3]);
        transaction.Commit();
        Assert.All(Directory.EnumerateFiles(vault.DirectoryPath, "*", SearchOption.AllDirectories), path =>
            Assert.DoesNotContain(fixture.Key, Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        File.WriteAllText(plugin, "another-mod");
        Assert.Throws<InvalidDataException>(() => transaction.Restore());
        Assert.Equal("replacement", File.ReadAllText(config)); // Preflight prevents a partial restore.
        File.WriteAllBytes(plugin, [1, 2, 3]);
        transaction.Restore();
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.False(File.Exists(plugin));
        Assert.Equal("player-save", File.ReadAllText(save));
    }

    [Theory]
    [InlineData("Config.ini")]
    [InlineData("AutoTranslatorConfig.ini")]
    public async Task Settings_ImportReturnsMetadataOnly_PreservesModel_RejectsCrossOriginKeyReuse(string fileName)
    {
        await using var fixture = await Fixture.Create();
        var config = Path.Combine(fixture.DirectoryPath, fileName);
        File.WriteAllText(config, "[DeepSeek]\nEndpoint=https://api.deepseek.com/chat/completions\nModel=legacy-model\nApiKey=" + fixture.Key);
        var imported = fixture.Invoke("settings.import", new { configPath = config });
        Assert.True(imported.Ok, imported.Error?.Message);
        var dto = Data(imported);
        Assert.Equal("legacy-model", dto.GetProperty("model").GetString());
        Assert.True(dto.GetProperty("hasKey").GetBoolean());
        Assert.DoesNotContain(fixture.Key, JsonSerializer.Serialize(imported, ContractJson.Options));
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());
        var change = fixture.Invoke("settings.set", new { provider = "openai", endpoint = "https://new-provider.example/v1", model = "next-model" });
        Assert.False(change.Ok);
        var model = fixture.Invoke("settings.set", new { provider = "deepseek", model = "new-model" });
        Assert.True(model.Ok, model.Error?.Message);
        Assert.Equal("new-model", Data(model).GetProperty("model").GetString());
        var leak = fixture.Invoke("settings.set", new { model = fixture.Key });
        Assert.False(leak.Ok);
        Assert.DoesNotContain(fixture.Key, JsonSerializer.Serialize(leak, ContractJson.Options));
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());
    }

    [Fact]
    public async Task CredentialReferences_IsolateFailedMetadataWrites_AndOldSettingsRestoreOldKey()
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var original = UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!;
        var vault = new UnityTranslationVault(Path.GetFullPath(fixture.Host.DataDirectory).ToUpperInvariant(), fixture.VaultBase);
        Assert.Equal(fixture.Key, vault.ReadKey(original.CredentialId));
        fixture.Store.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_metadata BEFORE INSERT ON app_settings WHEN NEW.key = 'unity_translation.settings' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END";
            command.ExecuteNonQuery();
        });
        var replacement = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var failed = fixture.Invoke("settings.set", new { apiKey = replacement, model = "replacement-model" });
        Assert.False(failed.Ok);
        Assert.DoesNotContain(replacement, JsonSerializer.Serialize(failed, ContractJson.Options));
        var stillOriginal = UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!;
        Assert.Equal(original, stillOriginal);
        Assert.Equal(fixture.Key, vault.ReadKey(stillOriginal.CredentialId));
        Assert.Equal(2, Directory.EnumerateFiles(vault.DirectoryPath, "*.bin").Count()); // Orphan new file cannot alter the old reference.
        fixture.Store.WriteExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_metadata";
            command.ExecuteNonQuery();
        });
        Assert.True(fixture.Invoke("settings.set", new { apiKey = replacement, model = "replacement-model" }).Ok);
        var current = UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!;
        Assert.NotEqual(original.CredentialId, current.CredentialId);
        Assert.Equal(replacement, vault.ReadKey(current.CredentialId));
        UnityTranslationPersistence.Write(fixture.Store, UnityTranslationPersistence.SettingsKey, original);
        Assert.True(fixture.Invoke("settings.set", new { model = "old-provider-new-model" }).Ok);
        Assert.Equal(fixture.Key, vault.ReadKey(UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!.CredentialId));
        File.Delete(Path.Combine(vault.DirectoryPath, original.CredentialId + ".bin"));
        Assert.False(Data(fixture.Invoke("settings.get", new { })).GetProperty("hasKey").GetBoolean());
        Assert.True(Data(fixture.Invoke("pending", new { })).GetProperty("needsSettings").GetBoolean());
    }

    [Fact]
    public async Task BindingSurvivesTagRename_AndReceiptsContainNoCredentialText()
    {
        await using var fixture = await Fixture.Create();
        var dispatcher = new OperationDispatcher(fixture.Host);
        var request = new IpcRequest
        {
            RequestId = "request-first",
            OperationId = "unity_translation.settings.set",
            Parameters = JsonSerializer.SerializeToElement(new { apiKey = fixture.Key, idempotencyKey = "setting-once" })
        };
        var first = dispatcher.Dispatch(request);
        Assert.True(first.Ok, first.Error?.Message);
        var settings = UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!;
        Assert.Equal(fixture.TagId, settings.BoundTagId);
        var replay = dispatcher.Dispatch(request);
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.Equal(settings.CredentialId, UnityTranslationPersistence.Read<UnityTranslationSettings>(fixture.Store, UnityTranslationPersistence.SettingsKey)!.CredentialId);
        var receipt = fixture.Store.TryGetReceipt("anonymous", "unity_translation.settings.set", "setting-once")!;
        Assert.NotNull(receipt.ResultJson);
        Assert.DoesNotContain(fixture.Key, receipt.ResultJson!);
        Assert.DoesNotContain(fixture.Key, receipt.RequestDigest);
        fixture.Store.UpdateTag(fixture.TagId, "等待翻译", null, null, null, null, null, false, 1, DateTime.UtcNow);
        var game = fixture.AddGame("renamed-tag");
        fixture.Service.RequestForTaggedGame(game.GameId);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.NotNull(fixture.ReadState(game.GameId));
        Assert.Equal(fixture.TagId, fixture.ReadState(game.GameId)!.BoundTagId);
    }

    [Fact]
    public async Task Startup_PersistsWizardQueue_DeclinesSurviveRestart_StaleAndWrongGameConfirmFail()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("pending");
        fixture.Service.Start();
        var pending = Data(fixture.Invoke("pending", new { }));
        Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal("needs_settings", pending.GetProperty("items")[0].GetProperty("state").GetString());
        var state = fixture.ReadState(game.GameId)! with { State = "configured" };
        fixture.WriteState(state);
        Assert.False(fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = "wrong", success = true }).Ok);
        Assert.False(fixture.Invoke("confirm", new { gameId = "another-game", attemptId = state.AttemptId, success = true }).Ok);
        var decline = fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = state.AttemptId, success = false });
        Assert.True(decline.Ok, decline.Error?.Message);
        fixture.RestartService();
        fixture.Service.Start();
        Assert.Empty(Data(fixture.Invoke("pending", new { })).GetProperty("items").EnumerateArray());
        Assert.Equal("declined", fixture.ReadState(game.GameId)!.State);
        Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
    }

    [Fact]
    public async Task Confirmation_RemovesOnlyBoundUserTag_RejectsChangedBindingAndEpoch()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("confirmation");
        fixture.Store.CreateTag(new("tag-other", "user", "收藏", null, 1, 0, DateTime.UtcNow, DateTime.UtcNow));
        fixture.Store.AssignTag(game.GameId, "tag-other", DateTime.UtcNow);
        var state = new UnityTranslationState
        {
            GameId = game.GameId,
            Title = game.Title,
            AttemptId = Guid.NewGuid().ToString("N"),
            DataEpoch = fixture.Store.Info.DataEpoch,
            State = "configured",
            BoundTagId = fixture.TagId,
            ProfileId = "profile-test",
            ExecutablePath = Path.Combine(game.RootPath, "Game.exe")
        };
        fixture.WriteState(state with { DataEpoch = "old-epoch" });
        Assert.False(fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = state.AttemptId, success = true }).Ok);
        fixture.WriteState(state);
        Assert.False(fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = state.AttemptId, success = true }).Ok);
        fixture.Host.Launches.RestoreAttempt(new LaunchAttempt
        {
            AttemptId = "launch-synthetic-record",
            GameId = game.GameId,
            ProfileId = state.ProfileId!,
            ExecutablePath = state.ExecutablePath!,
            Arguments = [],
            WorkingDirectory = game.RootPath,
            IdempotencyKey = "synthetic",
            State = "exited",
            CreatedUtc = state.UpdatedUtc.AddSeconds(1),
            ProcessStartedUtc = state.UpdatedUtc.AddSeconds(1),
            ProcessId = 1234
        });
        UnityTranslationPersistence.Write(fixture.Store, UnityTranslationPersistence.SettingsKey, new UnityTranslationSettings { BoundTagId = "tag-other" });
        Assert.False(fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = state.AttemptId, success = true }).Ok);
        UnityTranslationPersistence.Write(fixture.Store, UnityTranslationPersistence.SettingsKey, new UnityTranslationSettings { BoundTagId = fixture.TagId });
        Assert.True(fixture.Invoke("confirm", new { gameId = game.GameId, attemptId = state.AttemptId, success = true }).Ok);
        Assert.DoesNotContain(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        Assert.Contains(("user", "收藏"), fixture.Store.ListGameTags(game.GameId));
    }

    [Fact]
    public async Task AutoImport_UniqueSourcesReuseKey_MultipleDifferentSourcesReturnMetadataOnly()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("existing-config");
        var folder = Path.Combine(game.RootPath, "AutoTranslator");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Config.ini"), "[DeepSeek]\nModel=legacy-model\nApiKey=" + fixture.Key);
        fixture.Service.Start();
        var settings = Data(fixture.Invoke("settings.get", new { }));
        Assert.True(settings.GetProperty("hasKey").GetBoolean());
        Assert.Equal("legacy-model", settings.GetProperty("model").GetString());
        Assert.DoesNotContain(fixture.Key, settings.GetRawText());
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());

        await using var ambiguous = await Fixture.Create();
        foreach (var name in new[] { "first", "second" })
        {
            var source = ambiguous.AddGame(name);
            var directory = Path.Combine(source.RootPath, "AutoTranslator");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Config.ini"), "[DeepSeek]\nModel=" + name + "\nApiKey=" + ambiguous.Key);
        }
        ambiguous.Service.Start();
        var choice = Data(ambiguous.Invoke("settings.import", new { }));
        Assert.False(choice.GetProperty("imported").GetBoolean());
        Assert.Equal(2, choice.GetProperty("sources").GetArrayLength());
        Assert.DoesNotContain(ambiguous.Key, choice.GetRawText());
        Assert.False(Data(ambiguous.Invoke("settings.get", new { })).GetProperty("hasKey").GetBoolean());
    }

    [Fact]
    public async Task SyntheticEndpoint_ActuallyLoadsWithoutInitializing()
    {
        await using var fixture = await Fixture.Create();
        var data = Path.Combine(fixture.DirectoryPath, "Game_Data");
        await BuildSyntheticPlugins(data);
        var managed = Path.Combine(data, "Managed");
        var translators = Path.Combine(managed, "Translators");
        var script = Path.Combine(fixture.DirectoryPath, "verify.ps1");
        // This diagnostic helper only sees synthetic assemblies; it never reads any configuration or key.
        File.WriteAllText(script, UnityTranslationPayload.VerifyScript.Replace("catch { exit 1 }", "catch { Write-Output $_.Exception.ToString(); exit 1 }"));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, managed, managed, translators, Path.Combine(translators, "DeepSeekTranslate.dll") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task ExistingSyntheticRei_ConfiguresOffline_PreservesVersionsAndProfile_RestoresAndRetries()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("synthetic");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute this game");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data);
        var profile = fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var core = Path.Combine(data, "Managed", "XUnity.AutoTranslator.Plugin.Core.dll");
        var endpoint = Path.Combine(data, "Managed", "Translators", "DeepSeekTranslate.dll");
        var coreBefore = File.ReadAllBytes(core);
        var endpointBefore = File.ReadAllBytes(endpoint);
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[OtherMod]\nUntouched=keep\n[DeepSeek]\nModel=existing-model\n");
        var originalConfig = File.ReadAllBytes(config);
        Assert.True(fixture.Invoke("settings.set", new { provider = "deepseek", apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync(); // Setting up the provider queues the existing tagged batch once.
        var state = fixture.ReadState(game.GameId)!;
        Assert.True(state.State == "configured", state.Reason);
        Assert.Equal(coreBefore, File.ReadAllBytes(core));
        Assert.Equal(endpointBefore, File.ReadAllBytes(endpoint));
        Assert.Equal(profile, fixture.Host.Launches.GetDefaultProfile(game.GameId));
        var ini = UnityTranslationIni.Read(config);
        Assert.Equal("existing-model", ini.Get("DeepSeek", "Model"));
        Assert.Equal("DeepSeekTranslate", ini.Get("Service", "Endpoint"));
        Assert.Equal("keep", ini.Get("OtherMod", "Untouched"));
        Assert.True(TranslationLaunchRouteResolver.Resolve(game with { TranslationInherited = true }, profile).SatisfiedByEmbeddedPlugin);
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());
        Assert.DoesNotContain(fixture.Key, JsonSerializer.Serialize(fixture.Host.Jobs.TryGetProgress(state.JobId!), ContractJson.Options));
        Assert.Empty(fixture.Host.Launches.History());
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.Equal(originalConfig, File.ReadAllBytes(config));
        Assert.Equal(coreBefore, File.ReadAllBytes(core));
        var retry = fixture.Invoke("configure", new { gameIds = new[] { game.GameId } });
        Assert.True(retry.Ok, retry.Error?.Message);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("configured", fixture.ReadState(game.GameId)!.State);
        Assert.NotEqual(state.AttemptId, fixture.ReadState(game.GameId)!.AttemptId);
    }

    [Fact]
    public async Task DataDirectoryExe_DisabledOldDoorstopDoesNotConflictWithRei()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("nested");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data);
        var exe = Path.Combine(data, "Game.exe");
        File.WriteAllText(exe, "never execute");
        Directory.CreateDirectory(Path.Combine(data, "BepInEx"));
        File.WriteAllText(Path.Combine(data, "winhttp.dll"), "inactive-loader");
        File.WriteAllText(Path.Combine(data, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=false\n");
        var layout = UnityTranslationInspection.Inspect(exe);
        Assert.Equal("rei", layout.Loader);
        Assert.Null(layout.Reason);
        Assert.Equal(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini"), layout.Config);
        var profile = fixture.Host.Launches.AddProfile(game.GameId, exe, [], data, isDefault: true);
        Assert.True(TranslationLaunchRouteResolver.Resolve(game with { TranslationInherited = true }, profile).SatisfiedByEmbeddedPlugin);
        File.WriteAllText(Path.Combine(data, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\n");
        Assert.Equal("conflict", UnityTranslationInspection.Inspect(exe).Loader);
        Assert.False(TranslationLaunchRouteResolver.Resolve(game with { TranslationInherited = true }, profile).SatisfiedByEmbeddedPlugin);
    }

    [Fact]
    public async Task TagRevokedDuringPreparation_PreventsPluginConfiguration()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("revoked-tag");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        fixture.Host.Events.OnPublished = (_, _) =>
        {
            var current = fixture.ReadState(game.GameId);
            if (current?.State == "inspecting") fixture.Store.UnassignTag(game.GameId, fixture.TagId);
        };
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("blocked", fixture.ReadState(game.GameId)!.State);
        Assert.Contains("准备期间已改变", fixture.ReadState(game.GameId)!.Reason);
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task FailedEndpointLoad_RollsBackBeforePublishingConfigured()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("rollback");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data, invalidEndpoint: true);
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[OtherMod]\nKeep=yes\n");
        var original = File.ReadAllBytes(config);
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("failed", fixture.ReadState(game.GameId)!.State);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.DoesNotContain(fixture.Host.Events.ReadAfter(null, 1000)!, item => item.Type == "unity_translation.configured");
        Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
    }

    [Fact]
    public async Task BatchIsSerial_SecondGameInspectsOnlyAfterFirstConfigures()
    {
        await using var fixture = await Fixture.Create();
        var games = new[] { fixture.AddGame("serial-first"), fixture.AddGame("serial-second") };
        foreach (var game in games)
        {
            var exe = Path.Combine(game.RootPath, "Game.exe");
            File.WriteAllText(exe, "never execute");
            await BuildSyntheticPlugins(Path.Combine(game.RootPath, "Game_Data"));
            fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        }
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        var events = fixture.Host.Events.ReadAfter(null, 1000)!;
        var firstConfigured = events.Single(item => item.Type == "unity_translation.configured" && item.EntityKey == "game:" + games[0].GameId);
        var secondInspecting = events.Single(item => item.Type == "unity_translation.updated" && item.EntityKey == "game:" + games[1].GameId
            && JsonDocument.Parse(item.PayloadJson).RootElement.GetProperty("state").GetString() == "inspecting");
        Assert.True(firstConfigured.Sequence < secondInspecting.Sequence);
        Assert.Equal(fixture.ReadState(games[0].GameId)!.JobId, fixture.ReadState(games[1].GameId)!.JobId);
    }

    [Fact]
    public async Task CacheHashMismatchFailsClosedWithoutNetwork()
    {
        await using var fixture = await Fixture.Create();
        var cache = Path.Combine(fixture.DirectoryPath, "bad-cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "DeepSeekTranslate.dll"), [1, 2, 3]);
        var payload = new UnityTranslationPayload(cache);
        await Assert.ThrowsAsync<InvalidDataException>(() => payload.EndpointAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("il2cpp", "IL2CPP")]
    [InlineData("conflict", "BepInEx 核心")]
    [InlineData("readonly", "只读")]
    [InlineData("unknown-endpoint", "无法验证")]
    public async Task UnsafeExistingGames_LeaveTagAndReasonWithoutChangingFiles(string problem, string reason)
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("blocked-" + problem);
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.WriteAllText(exe, "never execute");
        var data = Path.Combine(game.RootPath, "Game_Data");
        await BuildSyntheticPlugins(data);
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var endpoint = Path.Combine(data, "Managed", "Translators", "DeepSeekTranslate.dll");
        var config = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[OtherMod]\nKeep=yes\n");
        var original = File.ReadAllBytes(config);
        if (problem == "il2cpp") File.WriteAllText(Path.Combine(game.RootPath, "GameAssembly.dll"), "il2cpp-marker");
        if (problem == "conflict")
        {
            File.WriteAllText(Path.Combine(game.RootPath, "winhttp.dll"), "active-loader");
            File.WriteAllText(Path.Combine(game.RootPath, "doorstop_config.ini"), "[General]\nenabled=true\n");
        }
        if (problem == "readonly") File.SetAttributes(config, FileAttributes.ReadOnly);
        if (problem == "unknown-endpoint") File.WriteAllText(endpoint, "another-mod");
        try
        {
            Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
            await fixture.Host.Jobs.WaitForIdleAsync();
            var state = fixture.ReadState(game.GameId)!;
            Assert.Equal("blocked", state.State);
            Assert.Contains(reason, state.Reason);
            Assert.Equal(original, File.ReadAllBytes(config));
            Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        }
        finally { File.SetAttributes(config, FileAttributes.Normal); }
    }

    [Fact]
    public void RunningDetection_RecognizesCurrentProcessWithoutLaunchingAnything()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(UnityTranslationInspection.IsRunning(current.MainModule!.FileName));
    }

    [Fact]
    public async Task Configure_InvalidGameAndMissingKeyKeepTag_QueueRejectsDuplicateActiveAttempt()
    {
        await using var fixture = await Fixture.Create();
        var game = fixture.AddGame("unknown");
        var response = fixture.Invoke("configure", new { gameIds = new[] { game.GameId } });
        Assert.True(response.Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal("blocked", fixture.ReadState(game.GameId)!.State);
        Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        fixture.WriteState(fixture.ReadState(game.GameId)! with { State = "installing" });
        Assert.False(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
    }

    private static JsonElement Data(Envelope<object> envelope) => JsonSerializer.SerializeToElement(envelope.Data, ContractJson.Options);

    [Fact]
    public async Task SplitUnityModule_PatcherResolvesEnumAttributeFromOriginalManagedDirectory()
    {
        await using var fixture = await Fixture.Create();
        var data = Path.Combine(fixture.DirectoryPath, "SplitGame_Data");
        await BuildSyntheticPlugins(data, splitDependency: true);
        var managed = Path.Combine(data, "Managed");
        var bootstrap = Path.Combine(managed, "UnityEngine.CoreModule.dll");
        var before = File.ReadAllBytes(bootstrap);
        Assert.False(UnityTranslationInspection.HasBootstrap(bootstrap));
        var payload = new UnityTranslationPayload(Path.Combine(fixture.DirectoryPath, "payload"));
        var runtime = await payload.RuntimeAsync(CancellationToken.None);
        var layout = new UnityTranslationLayout("unused.exe", fixture.DirectoryPath, data, managed, bootstrap, "none",
            Path.Combine(managed, "XUnity.AutoTranslator.Plugin.Core.dll"), Path.Combine(managed, "Translators"), "unused.ini");
        var patched = await payload.PatchAsync(layout, runtime, CancellationToken.None);
        var output = Path.Combine(fixture.DirectoryPath, "patched.dll");
        File.WriteAllBytes(output, patched);
        Assert.True(UnityTranslationInspection.HasBootstrap(output));
        Assert.Equal(before, File.ReadAllBytes(bootstrap));
    }

    [Theory]
    [InlineData("x64", "2020")]
    [InlineData("x86", "2020")]
    [InlineData("x64", "6000")]
    public async Task PinnedIl2CppPackages_ConfigureRouteWithoutLaunching_PreserveConfirmation_RestoreAndRetry(string architecture, string generation)
    {
        await using var fixture = await Fixture.Create();
        var engine = JsonSerializer.SerializeToElement(GameLibrary.Domain.Detection.EngineId.Unity, ContractJson.Options).GetString()!;
        var game = fixture.AddGame("il2cpp-" + architecture, engine);
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), architecture == "x64" ? "System32" : "SysWOW64", "kernel32.dll");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.Copy(native, exe);
        File.Copy(native, Path.Combine(game.RootPath, "GameAssembly.dll"));
        Directory.CreateDirectory(Path.Combine(game.RootPath, "Game_Data", "il2cpp_data"));
        File.WriteAllText(Path.Combine(game.RootPath, "Game_Data", "globalgamemanagers"), "\0\0\0\u0014" + generation + ".0.59f2\0");
        fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var layout = UnityTranslationInspection.Inspect(exe);
        Assert.Null(layout.Reason);
        Assert.Equal("il2cpp", layout.Runtime);
        Assert.Equal(architecture, layout.Architecture);
        Assert.Equal("none", layout.Loader);
        var configPath = Path.Combine(game.RootPath, "BepInEx", "config", "AutoTranslatorConfig.ini");
        Assert.Equal(configPath, layout.Config); // XUnity v5.6.2 BepInEx plugin's actual preferences path.
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        var state = fixture.ReadState(game.GameId)!;
        Assert.True(state.State == "configured", state.Reason);
        var installed = UnityTranslationInspection.Inspect(exe);
        Assert.Null(installed.Reason);
        Assert.Equal("bepinex", installed.Loader);
        var ini = UnityTranslationIni.Read(configPath);
        Assert.Equal(UnityTranslationFonts.SystemFont, ini.Get("Behaviour", "OverrideFont"));
        Assert.Equal(UnityTranslationFonts.BundlePath(generation), ini.Get("Behaviour", "FallbackFontTextMeshPro"));
        var fontPath = Path.Combine(game.RootPath, UnityTranslationFonts.BundlePath(generation));
        Assert.StartsWith("UnityFS\0", Encoding.ASCII.GetString(File.ReadAllBytes(fontPath), 0, 8));
        Assert.Equal("zh", ini.Get("General", "Language"));
        Assert.Equal("auto", ini.Get("General", "FromLanguage"));
        Assert.Equal("DeepSeekTranslate", ini.Get("Service", "Endpoint"));
        var interopPath = Path.Combine(game.RootPath, UnityTranslationAssembly.InteropPath);
        Assert.Equal(generation == "6000" ? UnityTranslationAssembly.PatchedInteropHash : UnityTranslationAssembly.OriginalInteropHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(interopPath))));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini")));
        Assert.True(UnityTranslationInspection.IsConfiguredChineseTranslator(installed));
        var chinese = File.ReadAllText(configPath);
        Directory.CreateDirectory(Path.Combine(game.RootPath, "AutoTranslator"));
        File.WriteAllText(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini"), chinese);
        File.WriteAllText(configPath, "[General]\nLanguage=en\nFromLanguage=ja\n");
        Assert.False(UnityTranslationInspection.IsConfiguredChineseTranslator(UnityTranslationInspection.Inspect(exe)));
        File.WriteAllText(configPath, chinese);
        File.Delete(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini"));
        var profile = fixture.Host.Launches.GetDefaultProfile(game.GameId)!;
        var route = TranslationLaunchRouteResolver.Resolve(game with { TranslationInherited = true }, profile);
        Assert.True(route.SatisfiedByEmbeddedPlugin, route.UnavailableReason);
        fixture.Service.RequestForTaggedGame(game.GameId);
        Assert.Equal("configured", fixture.ReadState(game.GameId)?.State);
        Assert.Contains(("user", "未翻译"), fixture.Store.ListGameTags(game.GameId));
        Assert.Empty(fixture.Host.Launches.History());
        Assert.False(fixture.PersistedSettings().Contains(fixture.Key, StringComparison.Ordinal));
        // Real loaders generate these after the install transaction; restore keeps them and permits retry.
        var generatedConfig = Path.Combine(game.RootPath, "BepInEx", "config", "BepInEx.cfg");
        var generatedLog = Path.Combine(game.RootPath, "BepInEx", "LogOutput.log");
        File.WriteAllText(generatedConfig, "[IL2CPP]\nUpdateInteropAssemblies=true\n");
        File.WriteAllText(generatedLog, "generated loader log");
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.False(File.Exists(Path.Combine(game.RootPath, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(game.RootPath, "AutoTranslator", "Config.ini")));
        Assert.False(File.Exists(configPath));
        Assert.False(File.Exists(fontPath));
        Assert.Equal("generated loader log", File.ReadAllText(generatedLog));
        Assert.NotNull(fixture.ReadState(game.GameId)?.RestoredBackupId);
        Assert.Equal("none", UnityTranslationInspection.Inspect(exe).Loader);
        var unknownMod = Path.Combine(game.RootPath, "BepInEx", "plugins", "unrecognized.dll");
        File.WriteAllText(unknownMod, "preserve");
        Assert.Equal("unknown", UnityTranslationInspection.Inspect(exe).Loader);
        File.Delete(unknownMod);
        fixture.RestartService();
        Assert.True(fixture.Invoke("configure", new { gameIds = new[] { game.GameId } }).Ok);
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.True(fixture.ReadState(game.GameId)?.State == "configured", fixture.ReadState(game.GameId)?.Reason);
        Assert.Equal(state.BackupId, fixture.ReadState(game.GameId)?.BackupId);
    }

    [Theory]
    [InlineData("confirmed", false)]
    [InlineData("configured", true)]
    [InlineData("declined", true)]
    [InlineData("unowned", false)]
    public async Task Startup_RepairsOwnedLegacyBepInExConfig_WithoutTagOrTouchingEnglishCache(string previousState, bool tagged)
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Invoke("settings.set", new { apiKey = fixture.Key }).Ok);
        var game = fixture.AddGame("legacy-il2cpp");
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
        var exe = Path.Combine(game.RootPath, "Game.exe");
        File.Copy(native, exe);
        File.Copy(native, Path.Combine(game.RootPath, "GameAssembly.dll"));
        Directory.CreateDirectory(Path.Combine(game.RootPath, "Game_Data", "il2cpp_data"));
        var profile = fixture.Host.Launches.AddProfile(game.GameId, exe, [], game.RootPath, isDefault: true);
        var payload = new UnityTranslationPayload();
        var backupId = Guid.NewGuid().ToString("N");
        var vault = new UnityTranslationVault(Path.GetFullPath(fixture.Host.DataDirectory).ToUpperInvariant(), fixture.VaultBase);
        var transaction = new UnityTranslationTransaction(vault, backupId, game.RootPath);
        foreach (var pair in await payload.Il2CppAsync("x64", includeLoader: true, CancellationToken.None))
            transaction.Write(Path.Combine(game.RootPath, pair.Key), pair.Value);
        var layout = UnityTranslationInspection.Inspect(exe);
        transaction.Write(Path.Combine(layout.Translators, "DeepSeekTranslate.dll"), await payload.EndpointAsync(CancellationToken.None));
        var legacy = Path.Combine(game.RootPath, "AutoTranslator", "Config.ini");
        transaction.Write(legacy, new UnityTranslationIni("").Configure(new(), fixture.Key));
        transaction.Commit();
        var english = "[General]\nLanguage=en\nFromLanguage=ja\n[OtherMod]\nValue=keep\n";
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Config)!);
        File.WriteAllText(layout.Config, english); // Generated by the real BepInEx plugin on its first launch.
        var cache = Path.Combine(game.RootPath, "BepInEx", "Translation", "en", "Text", "_AutoGeneratedTranslations.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "original=English cached translation");
        if (!tagged) fixture.Store.UnassignTag(game.GameId, fixture.TagId);
        if (previousState != "unowned") fixture.WriteState(new()
        {
            GameId = game.GameId, Title = game.Title, AttemptId = "legacy", DataEpoch = fixture.Store.Info.DataEpoch,
            State = previousState, BackupId = backupId, InstallRoot = game.RootPath, BoundTagId = fixture.TagId,
            ProfileId = profile.ProfileId, ExecutablePath = exe
        });
        fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        if (previousState is "declined" or "unowned")
        {
            Assert.Equal(english, File.ReadAllText(layout.Config));
            return;
        }
        var repaired = fixture.ReadState(game.GameId)!;
        Assert.True(repaired.State == "configured", repaired.Reason);
        Assert.Equal(backupId, repaired.BackupId);
        Assert.Equal("zh", UnityTranslationIni.Read(layout.Config).Get("General", "Language"));
        Assert.Equal("keep", UnityTranslationIni.Read(layout.Config).Get("OtherMod", "Value"));
        Assert.Equal("original=English cached translation", File.ReadAllText(cache));
        Assert.Empty(fixture.Host.Launches.History());
        fixture.RestartService();
        fixture.Service.Start();
        await fixture.Host.Jobs.WaitForIdleAsync();
        Assert.Equal(repaired.AttemptId, fixture.ReadState(game.GameId)?.AttemptId);
        Assert.DoesNotContain(fixture.Key, fixture.PersistedSettings());
        Assert.True(fixture.Invoke("restore", new { gameId = game.GameId }).Ok);
        Assert.Equal(english, File.ReadAllText(layout.Config));
        Assert.False(File.Exists(legacy));
        Assert.Equal("original=English cached translation", File.ReadAllText(cache));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("BepInEx/core/../../escape.dll")]
    [InlineData("BepInEx/core/escape.dll:stream")]
    public void Il2CppArchives_RejectTraversalAndAlternateStreams(string path)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var output = new StreamWriter(archive.CreateEntry(path).Open())) output.Write("untrusted");
        var error = Assert.Throws<InvalidDataException>(() => UnityTranslationPayload.ExtractIl2CppArchive(buffer.ToArray(), loader: true));
        Assert.Contains("非法路径", error.Message);
    }

    private static async Task BuildSyntheticPlugins(string data, bool invalidEndpoint = false, bool splitDependency = false,
        bool dualLoader = false, bool systemTmp = false)
    {
        var managed = Path.Combine(data, "Managed");
        var translators = Path.Combine(managed, "Translators");
        Directory.CreateDirectory(translators);
        var script = Path.Combine(data, "synthetic.ps1");
        var source = """
            param($managed, $translators)
            $ErrorActionPreference='Stop'
            try {
              $core=Join-Path $managed 'XUnity.AutoTranslator.Plugin.Core.dll'
              Add-Type -TypeDefinition 'namespace XUnity.AutoTranslator.Plugin.Core { public static class PluginLoader { public static void LoadThroughBootstrapper() {} } namespace Endpoints { public interface ITranslateEndpoint { void Initialize(); void Translate(); } } }' -OutputAssembly $core
              Add-Type -TypeDefinition 'namespace UnityEngine { public class Input { static Input() { XUnity.AutoTranslator.Plugin.Core.PluginLoader.LoadThroughBootstrapper(); } } public class Display { static Display() {} } }' -ReferencedAssemblies $core -OutputAssembly (Join-Path $managed 'UnityEngine.CoreModule.dll')
              Add-Type -TypeDefinition 'public class DummyGame {}' -OutputAssembly (Join-Path $managed 'Assembly-CSharp.dll')
              Add-Type -TypeDefinition 'namespace DeepSeekTranslate { public class DeepSeekTranslateEndpoint : XUnity.AutoTranslator.Plugin.Core.Endpoints.ITranslateEndpoint { public string Id { get { return "DeepSeekTranslate"; } } public void Initialize() { throw new System.Exception("Must never run"); } public void Translate() { throw new System.Exception("Must never run"); } } }' -ReferencedAssemblies $core -OutputAssembly (Join-Path $translators 'DeepSeekTranslate.dll')
              exit 0
            } catch { exit 1 }
            """;
        if (invalidEndpoint) source = source.Replace(" : XUnity.AutoTranslator.Plugin.Core.Endpoints.ITranslateEndpoint", "");
        if (dualLoader) source = source.Replace("exit 0", """
              $root=Split-Path (Split-Path $managed)
              $bepin=Join-Path $root 'BepInEx'
              $bepinCore=Join-Path $bepin 'core'
              $plugins=Join-Path $bepin 'plugins'
              New-Item -ItemType Directory -Force -Path $bepinCore,$plugins | Out-Null
              Add-Type -TypeDefinition 'public class BepInCore {}' -OutputAssembly (Join-Path $bepinCore 'BepInEx.dll')
              Add-Type -TypeDefinition 'public class BepInPreloader {}' -OutputAssembly (Join-Path $bepinCore 'BepInEx.Unity.Mono.Preloader.dll')
              Add-Type -TypeDefinition 'public class BepInTranslator {}' -OutputAssembly (Join-Path $plugins 'XUnity.AutoTranslator.Plugin.BepInEx.dll')
              Copy-Item -LiteralPath $core -Destination (Join-Path $plugins 'XUnity.AutoTranslator.Plugin.Core.dll')
              Copy-Item -LiteralPath $translators -Destination $plugins -Recurse
              Copy-Item -LiteralPath (Join-Path $managed 'UnityEngine.CoreModule.dll') -Destination (Join-Path $managed 'UnityEngine.CoreModule.dll.untrusted.bak')
              Set-Content -LiteralPath (Join-Path $root 'winhttp.dll') -Value 'never execute'
              Set-Content -LiteralPath (Join-Path $root 'doorstop_config.ini') -Value "[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Unity.Mono.Preloader.dll"
              exit 0
            """);
        if (systemTmp) source = source.Replace("exit 0", """
              Add-Type -TypeDefinition 'namespace TMPro { public class TMP_FontAsset { public static TMP_FontAsset CreateFontAsset(string family, string style, int size) { throw new System.Exception("Must never run"); } } }' -OutputAssembly (Join-Path $managed 'Unity.TextMeshPro.dll')
              exit 0
            """);
        if (splitDependency)
        {
            var original = source.Split('\n').Single(line => line.Contains("namespace UnityEngine {", StringComparison.Ordinal));
            source = source.Replace(original, """
                $shared=Join-Path $managed 'UnityEngine.SharedInternalsModule.dll'
                Add-Type -TypeDefinition 'namespace UnityEngine.Shared { public enum Scope { Both } public class NativeAttribute : System.Attribute { public NativeAttribute(Scope value) {} } }' -OutputAssembly $shared
                Add-Type -TypeDefinition 'namespace UnityEngine { [Shared.Native(Shared.Scope.Both)] public class Display { static Display() { System.GC.KeepAlive(typeof(Display)); } } }' -ReferencedAssemblies $shared -OutputAssembly (Join-Path $managed 'UnityEngine.CoreModule.dll')
                """);
        }
        File.WriteAllText(script, source);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, managed, translators }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "GameLibrary-UnityTests-" + Guid.NewGuid().ToString("N"));
        public string VaultBase => Path.Combine(DirectoryPath, "vault");
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public string TagId { get; } = "tag-untranslated";
        public SqliteLibraryStore Store => Host.Library.Store!;
        public HostRuntimeState Host { get; private set; } = null!;
        public UnityTranslationService Service { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.DirectoryPath);
            var data = Path.Combine(fixture.DirectoryPath, "data");
            var opened = await SqliteLibraryStore.InitializeAsync(data, new SqliteLibraryStoreOptions { AppVersion = "test", ApiVersion = ApiConstants.ApiVersion }, CancellationToken.None);
            Assert.True(opened.IsOpened, opened.Detail);
            var metrics = new HostMetrics();
            fixture.Host = new()
            {
                Identity = new(),
                DataDirectory = data,
                Library = new() { Status = LibraryOpenStatus.Opened, Store = opened.Store },
                Jobs = new(),
                Candidates = new(),
                Launches = new(),
                Roots = new(),
                Events = new(opened.Store),
                Metrics = metrics,
                AuditLog = new(Path.Combine(fixture.DirectoryPath, "logs"))
            };
            fixture.Host.Roots.Add(fixture.DirectoryPath);
            fixture.Store.CreateTag(new(fixture.TagId, "user", "未翻译", null, 1, 0, DateTime.UtcNow, DateTime.UtcNow));
            fixture.RestartService();
            return fixture;
        }
        public void RestartService() => Host.UnityTranslations = Service = new(Host, new UnityTranslationPayload(Path.Combine(DirectoryPath, "empty-cache")), VaultBase);
        public GameCard AddGame(string name, string engine = "unity")
        {
            var root = Path.Combine(DirectoryPath, "games", name);
            Directory.CreateDirectory(root);
            var game = new GameCard
            {
                GameId = "game-" + name,
                Title = name,
                RootPath = root,
                Engine = engine,
                Kind = "game",
                Membership = "active",
                AcceptedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            Store.InsertGame(game);
            Store.AssignTag(game.GameId, TagId, DateTime.UtcNow);
            return game;
        }
        public UnityTranslationState? ReadState(string gameId) => UnityTranslationPersistence.Read<UnityTranslationState>(Store, UnityTranslationPersistence.StatePrefix + gameId);
        public void WriteState(UnityTranslationState state) => UnityTranslationPersistence.Write(Store, UnityTranslationPersistence.StatePrefix + state.GameId, state);
        public Envelope<object> Invoke(string suffix, object parameters) => Service.Handle(new IpcRequest
        { RequestId = Guid.NewGuid().ToString("N"), OperationId = "unity_translation." + suffix, Parameters = JsonSerializer.SerializeToElement(parameters, ContractJson.Options) });
        public string PersistedSettings() => Store.ReadExclusive((connection, _) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return string.Join("\n", values);
        });
        public async ValueTask DisposeAsync()
        {
            await Host.Jobs.WaitForIdleAsync();
            await Store.DisposeAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
