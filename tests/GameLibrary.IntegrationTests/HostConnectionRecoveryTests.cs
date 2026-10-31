using System.IO.Pipes;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.IntegrationTests;

public sealed class HostConnectionRecoveryTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("half-frame")]
    [InlineData("wrong-id")]
    public async Task IncompleteExchange_DropsConnectionAndNextInvokeReconnectsAutomatically(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-pipe-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = CreateServer(directory);
        var accept = HandshakeAsync(server, timeout.Token);
        await using var client = await HostConnection.ConnectAsync(directory, "test", timeout.Token);
        await accept;
        using var cancel = new CancellationTokenSource();
        var first = client.InvokeAsync(Request("a"), cancel.Token);
        Assert.Equal("a", (await IpcFrame.ReadJsonAsync<IpcRequest>(server, timeout.Token)).RequestId);
        if (failure == "wrong-id")
        {
            await RespondAsync(server, "unrelated", timeout.Token);
            var error = await Assert.ThrowsAsync<HostClientException>(() => first);
            Assert.Equal(HostClientErrorCodes.ProtocolError, error.Code);
        }
        else
        {
            if (failure == "half-frame")
            {
                await server.WriteAsync(new byte[] { 0, 0, 0, 10, (byte)'{' }, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        // 契约：连接失效后，下一次 InvokeAsync 必须自动重连并重新握手，
        // 不得再把 InvalidOperationException("尚未完成握手") 暴露给调用方。
        await using var nextServer = CreateServer(directory);
        var nextAccept = HandshakeAsync(nextServer, timeout.Token);
        var second = client.InvokeAsync(Request("b"), timeout.Token);
        await nextAccept;
        Assert.Equal("b", (await IpcFrame.ReadJsonAsync<IpcRequest>(nextServer, timeout.Token)).RequestId);
        await RespondAsync(nextServer, "b", timeout.Token);
        Assert.Equal("b", (await second).RequestId);
    }

    [Fact]
    public async Task CancelWhileWaitingForSendLock_DoesNotInvalidateActiveConnection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-pipe-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = CreateServer(directory);
        var accept = HandshakeAsync(server, timeout.Token);
        await using var client = await HostConnection.ConnectAsync(directory, "test", timeout.Token);
        await accept;
        var first = client.InvokeAsync(Request("a"), timeout.Token);
        await IpcFrame.ReadJsonAsync<IpcRequest>(server, timeout.Token);
        using var cancel = new CancellationTokenSource();
        var waiting = client.InvokeAsync(Request("cancelled"), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await RespondAsync(server, "a", timeout.Token);
        Assert.True((await first).Ok);
        var second = client.InvokeAsync(Request("b"), timeout.Token);
        Assert.Equal("b", (await IpcFrame.ReadJsonAsync<IpcRequest>(server, timeout.Token)).RequestId);
        await RespondAsync(server, "b", timeout.Token);
        Assert.True((await second).Ok);
    }

    [Fact]
    public async Task InvokeAfterDropWithoutHost_SurfacesHostUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-pipe-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = CreateServer(directory);
        var accept = HandshakeAsync(server, timeout.Token);
        await using var client = await HostConnection.ConnectAsync(directory, "test", timeout.Token);
        await accept;
        // 宿主消失且无人再监听：自动重连会在 ConnectAsync 的 5s 超时后失败，
        // 契约要求失败以 HostClientException(HostUnavailable) 暴露，供桥接层丢弃缓存并重建。
        await server.DisposeAsync();
        var error = await Assert.ThrowsAsync<HostClientException>(() => client.InvokeAsync(Request("a"), timeout.Token));
        Assert.Equal(HostClientErrorCodes.HostUnavailable, error.Code);
    }

    [Fact]
    public async Task BootstrapWrite_AfterLibraryAppears_ReturnsSerializableIdentityFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLibrary-bootstrap-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var firstServer = CreateServer(directory);
        var initialHandshake = HandshakeAsync(firstServer, timeout.Token, null);
        await using var client = await HostConnection.ConnectAsync(directory, "test", timeout.Token);
        await initialHandshake;
        var request = new IpcRequest { RequestId = "bootstrap-write", OperationId = "settings.update" };
        var pending = client.InvokeAsync(request, timeout.Token);
        await IpcFrame.ReadJsonAsync<IpcRequest>(firstServer, timeout.Token);
        await firstServer.DisposeAsync();

        await using var replacement = CreateServer(directory);
        await HandshakeAsync(replacement, timeout.Token, "new-library");
        var result = await pending;
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.LibraryInstanceMismatch, result.Error?.Code);
        Assert.Equal(JsonValueKind.Null, result.Data.ValueKind);
        Assert.Null(request.LibraryInstanceId);
        Assert.Null(request.ExpectedDataEpoch);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(result, IpcJsonContext.Wire.JsonEnvelope));
        Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("data").ValueKind);
        Assert.Equal("bootstrap-write", wire.RootElement.GetProperty("requestId").GetString());
    }

    private static NamedPipeServerStream CreateServer(string directory) => new(
        ChannelNames.PipeName(DataDirectory.Resolve(directory).ComparisonKey!), PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task HandshakeAsync(NamedPipeServerStream server, CancellationToken token, string? libraryInstanceId = "library")
    {
        await server.WaitForConnectionAsync(token);
        await IpcFrame.ReadJsonAsync<HandshakeRequest>(server, token);
        await IpcFrame.WriteJsonAsync(server, new HandshakeResponse { HostInstanceId = "test", LibraryInstanceId = libraryInstanceId,
            DataEpoch = libraryInstanceId is null ? null : "epoch" }, token);
    }

    private static IpcRequest Request(string id) => new() { RequestId = id, OperationId = "host.status" };
    private static Task RespondAsync(Stream stream, string id, CancellationToken token) => IpcFrame.WriteJsonAsync(stream,
        new Envelope<JsonElement> { RequestId = id, Ok = true, Status = OperationStatus.Completed, Data = JsonSerializer.SerializeToElement(new { }) }, token);
}
