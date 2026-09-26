using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests;

/// <summary>
/// v1 审查修复回归（IPC 层）：
/// 1) 响应信封必须携带 libraryInstanceId/dataEpoch（不再为 null）；
/// 2) 请求携带错误纪元/实例被拒绝（DataEpochMismatch / LibraryInstanceMismatch）；
/// 3) roots.add 落库（运行态持久化）；
/// 4) schema.get 返回真实 inputSchema（不再返回 null 占位）；
/// 5) 幂等键格式校验生效。
/// </summary>
public sealed class EpochGuardTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public EpochGuardTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(
        string operationId, object? parameters = null, string? epochOverride = null, string? instanceOverride = null)
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "epoch-guard-test",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(parameters ?? new { });
        return await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
                LibraryInstanceId = instanceOverride ?? _fixture.State.Library.LibraryInstanceId,
                ExpectedDataEpoch = epochOverride ?? _fixture.State.Library.DataEpoch,
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task HostStatus_EnvelopeCarriesLibraryInstanceAndEpoch()
    {
        var envelope = await InvokeAsync("host.status");
        Assert.True(envelope.Ok, envelope.Error?.Message);
        Assert.False(string.IsNullOrEmpty(envelope.LibraryInstanceId));
        Assert.False(string.IsNullOrEmpty(envelope.DataEpoch));
        Assert.Equal(
            _fixture.State.Library.Store!.Info.DataEpoch,
            envelope.DataEpoch);
    }

    [Fact]
    public async Task Request_WithStaleEpoch_IsRejectedWithDataEpochMismatch()
    {
        var envelope = await InvokeAsync("candidates.list", epochOverride: "00000000-dead-beef-0000-000000000000");
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.DataEpochMismatch, envelope.Error!.Code);
    }

    [Fact]
    public async Task Request_WithWrongLibraryInstance_IsRejected()
    {
        var envelope = await InvokeAsync("candidates.list", instanceOverride: "not-the-current-instance");
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.LibraryInstanceMismatch, envelope.Error!.Code);
    }

    [Fact]
    public async Task Handshake_AccessAdminDeclaration_DoesNotElevate()
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "restricted-agent",
            CancellationToken.None,
            permissions: ["access.admin"]);

        var envelope = await client.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"req-{Guid.NewGuid():N}",
                OperationId = "roots.add",
                Parameters = JsonSerializer.SerializeToElement(new { root = @"D:\not-a-real-root" }),
            },
            CancellationToken.None);

        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, envelope.Error!.Code);
    }

    [Fact]
    public async Task RootsAdd_PersistsRowForRestartRecovery()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"epoch-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var added = await InvokeAsync("roots.add", new { root });
            Assert.True(added.Ok, added.Error?.Message);
            Assert.Equal(1, added.Data.GetProperty("revision").GetInt32());

            var persisted = _fixture.State.Library.Store!.ReadRoots();
            Assert.Contains(persisted, r => r.PhysicalPath == Path.GetFullPath(root));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task RootsRemove_DeletesPersistedRow()
    {
        var root = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"epoch-remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var added = await InvokeAsync("roots.add", new { root });
            Assert.True(added.Ok, added.Error?.Message);
            var rootId = added.Data.GetProperty("rootId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(rootId));

            var removed = await InvokeAsync("roots.remove", new
            {
                rootId,
                expectedRevision = added.Data.GetProperty("revision").GetInt32(),
                idempotencyKey = $"remove-{Guid.NewGuid():N}",
            });

            Assert.True(removed.Ok, removed.Error?.Message);
            Assert.DoesNotContain(_fixture.State.Library.Store!.ReadRoots(), r => r.RootId == rootId);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task SchemaGet_ReturnsRealInputSchema()
    {
        var envelope = await InvokeAsync("schema.get", new { operationId = "games.list" });
        Assert.True(envelope.Ok, envelope.Error?.Message);
        var schema = envelope.Data.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Contains("properties", schema.EnumerateObject().Select(p => p.Name));
        Assert.True(schema.GetProperty("properties").TryGetProperty("limit", out _));
        var output = envelope.Data.GetProperty("outputSchema");
        Assert.Equal("object", output.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SettingsUpdate_WithInvalidIdempotencyKey_IsRejected()
    {
        // 201 字符超长键（路径分隔符合法——控制区文件名已哈希化；控制字符/超长仍拒绝）。
        var tooLongKey = new string('k', 201);
        var envelope = await InvokeAsync("settings.update", new
        {
            idempotencyKey = tooLongKey,
            expectedRevision = 0,
            theme = "dark",
        });
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, envelope.Error!.Code);
    }
}
