using System.Text.Json;

namespace GameLibrary.TauriBridge;

public sealed record BridgeRequest
{
    public string? RequestId { get; init; }

    public required string OperationId { get; init; }

    public JsonElement? Parameters { get; init; }

    public string? DataDir { get; init; }
}
