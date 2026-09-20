using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// IPC 参数解析与标准错误信封的单源助手：dispatcher 与各域 handler 共用，
/// 避免各 handler 复制解析逻辑造成双源漂移（空字符串视为缺失等语义以此为准）。
/// </summary>
internal static class IpcRequests
{
    /// <summary>取字符串参数：参数为非空字符串时返回 true；空字符串视为缺失。</summary>
    public static bool TryGetStringParameter(IpcRequest request, string name, out string value)
    {
        value = "";
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return value.Length > 0;
        }

        return false;
    }

    /// <summary>取字符串列表参数：参数为数组且全部元素为字符串时返回 true；任一非字符串元素整体失败。</summary>
    public static bool TryGetStringListParameter(IpcRequest request, string name, out IReadOnlyList<string> values)
    {
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    values = [];
                    return false;
                }

                list.Add(item.GetString() ?? "");
            }

            values = list;
            return true;
        }

        values = [];
        return false;
    }

    /// <summary>取布尔参数：仅接受 JSON true/false 字面量（数字/字符串形式不识别）。</summary>
    public static bool TryGetBoolParameter(IpcRequest request, string name, out bool? value)
    {
        value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (request.Parameters is { ValueKind: JsonValueKind.Object } falseParameters
            && falseParameters.TryGetProperty(name, out var falseElement)
            && falseElement.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    /// <summary>取整数参数：参数为数字且可解析为 Int32 时返回 true。</summary>
    public static bool TryGetIntParameter(IpcRequest request, string name, out int? value)
    {
        value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>NotFound 错误信封（错误码与文案由调用方给定，形状不可变更）。</summary>
    public static Envelope<object> NotFound(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.NotFound,
                Message = message,
                Retryable = false,
            },
        };

    /// <summary>InvalidArgument 错误信封（错误码与文案由调用方给定，形状不可变更）。</summary>
    public static Envelope<object> InvalidArgument(IpcRequest request, string message) =>
        new()
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.InvalidArgument,
                Message = message,
                Retryable = false,
            },
        };

    /// <summary>
    /// 路径包含校验（CWE-22 边界）：调用方路径必须在已注册库根内。
    /// 原先散布在 OperationDispatcher / GamesHandler / LaunchingHandler /
    /// ObservabilityHandler 的四份同构副本收敛于此（收尾债），错误码与文案逐字节不变。
    /// </summary>
    public static Envelope<object>? RejectPathOutsideRoots(IpcRequest request, string physicalPath, RootRegistry roots)
    {
        if (roots.Contains(physicalPath))
        {
            return null;
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.PermissionDenied,
                Message = $"路径不在已注册库根内（先通过 roots.add 注册）：{physicalPath}",
                Retryable = false,
            },
        };
    }
}
