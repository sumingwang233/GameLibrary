using GameLibrary.Contracts.Ipc;
using Xunit;

namespace GameLibrary.ContractTests;

public sealed class IpcFrameTests
{
    [Fact]
    public async Task WriteRead_RoundTripsPayload()
    {
        using var stream = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 0xDE, 0xAD };

        await IpcFrame.WriteAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;
        var read = await IpcFrame.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Read_RejectsOversizedFrames()
    {
        using var stream = new MemoryStream();
        var bigPayload = new byte[IpcFrame.MaxMessageBytes + 1];
        bigPayload.AsSpan().Fill(0xAB);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => IpcFrame.WriteAsync(stream, bigPayload, CancellationToken.None));
    }

    [Fact]
    public async Task Read_RejectsIllegalLengthPrefix()
    {
        var stream = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF, 0x00]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => IpcFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Read_TruncatedPayload_ThrowsEndOfStream()
    {
        var stream = new MemoryStream([0x00, 0x00, 0x00, 0x10, 0x01, 0x02]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => IpcFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteReadJson_RoundTripsTypedMessages()
    {
        using var stream = new MemoryStream();
        var request = new IpcRequest { RequestId = "req-1", OperationId = "host.status" };

        await IpcFrame.WriteJsonAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var read = await IpcFrame.ReadJsonAsync<IpcRequest>(stream, CancellationToken.None);

        Assert.Equal("req-1", read.RequestId);
        Assert.Equal("host.status", read.OperationId);
    }
}
