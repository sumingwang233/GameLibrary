using System.Text.Json;
using System.Text.Json.Serialization;
using GameLibrary.Contracts;

namespace GameLibrary.TauriBridge;

/// <summary>
/// 私有 stdio 帧。transportId 只用于桥内请求/响应配对，独立于 Host 的 requestId，
/// 因此乱序响应不会覆盖或改写原有业务 ID。
/// </summary>
public sealed record BridgeFrameRequest
{
    public required string TransportId { get; init; }

    public required BridgeRequest Request { get; init; }
}

public sealed record BridgeFrameResponse
{
    public required string TransportId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Envelope<JsonElement>? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeTransportError? TransportError { get; init; }
}

public sealed record BridgeTransportError
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}
