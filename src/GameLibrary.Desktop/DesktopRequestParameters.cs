using System.Text.Json;
using System.Text.Json.Nodes;
using GameLibrary.Contracts;

namespace GameLibrary.Desktop;

internal static class DesktopRequestParameters
{
    public static JsonElement Prepare(string operationId, object? parameters)
    {
        var node = JsonSerializer.SerializeToNode(parameters ?? new { });
        if (node is not JsonObject parameterObject)
        {
            throw new InvalidOperationException("Desktop 请求参数必须是 JSON 对象");
        }

        var operation = OperationCatalog.Catalog.Find(operationId);
        if (operation?.RequiresIdempotencyKey == true && !HasUsableIdempotencyKey(parameterObject))
        {
            parameterObject["idempotencyKey"] = $"desktop-{operationId}-{Guid.NewGuid():N}";
        }

        return JsonSerializer.SerializeToElement(parameterObject);
    }

    private static bool HasUsableIdempotencyKey(JsonObject parameters)
    {
        if (!parameters.TryGetPropertyValue("idempotencyKey", out var node) || node is null)
        {
            return false;
        }

        return node is JsonValue value
            && value.TryGetValue<string>(out var key)
            && !string.IsNullOrWhiteSpace(key);
    }
}
