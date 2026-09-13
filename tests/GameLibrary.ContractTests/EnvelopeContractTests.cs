using System.Text.Json;
using GameLibrary.Contracts;
using Xunit;

namespace GameLibrary.ContractTests;

/// <summary>
/// 统一结果信封的公开契约（MCP/CLI 契约第 4 节）：
/// 字段名 camelCase、可选字段输出 null、status 为固定英文值。
/// </summary>
public sealed class EnvelopeContractTests
{
    [Fact]
    public void Serialize_UsesContractFieldNamesAndCamelCase()
    {
        var envelope = new Envelope<object>
        {
            RequestId = "req-demo-01",
            LibraryInstanceId = "library-demo",
            DataEpoch = "epoch-demo",
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId = "game-demo", revision = 8 },
        };

        var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var field in new[]
                 {
                     "apiVersion", "requestId", "libraryInstanceId", "dataEpoch",
                     "ok", "status", "data", "jobId", "error", "warnings", "nextActions",
                 })
        {
            Assert.True(root.TryGetProperty(field, out _), $"信封缺少字段 {field}：{json}");
        }

        Assert.Equal(ApiConstants.ApiVersion, root.GetProperty("apiVersion").GetString());
        Assert.Equal("req-demo-01", root.GetProperty("requestId").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("jobId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.Equal("game-demo", root.GetProperty("data").GetProperty("gameId").GetString());
    }

    [Fact]
    public void Serialize_AllStatuses_UseFixedEnglishCamelCaseValues()
    {
        var expected = new Dictionary<OperationStatus, string>
        {
            [OperationStatus.Completed] = "completed",
            [OperationStatus.Accepted] = "accepted",
            [OperationStatus.Partial] = "partial",
            [OperationStatus.NeedsUserAction] = "needsUserAction",
            [OperationStatus.Failed] = "failed",
            [OperationStatus.UnknownOutcome] = "unknownOutcome",
        };

        foreach (var (status, value) in expected)
        {
            var envelope = new Envelope<object> { Status = status };
            var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(value, document.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(expected.Count, Enum.GetValues<OperationStatus>().Length);
    }

    [Fact]
    public void ErrorCodes_ContainAllRequiredContractCodes()
    {
        var unique = new HashSet<string>(ErrorCodes.RequiredCodes, StringComparer.Ordinal);
        Assert.Equal(ErrorCodes.RequiredCodes.Count, unique.Count);
        Assert.Contains(ErrorCodes.RevisionConflict, unique);
        Assert.Contains(ErrorCodes.UnknownOutcome, unique);
        Assert.Contains(ErrorCodes.MaintenanceMode, unique);
    }

    [Fact]
    public void Serialize_Error_IncludesOptionalFieldsAsNull()
    {
        var envelope = new Envelope<object>
        {
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.InvalidArgument,
                Message = "示例错误",
            },
        };

        var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("InvalidArgument", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("retryable").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("fieldErrors").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("currentRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("recoveryOperation").ValueKind);
    }
}
