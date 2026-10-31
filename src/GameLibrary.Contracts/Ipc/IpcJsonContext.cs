using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLibrary.Contracts.Ipc;

/// <summary>Known client wire types, with the same naming, null and enum rules as ContractJson.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(HandshakeRequest))]
[JsonSerializable(typeof(HandshakeResponse))]
[JsonSerializable(typeof(Envelope<JsonElement>), TypeInfoPropertyName = "JsonEnvelope")]
public partial class IpcJsonContext : JsonSerializerContext
{
    public static IpcJsonContext Wire { get; } = new(CreateWireOptions());

    public static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<OperationStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
