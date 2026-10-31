using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using Xunit;

namespace GameLibrary.IntegrationTests;

public sealed class IpcJsonSerializationTests
{
    [Theory]
    [InlineData(OperationStatus.Completed, "completed")]
    [InlineData(OperationStatus.Accepted, "accepted")]
    [InlineData(OperationStatus.Partial, "partial")]
    [InlineData(OperationStatus.NeedsUserAction, "needsUserAction")]
    [InlineData(OperationStatus.UnknownOutcome, "unknownOutcome")]
    [InlineData(OperationStatus.Failed, "failed")]
    public async Task GeneratedClientJson_PreservesContractAndFrameRoundTrip(OperationStatus status, string wireStatus)
    {
        using var data = JsonDocument.Parse("{\"路径\":\"D:/游戏库\"}");
        var envelope = new Envelope<JsonElement>
        {
            RequestId = "request-中文",
            LibraryInstanceId = "library",
            DataEpoch = "epoch",
            Ok = true,
            Status = status,
            Data = data.RootElement.Clone(),
        };
        var generated = JsonSerializer.Serialize(envelope, IpcJsonContext.Wire.JsonEnvelope);
        using var document = JsonDocument.Parse(generated);
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(envelope, ContractJson.Options));
        Assert.True(JsonElement.DeepEquals(legacy.RootElement, document.RootElement));
        Assert.Equal(wireStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("jobId").ValueKind);

        using var stream = new MemoryStream();
        await IpcFrame.WriteJsonAsync(stream, envelope, IpcJsonContext.Wire.JsonEnvelope, CancellationToken.None);
        stream.Position = 0;
        var restored = await IpcFrame.ReadJsonAsync(stream, IpcJsonContext.Wire.JsonEnvelope, CancellationToken.None);
        Assert.Equal(envelope.RequestId, restored.RequestId);
        Assert.Equal(status, restored.Status);
        Assert.Equal("D:/游戏库", restored.Data.GetProperty("路径").GetString());
    }

    [Fact]
    public void GeneratedHandshakeAndRequest_PreservePermissionsIdentityAndNulls()
    {
        var request = new IpcRequest
        {
            RequestId = "same-id",
            OperationId = "host.status",
            LibraryInstanceId = "library",
            ExpectedDataEpoch = "epoch"
        };
        using var generated = JsonDocument.Parse(JsonSerializer.Serialize(request, IpcJsonContext.Wire.IpcRequest));
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(request, ContractJson.Options));
        Assert.True(JsonElement.DeepEquals(legacy.RootElement, generated.RootElement));
        var handshake = new HandshakeRequest { ClientName = "tauri", Permissions = ["games.read"] };
        using var generatedHandshake = JsonDocument.Parse(JsonSerializer.Serialize(handshake, IpcJsonContext.Wire.HandshakeRequest));
        using var legacyHandshake = JsonDocument.Parse(JsonSerializer.Serialize(handshake, ContractJson.Options));
        Assert.True(JsonElement.DeepEquals(legacyHandshake.RootElement, generatedHandshake.RootElement));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"status\":1}", IpcJsonContext.Wire.JsonEnvelope));
    }
}
