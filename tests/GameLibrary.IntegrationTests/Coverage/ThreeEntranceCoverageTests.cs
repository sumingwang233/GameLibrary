using System.Reflection;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;
using HostConnection = GameLibrary.HostClient.HostConnection;

namespace GameLibrary.IntegrationTests.Coverage;

/// <summary>
/// 三入口覆盖门禁（T28 / AI-12）：每个"已实现"的 catalog 操作必须同时具备
/// dispatcher handler、CLI 命令映射与 MCP 工具。缺任一入口即失败，不允许发布。
/// </summary>
public sealed class ThreeEntranceCoverageTests : IClassFixture<PipeServerFixture>
{
    private readonly PipeServerFixture _fixture;

    public ThreeEntranceCoverageTests(PipeServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static IReadOnlyList<GameLibrary.Contracts.OperationInfo> AvailableOperations() =>
        OperationCatalog.Catalog.AvailableOperations;

    [Fact]
    public void AvailableOperations_HaveMcpTools()
    {
        var toolNames = typeof(GameLibrary.Mcp.GameLibraryTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        var missing = AvailableOperations()
            .Where(op => !toolNames.Contains(op.McpTool))
            .Select(op => $"{op.OperationId} → mcp:{op.McpTool}")
            .ToList();

        Assert.True(missing.Count == 0, $"缺 MCP 工具的已实现操作：{string.Join("; ", missing)}");
    }

    [Fact]
    public void AvailableOperations_HaveCliCommandMappings()
    {
        var missing = new List<string>();
        foreach (var op in AvailableOperations())
        {
            var cli = op.Cli.ToArray();
            var parse = GameLibrary.Cli.CommandLine.Parse(new[] { cli[0], cli[1], "--format", "json" });
            if (!parse.IsValid || parse.OperationId != op.OperationId)
            {
                missing.Add($"{op.OperationId} → cli:{string.Join(' ', cli)}");
            }
        }

        Assert.True(missing.Count == 0, $"缺 CLI 映射的已实现操作：{string.Join("; ", missing)}");
    }

    /// <summary>
    /// 空参数探针：除 host.stop（会停掉夹具宿主）外，每个已实现操作用空参数调用，
    /// 结果不得是 UnsupportedOperation——证明 dispatcher 真有 handler（参数错误也算覆盖）。
    /// </summary>
    [Fact]
    public async Task AvailableOperations_DispatchWithoutUnsupportedOperation()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "host.stop" };
        var unsupported = new List<string>();
        var mutatedWrongly = new List<string>();

        foreach (var op in AvailableOperations())
        {
            if (excluded.Contains(op.OperationId))
            {
                continue;
            }

            await using var client = await HostConnection.ConnectAsync(
                @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
                clientName: "coverage-probe",
                CancellationToken.None);
            var envelope = await client.InvokeAsync(
                new IpcRequest
                {
                    RequestId = $"probe-{op.OperationId}",
                    OperationId = op.OperationId,
                    Parameters = JsonDocument.Parse("{}").RootElement.Clone(),
                },
                CancellationToken.None);

            if (envelope.Error?.Code == ErrorCodes.UnsupportedOperation)
            {
                unsupported.Add(op.OperationId);
                continue;
            }

            // 有参数校验的写操作应当拒绝空参数；若空参数竟成功了，记录下来供人工判断是否误触发。
            if (envelope.Ok && op.RequiresIdempotencyKey)
            {
                mutatedWrongly.Add(op.OperationId);
            }
        }

        Assert.True(unsupported.Count == 0, $"无 handler 的已实现操作：{string.Join(", ", unsupported)}");
        Assert.True(mutatedWrongly.Count == 0, $"空参数不应成功的写操作：{string.Join(", ", mutatedWrongly)}");
    }

    [Fact]
    public async Task DiagnosticsStatus_ReportsOperationCatalogTotals()
    {
        await using var client = await HostConnection.ConnectAsync(
            @$"D:\Official\GameLibrary\artifacts\test-runs\{_fixture.TestId}\data",
            clientName: "coverage-probe",
            CancellationToken.None);
        var capabilities = await client.InvokeAsync(
            new IpcRequest { RequestId = "cov-cap", OperationId = "capabilities.get" },
            CancellationToken.None);

        Assert.True(capabilities.Ok);
        var available = capabilities.Data.GetProperty("availableOperations").EnumerateArray().Count();
        Assert.Equal(AvailableOperations().Count, available);
    }
}
