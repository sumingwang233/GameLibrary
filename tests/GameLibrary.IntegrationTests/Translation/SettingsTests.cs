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
        var baseline = await InvokeAsync("settings.get");
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
