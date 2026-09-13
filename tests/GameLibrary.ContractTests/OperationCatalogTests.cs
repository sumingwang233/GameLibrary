using System.Text.Json;
using GameLibrary.Contracts;
using Xunit;

namespace GameLibrary.ContractTests;

/// <summary>
/// 操作目录（contracts/operations.v1.json）的结构与命名门禁。
/// 这是 AI-12 覆盖检查的机器基础：catalog 本身必须自洽。
/// </summary>
public sealed class OperationCatalogTests
{
    private static readonly Lazy<JsonDocument> Catalog = new(LoadCatalog);

    private static JsonDocument LoadCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "operations.v1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static IEnumerable<(string OperationId, JsonElement Op)> Operations()
    {
        foreach (var op in Catalog.Value.RootElement.GetProperty("operations").EnumerateArray())
        {
            yield return (op.GetProperty("operationId").GetString()!, op);
        }
    }

    /// <summary>契约第 3.1 节必须覆盖的操作矩阵；缺项即门禁失败。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredMatrix =
        new Dictionary<string, string[]>
        {
            ["capabilities"] = ["get"],
            ["schema"] = ["get"],
            ["host"] = ["status", "start", "stop"],
            ["library"] = ["init", "status", "export", "import_plan", "import"],
            ["roots"] = ["list", "get", "add", "update", "remove", "rebind"],
            ["rules"] = ["list", "get", "create", "update", "remove", "preview"],
            ["scan"] = ["start", "status", "pause", "resume", "cancel", "coverage", "inspect"],
            ["candidates"] = ["list", "get", "accept", "defer", "ignore"],
            ["ignores"] = ["list", "get", "create", "update", "remove"],
            ["games"] = ["list", "get", "create", "update", "remove", "relink"],
            ["fields"] = ["set", "clear", "reset"],
            ["tags"] = ["list", "create", "update", "remove", "assign", "unassign", "suppress", "reset"],
            ["metadata"] = ["preview", "refresh"],
            ["assets"] = ["list", "get", "import", "choose", "crop", "reset", "remove"],
            ["profiles"] = ["list", "get", "create", "update", "remove", "set_default", "validate"],
            ["translation"] = ["get", "set"],
            ["tools"] = ["list", "get", "discover", "register", "update", "remove", "capabilities"],
            ["verification"] = ["start", "get", "list", "report", "invalidate"],
            ["launch"] = ["plan", "execute", "status", "history"],
            ["jobs"] = ["list", "get", "wait", "cancel"],
            ["events"] = ["read"],
            ["notifications"] = ["list", "get", "acknowledge", "defer"],
            ["settings"] = ["get", "update", "reset"],
            ["views"] = ["list", "get", "create", "update", "remove", "activate"],
            ["desktop"] = ["state", "show", "navigate", "hide", "close"],
            ["backups"] = ["list", "create", "inspect", "restore_plan", "restore"],
            ["diagnostics"] = ["status", "logs", "export", "cache_rebuild"],
            ["access"] = ["status", "list", "configure", "revoke"],
            ["actions"] = ["list", "get", "complete"],
        };

    [Fact]
    public void Catalog_CoversRequiredOperationMatrix()
    {
        var actual = Operations()
            .GroupBy(item => item.OperationId.Split('.')[0], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.OperationId.Split('.')[1]).Order().ToArray(),
                StringComparer.Ordinal);

        foreach (var (ns, verbs) in RequiredMatrix)
        {
            Assert.True(actual.ContainsKey(ns), $"catalog 缺少命名空间 {ns}");
            var missing = verbs.Except(actual[ns]).ToList();
            Assert.True(missing.Count == 0, $"catalog 命名空间 {ns} 缺少操作：{string.Join(", ", missing)}");
        }

        var extraNamespaces = actual.Keys.Except(RequiredMatrix.Keys).ToList();
        Assert.True(extraNamespaces.Count == 0, $"catalog 出现矩阵外命名空间：{string.Join(", ", extraNamespaces)}");
    }

    [Fact]
    public void NamingRules_HoldForEveryOperation()
    {
        foreach (var (operationId, op) in Operations())
        {
            var parts = operationId.Split('.');
            Assert.Equal(2, parts.Length);

            Assert.Equal(operationId.Replace('.', '_'), op.GetProperty("mcpTool").GetString());

            var cli = op.GetProperty("cli");
            Assert.Equal(2, cli.GetArrayLength());
            Assert.Equal(parts[0], cli[0].GetString());
            Assert.Equal(parts[1].Replace('_', '-'), cli[1].GetString());
        }
    }

    [Fact]
    public void AllIdentifiers_AreUnique()
    {
        var ops = Operations().ToList();
        Assert.Equal(ops.Count, ops.Select(item => item.OperationId).Distinct().Count());
        Assert.Equal(ops.Count, ops.Select(item => item.Op.GetProperty("mcpTool").GetString()).Distinct().Count());
        Assert.Equal(ops.Count, ops.Select(item => item.Op.GetProperty("handler").GetString()).Distinct().Count());
        Assert.Equal(
            ops.Count,
            ops.Select(item => string.Join(' ', item.Op.GetProperty("cli").EnumerateArray().Select(v => v.GetString())))
                .Distinct().Count());
    }

    [Fact]
    public void EveryOperation_HasValidPermissionAndExecution()
    {
        var permissions = Catalog.Value.RootElement.GetProperty("permissions")
            .EnumerateArray().Select(v => v.GetString()).ToHashSet();
        var executions = Catalog.Value.RootElement.GetProperty("executionModes")
            .EnumerateArray().Select(v => v.GetString()).ToHashSet();

        foreach (var (_, op) in Operations())
        {
            Assert.Contains(op.GetProperty("permission").GetString(), permissions);
            Assert.Contains(op.GetProperty("execution").GetString(), executions);
            Assert.False(string.IsNullOrWhiteSpace(op.GetProperty("handler").GetString()));
            Assert.True(op.TryGetProperty("requiresRevision", out _));
            Assert.True(op.TryGetProperty("requiresIdempotencyKey", out _));
        }
    }

    [Fact]
    public void EmbeddedCatalog_LoadsAndImplementedSet_IsSubsetOfCatalog()
    {
        var catalog = OperationCatalog.Catalog;

        Assert.Equal(ApiConstants.ApiVersion, catalog.ApiVersion);
        Assert.True(catalog.Operations.Count >= 129);
        Assert.All(catalog.Operations, op => Assert.False(string.IsNullOrWhiteSpace(op.Handler)));

        Assert.NotEmpty(OperationCatalog.ImplementedOperations);
        var ids = catalog.Operations.Select(op => op.OperationId).ToHashSet(StringComparer.Ordinal);
        var unknown = OperationCatalog.ImplementedOperations.Where(id => !ids.Contains(id)).ToList();
        Assert.True(unknown.Count == 0, $"已实现集合包含未登记操作：{string.Join(", ", unknown)}");

        Assert.Contains(catalog.AvailableOperations, op => op.OperationId == "host.status");
    }

    [Fact]
    public void SchemaFiles_AreNotYetClaimedAsImplemented()
    {
        // T06 已交付 launch.*（守卫随之解除）；games.update 仍属 T14+，schema 尚未开始。
        var implemented = OperationCatalog.ImplementedOperations;
        Assert.DoesNotContain("games.update", implemented);
        Assert.Contains("launch.execute", implemented);
    }
}
