using System.Text.Json.Serialization;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.TauriBridge;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(BridgeFrameRequest))]
[JsonSerializable(typeof(BridgeFrameResponse))]
[JsonSerializable(typeof(HostStopParameters))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
    public static BridgeJsonContext Wire { get; } = new(IpcJsonContext.CreateWireOptions());
}

internal sealed record HostStopParameters(string IdempotencyKey);
