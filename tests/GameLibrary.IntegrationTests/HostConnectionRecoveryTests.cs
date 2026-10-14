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
    public async Task IncompleteExchange_DropsConnectionAndExplicitReconnectReadsItsOwnResponse(string failure)
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InvokeAsync(Request("blocked"), timeout.Token));
        await using var nextServer = CreateServer(directory);
        var nextAccept = HandshakeAsync(nextServer, timeout.Token);
        await client.ReconnectAsync(directory, "test", timeout.Token);
        await nextAccept;
        var second = client.InvokeAsync(Request("b"), timeout.Token);
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

    private static NamedPipeServerStream CreateServer(string directory) => new(
        ChannelNames.PipeName(DataDirectory.Resolve(directory).ComparisonKey!), PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task HandshakeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        await server.WaitForConnectionAsync(token);
        await IpcFrame.ReadJsonAsync<HandshakeRequest>(server, token);
        await IpcFrame.WriteJsonAsync(server, new HandshakeResponse { HostInstanceId = "test", LibraryInstanceId = "library", DataEpoch = "epoch" }, token);
    }

    private static IpcRequest Request(string id) => new() { RequestId = id, OperationId = "host.status" };
    private static Task RespondAsync(Stream stream, string id, CancellationToken token) => IpcFrame.WriteJsonAsync(stream,
        new Envelope<JsonElement> { RequestId = id, Ok = true, Status = OperationStatus.Completed, Data = JsonSerializer.SerializeToElement(new { }) }, token);
}
