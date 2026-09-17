using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Translation;

/// <summary>settings 三入口：快照默认值、受限 patch、Revision、开机启动快捷方式、reset、激活视图持久化。</summary>
public sealed class SettingsTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public SettingsTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "settings-test",
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

    [Fact]
    public async Task SettingsGet_ReturnsConsistentSnapshotShape()
    {
        var envelope = await InvokeAsync("settings.get");

        Assert.True(envelope.Ok, envelope.Error?.Message);
        var data = envelope.Data;
        Assert.True(data.GetProperty("revision").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.False, data.GetProperty("autostartEnabled").ValueKind == JsonValueKind.True ? JsonValueKind.True : JsonValueKind.False);
        Assert.True(data.GetProperty("scanIntervalMinutes").GetInt32() > 0);
        Assert.Contains(data.GetProperty("theme").GetString(), new[] { "dark", "light", "system" });
        Assert.True(data.GetProperty("closeToTray").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("uiFontFamily").GetString()));
        Assert.True(data.GetProperty("cacheParentDirectory").ValueKind is JsonValueKind.Null or JsonValueKind.String);
    }

    [Fact]
    public async Task SettingsUpdate_UnknownField_IsRejected()
    {
        var envelope = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = 0,
            notAField = true,
        });

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, envelope.Error!.Code);
        Assert.Contains("notAField", envelope.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsUpdate_IntervalAndTheme_BumpsRevision()
    {
        var baseline = await InvokeAsync("settings.get");
        var revision = baseline.Data.GetProperty("revision").GetInt32();

        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            scanIntervalMinutes = 30,
            theme = "light",
        });

        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal(revision + 1, update.Data.GetProperty("revision").GetInt32());
        Assert.Equal(30, update.Data.GetProperty("scanIntervalMinutes").GetInt32());
        Assert.Equal("light", update.Data.GetProperty("theme").GetString());

        // 旧 Revision 再改 → 冲突并报告当前值。
        var stale = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            theme = "dark",
        });
        Assert.False(stale.Ok);
        Assert.Equal(ErrorCodes.RevisionConflict, stale.Error!.Code);
        Assert.Equal(revision + 1, stale.Error.CurrentRevision);
    }

    [Fact]
    public async Task SettingsUpdate_InvalidValues_AreRejected()
    {
        var baseline = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"st-reset-{Guid.NewGuid():N}",
        });
        var revision = baseline.Data.GetProperty("revision").GetInt32();

        var badInterval = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            scanIntervalMinutes = 0,
        });
        Assert.False(badInterval.Ok);

        var badTheme = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            theme = "retro",
        });
        Assert.False(badTheme.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, badTheme.Error!.Code);

        var badFont = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            uiFontFamily = "C:\\some-font.ttf#Font",
            autostartEnabled = true,
        });
        Assert.False(badFont.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, badFont.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_fixture.StartupDir, "GameLibrary.lnk")),
            "无效设置请求不得留下开机启动快捷方式");
    }

    [Fact]
    public async Task SettingsUpdate_FontChoiceAndScale_PersistAndReset()
    {
        var baseline = await InvokeAsync("settings.get");
        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-font-{Guid.NewGuid():N}",
            expectedRevision = baseline.Data.GetProperty("revision").GetInt32(),
            uiFontFamily = "Microsoft YaHei UI",
            uiFontScale = 1.25,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal("Microsoft YaHei UI", update.Data.GetProperty("uiFontFamily").GetString());
        Assert.Equal(1.25, update.Data.GetProperty("uiFontScale").GetDouble());

        var persisted = await InvokeAsync("settings.get");
        Assert.Equal("Microsoft YaHei UI", persisted.Data.GetProperty("uiFontFamily").GetString());
        Assert.Equal(1.25, persisted.Data.GetProperty("uiFontScale").GetDouble());

        var reset = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"st-reset-{Guid.NewGuid():N}",
        });
        Assert.True(reset.Ok, reset.Error?.Message);
        Assert.Equal("Segoe UI", reset.Data.GetProperty("uiFontFamily").GetString());
        Assert.Equal(1.0, reset.Data.GetProperty("uiFontScale").GetDouble());
    }

    [Fact]
    public async Task SettingsUpdate_CacheParent_PersistsValidDirectory_AndRejectsMissingDirectory()
    {
        var baseline = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"st-reset-{Guid.NewGuid():N}",
        });
        var cacheParent = Path.Combine(
            @"D:\Official\GameLibrary\artifacts\test-runs", _fixture.TestId, "selected-cache-parent");
        Directory.CreateDirectory(cacheParent);

        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-cache-{Guid.NewGuid():N}",
            expectedRevision = baseline.Data.GetProperty("revision").GetInt32(),
            cacheParentDirectory = cacheParent,
        });
        Assert.True(update.Ok, update.Error?.Message);
        Assert.Equal(cacheParent, update.Data.GetProperty("cacheParentDirectory").GetString());

        var persisted = await InvokeAsync("settings.get");
        Assert.Equal(cacheParent, persisted.Data.GetProperty("cacheParentDirectory").GetString());

        var missing = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-cache-missing-{Guid.NewGuid():N}",
            expectedRevision = persisted.Data.GetProperty("revision").GetInt32(),
            cacheParentDirectory = Path.Combine(cacheParent, "does-not-exist"),
        });
        Assert.False(missing.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, missing.Error!.Code);

        var reset = await InvokeAsync("settings.update", new Dictionary<string, object?>
        {
            ["idempotencyKey"] = $"st-cache-default-{Guid.NewGuid():N}",
            ["expectedRevision"] = persisted.Data.GetProperty("revision").GetInt32(),
            ["cacheParentDirectory"] = null,
        });
        Assert.True(reset.Ok, reset.Error?.Message);
        Assert.Equal(JsonValueKind.Null, reset.Data.GetProperty("cacheParentDirectory").ValueKind);
    }

    [Fact]
    public async Task McpSettingsUpdate_OmitsUnspecifiedOptionalFields()
    {
        var baseline = await InvokeAsync("settings.get");
        var session = typeof(GameLibrary.Mcp.GameLibraryTools).Assembly
            .GetType("GameLibrary.Mcp.McpSession")!;
        var dataDirectory = session.GetProperty("DataDirectory",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var previous = dataDirectory.GetValue(null);
        dataDirectory.SetValue(null,
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data");
        try
        {
            var cacheParent = Path.Combine(
                @"D:\Official\GameLibrary\artifacts\test-runs", _fixture.TestId, "mcp-cache-parent");
            Directory.CreateDirectory(cacheParent);
            var result = await GameLibrary.Mcp.GameLibraryTools.SettingsUpdate(
                baseline.Data.GetProperty("revision").GetInt32(),
                theme: "light",
                cacheParentDirectory: cacheParent);
            Assert.False(result.IsError);
            var persisted = await InvokeAsync("settings.get");
            Assert.Equal("light", persisted.Data.GetProperty("theme").GetString());
            Assert.Equal(cacheParent, persisted.Data.GetProperty("cacheParentDirectory").GetString());

            var clear = await GameLibrary.Mcp.GameLibraryTools.SettingsUpdate(
                persisted.Data.GetProperty("revision").GetInt32(),
                clearCacheParentDirectory: true);
            Assert.False(clear.IsError);
            var cleared = await InvokeAsync("settings.get");
            Assert.Equal(JsonValueKind.Null, cleared.Data.GetProperty("cacheParentDirectory").ValueKind);
        }
        finally
        {
            dataDirectory.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task SettingsUpdate_Autostart_CreatesStartupShortcut()
    {
        var baseline = await InvokeAsync("settings.get");
        var revision = baseline.Data.GetProperty("revision").GetInt32();

        var update = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = revision,
            autostartEnabled = true,
        });

        Assert.True(update.Ok, update.Error?.Message);
        Assert.True(update.Data.GetProperty("autostartEnabled").GetBoolean());
        var shortcutPath = Path.Combine(_fixture.StartupDir, "GameLibrary.lnk");
        Assert.True(File.Exists(shortcutPath), $"启动快捷方式应存在：{shortcutPath}");

        // 关闭：快捷方式移除。
        var disable = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = update.Data.GetProperty("revision").GetInt32(),
            autostartEnabled = false,
        });
        Assert.True(disable.Ok, disable.Error?.Message);
        Assert.False(disable.Data.GetProperty("autostartEnabled").GetBoolean());
        Assert.False(File.Exists(shortcutPath));
    }

    [Fact]
    public async Task SettingsReset_RestoresDefaults_AndRemovesAutostart()
    {
        var baseline = await InvokeAsync("settings.get");
        var enable = await InvokeAsync("settings.update", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
            expectedRevision = baseline.Data.GetProperty("revision").GetInt32(),
            autostartEnabled = true,
            scanIntervalMinutes = 45,
        });
        Assert.True(enable.Ok, enable.Error?.Message);

        var reset = await InvokeAsync("settings.reset", new
        {
            idempotencyKey = $"st-{Guid.NewGuid():N}",
        });

        Assert.True(reset.Ok, reset.Error?.Message);
        var data = reset.Data;
        Assert.False(data.GetProperty("autostartEnabled").GetBoolean());
        Assert.Equal(15, data.GetProperty("scanIntervalMinutes").GetInt32());
        Assert.False(File.Exists(Path.Combine(_fixture.StartupDir, "GameLibrary.lnk")));
    }

    [Fact]
    public async Task ViewsActivate_PersistsToSettings()
    {
        var activate = await InvokeAsync("views.activate", new
        {
            idempotencyKey = "st-act-favorites",
            viewId = "favorites",
        });
        Assert.True(activate.Ok, activate.Error?.Message);

        var settings = await InvokeAsync("settings.get");
        Assert.Equal("favorites", settings.Data.GetProperty("activeViewId").GetString());
    }
}
