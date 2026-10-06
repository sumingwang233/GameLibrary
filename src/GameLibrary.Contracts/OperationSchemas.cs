using System.Text.Json;

namespace GameLibrary.Contracts;

/// <summary>参数 schema 描述项。</summary>
public sealed record ParamSpec(string Name, string Type, bool Required, string Description, bool Nullable = false);

/// <summary>
/// 操作输入/输出 schema 注册表（v1 审查修复：schema.get 不再返回 null）。
/// 以紧凑参数表声明每个已实现操作的输入契约，程序化生成 JSON Schema（draft 2020-12 子集）；
/// 未登记的操作回退到通用对象 schema 并注明待补充——这是"机器可发现"的诚实降级，
/// 不再把占位 null 冒充 schema。
/// </summary>
public static class OperationSchemas
{
    private static readonly IReadOnlyDictionary<string, ParamSpec[]> InputSpecs = GeneratedOperations.InputSpecs;

    public static object Describe(OperationInfo info) => new
    {
        operationId = info.OperationId,
        cli = info.Cli,
        mcpTool = info.McpTool,
        handler = info.Handler,
        permission = info.Permission,
        requiresRevision = info.RequiresRevision,
        requiresIdempotencyKey = info.RequiresIdempotencyKey,
        execution = info.Execution,
        available = info.IsAvailable,
        note = info.Note,
        inputSchema = BuildInputSchema(info.OperationId),
        outputSchema = BuildOutputSchema(),
    };

    /// <summary>生成指定操作的 inputSchema；未登记操作返回通用对象 schema（注明待补充）。</summary>
    public static JsonElement BuildInputSchema(string operationId)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        if (InputSpecs.TryGetValue(operationId, out var specs))
        {
            foreach (var spec in specs)
            {
                properties[spec.Name] = new Dictionary<string, object>
                {
                    ["type"] = spec.Nullable ? new[] { MapType(spec.Type), "null" } : (object)MapType(spec.Type),
                    ["description"] = spec.Description,
                };
                if (spec.Required)
                {
                    required.Add(spec.Name);
                }
            }

            return WriteSchema(properties, required, "操作输入参数对象", operationId);
        }

        return WriteSchema([], [], "该操作的输入 schema 尚未结构化登记；当前接受自由键值对");
    }

    /// <summary>输出统一为契约信封形状（data 结构由各操作 note 描述）。</summary>
    public static JsonElement BuildOutputSchema() =>
        WriteSchema(
            new Dictionary<string, object>
            {
                ["apiVersion"] = new Dictionary<string, object> { ["type"] = "string" },
                ["requestId"] = new Dictionary<string, object> { ["type"] = "string" },
                ["libraryInstanceId"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["dataEpoch"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["ok"] = new Dictionary<string, object> { ["type"] = "boolean" },
                ["status"] = new Dictionary<string, object> { ["type"] = "string" },
                ["data"] = new Dictionary<string, object>
                {
                    ["description"] = "操作结果载荷；结构随操作而异",
                },
                ["jobId"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["error"] = new Dictionary<string, object> { ["type"] = new[] { "object", "null" } },
                ["warnings"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new { type = "string" } },
                ["nextActions"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new { type = "object" } },
            },
            ["apiVersion", "requestId", "ok", "status"],
            "统一结果信封（契约第 3 节）");

    private static string MapType(string type) => type switch
    {
        "integer" => "integer",
        "number" => "number",
        "boolean" => "boolean",
        "array" => "array",
        "string" => "string",
        _ => "string",
    };

    private static JsonElement WriteSchema(Dictionary<string, object> properties, List<string> required, string description, string? operationId = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            payload["required"] = required;
        }
        if (operationId == "assets.import")
        {
            foreach (var name in new[] { "sourcePath", "imageBase64", "mimeType" })
                ((Dictionary<string, object>)properties[name])["type"] = new[] { "string", "null" };
            payload["oneOf"] = new[]
            {
                new { required = new[] { "sourcePath" }, properties = new { sourcePath = new { type = "string" }, imageBase64 = new { type = "null" }, mimeType = new { type = "null" } } },
                new { required = new[] { "imageBase64", "mimeType" }, properties = new { sourcePath = new { type = "null" }, imageBase64 = new { type = "string" }, mimeType = new { type = "string" } } },
            };
        }

        return JsonSerializer.SerializeToElement(payload);
    }
}
