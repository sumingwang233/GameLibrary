using System.Reflection;
using System.Text.Json;

namespace GameLibrary.Contracts;

public sealed record OperationInfo(
    string OperationId,
    IReadOnlyList<string> Cli,
    string McpTool,
    string Handler,
    string Permission,
    bool RequiresRevision,
    bool RequiresIdempotencyKey,
    string Execution,
    string? Note)
{
    public bool IsAvailable => OperationCatalog.ImplementedOperations.Contains(OperationId);
}

public sealed class OperationCatalogData
{
    public required string CatalogVersion { get; init; }

    public required string ApiVersion { get; init; }

    public required IReadOnlyList<string> Permissions { get; init; }

    public required IReadOnlyList<OperationInfo> Operations { get; init; }

    public OperationInfo? Find(string operationId) =>
        Operations.FirstOrDefault(op => string.Equals(op.OperationId, operationId, StringComparison.Ordinal));

    public IReadOnlyList<OperationInfo> AvailableOperations =>
        Operations.Where(op => op.IsAvailable).ToArray();
}

/// <summary>
/// 操作目录的代码侧入口：注册表来自嵌入的 contracts/operations.v1.json（单一来源），
/// 可用性由 <see cref="ImplementedOperations"/> 声明——实现一个 handler 才加一个 ID（实现即事实）。
/// capabilities/schema/host.status 均可离线由本类回答；运行能力仍以宿主返回为准。
/// </summary>
public static class OperationCatalog
{
    /// <summary>已实现的操作（与 handler/CLI/MCP 三入口映射同一提交内更新）。</summary>
    public static readonly IReadOnlySet<string> ImplementedOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "capabilities.get",
        "schema.get",
        "host.status",
    };

    private static readonly Lazy<OperationCatalogData> Data = new(Load);

    public static OperationCatalogData Catalog => Data.Value;

    private static OperationCatalogData Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("operations.v1.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("未找到嵌入的操作目录资源");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取嵌入的操作目录资源");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var operations = root.GetProperty("operations")
            .EnumerateArray()
            .Select(op => new OperationInfo(
                op.GetProperty("operationId").GetString()!,
                op.GetProperty("cli").EnumerateArray().Select(v => v.GetString()!).ToArray(),
                op.GetProperty("mcpTool").GetString()!,
                op.GetProperty("handler").GetString()!,
                op.GetProperty("permission").GetString()!,
                op.GetProperty("requiresRevision").GetBoolean(),
                op.GetProperty("requiresIdempotencyKey").GetBoolean(),
                op.GetProperty("execution").GetString()!,
                op.TryGetProperty("note", out var note) ? note.GetString() : null))
            .ToArray();

        return new OperationCatalogData
        {
            CatalogVersion = root.GetProperty("catalogVersion").GetString()!,
            ApiVersion = root.GetProperty("apiVersion").GetString()!,
            Permissions = root.GetProperty("permissions").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            Operations = operations,
        };
    }
}
