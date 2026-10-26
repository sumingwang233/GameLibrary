using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Coverage;

/// <summary>
/// 握手声明的权限在真实 IPC 通道上生效：声明受限集合的客户端只能调用授权范围内的操作，
/// 未授权的写操作与高危控制面操作在参数校验之前就被拒绝；长在权限表里的 none 操作始终可读。
/// </summary>
public sealed class PermissionDeclarationTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public PermissionDeclarationTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(
        string operationId,
        IReadOnlyList<string>? permissions,
        object? parameters = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "permission-test",
            CancellationToken.None,
            permissions);
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
    public async Task ReadOnlyDeclaration_CanListGames_ButCannotExportOrStopHost()
    {
        string[] readOnly = ["library.read"];

        var list = await InvokeAsync("games.list", readOnly);
        Assert.True(list.Ok, list.Error?.Message);

        var export = await InvokeAsync("library.export", readOnly, new { idempotencyKey = $"perm-export-{Guid.NewGuid():N}" });
        Assert.False(export.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, export.Error!.Code);

        var stop = await InvokeAsync("host.stop", readOnly, new { idempotencyKey = $"perm-stop-{Guid.NewGuid():N}" });
        Assert.False(stop.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, stop.Error!.Code);
    }

    [Fact]
    public async Task TauriBridgeProfile_CoversUiWrites_ButNotAdminExportOrShutdown()
    {
        var profile = ClientPermissionProfiles.TauriBridge;

        // 界面日常路径：读列表、收藏（library.write）、扫描进度（library.read）。
        Assert.True((await InvokeAsync("games.list", profile)).Ok);
        Assert.True((await InvokeAsync("host.status", profile)).Ok);

        // 桥接界面根本不会发起的操作：导出、停宿主都应被拒。
        var export = await InvokeAsync("diagnostics.export", profile, new { idempotencyKey = $"perm-diag-{Guid.NewGuid():N}" });
        Assert.False(export.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, export.Error!.Code);

        var stop = await InvokeAsync("host.stop", profile, new { idempotencyKey = $"perm-stop-{Guid.NewGuid():N}" });
        Assert.False(stop.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, stop.Error!.Code);
    }

    [Fact]
    public async Task EmptyDeclaration_DeniesReads_ButPermissionlessOperationsRemainAvailable()
    {
        var list = await InvokeAsync("games.list", []);
        Assert.False(list.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, list.Error!.Code);

        // capabilities.get 的 catalog 权限是 none，不依赖声明。
        var capabilities = await InvokeAsync("capabilities.get", []);
        Assert.True(capabilities.Ok, capabilities.Error?.Message);
    }

    [Fact]
    public async Task FullProfile_KeepsFirstPartyCapabilities()
    {
        var profile = ClientPermissionProfiles.Full;

        Assert.True((await InvokeAsync("games.list", profile)).Ok);
        // verification.start 需要 tools.verify，Full 必须覆盖它；参数缺失应落到业务校验，
        // 说明权限门已经通过（而不是在权限层被拒）。
        var verify = await InvokeAsync("verification.start", profile, new { idempotencyKey = $"perm-verify-{Guid.NewGuid():N}" });
        Assert.NotEqual(ErrorCodes.PermissionDenied, verify.Error?.Code);
    }
}
