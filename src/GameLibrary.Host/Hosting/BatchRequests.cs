using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 批量写操作的公共解析与回执（optimize-v1.7.23）：games.update_batch /
/// games.remove_batch / tags.assign_batch 共用「items 形状校验 + 逐项回执」，
/// 避免三处各写一份。分工是固定的——整批参数非法（items 缺失/为空/超限/重复/
/// 单条缺字段）返回 Envelope 级 InvalidArgument；逐项业务失败（游戏不存在、
/// Revision 冲突、已移除、评分越界）返回该项 ok=false + errorCode，Envelope 仍为
/// ok=true，前端据 items 合并失败列表。
/// </summary>
internal static class BatchRequests
{
    /// <summary>单批上限：与 candidates.review_batch 一致，防单请求长时间持有写锁。</summary>
    public const int MaxItems = 1000;

    /// <summary>读 items 参数：必须是 1–MaxItems 个对象组成的数组。</summary>
    public static bool TryReadItems(IpcRequest request, out IReadOnlyList<JsonElement> items, out string? error)
    {
        items = [];
        error = null;
        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters
            || !parameters.TryGetProperty("items", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            error = $"缺少 items 参数（1–{MaxItems} 项的数组）";
            return false;
        }

        var count = array.GetArrayLength();
        if (count is < 1 or > MaxItems)
        {
            error = $"items 需要 1–{MaxItems} 项，实际 {count} 项";
            return false;
        }

        var list = new List<JsonElement>(count);
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "items 的每一项必须是对象";
                return false;
            }

            list.Add(element);
        }

        items = list;
        return true;
    }

    /// <summary>读每项公共定位字段：gameId 非空字符串、expectedRevision 为 ≥1 的整数。</summary>
    public static bool TryReadIdentity(JsonElement item, out string gameId, out int expectedRevision)
    {
        gameId = "";
        expectedRevision = 0;
        if (!item.TryGetProperty("gameId", out var id) || id.ValueKind != JsonValueKind.String) return false;
        var value = id.GetString();
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!item.TryGetProperty("expectedRevision", out var revision)
            || revision.ValueKind != JsonValueKind.Number
            || !revision.TryGetInt32(out expectedRevision)
            || expectedRevision < 1)
        {
            return false;
        }

        gameId = value;
        return true;
    }

    /// <summary>读可选布尔字段：缺省与 null 都视为未提供，非布尔即非法。</summary>
    public static bool TryReadOptionalBool(JsonElement item, string name, out bool? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    /// <summary>读可选整数字段：缺省与 null 都视为未提供，非整数即非法（范围由 Store 按单条语义校验）。</summary>
    public static bool TryReadOptionalInt(JsonElement item, string name, out int? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed)) return false;
        value = parsed;
        return true;
    }

    /// <summary>单项失败回执（不抛异常，批量事务继续下一项）。</summary>
    public static SqliteLibraryStore.BatchWriteOutcome Failure(string gameId, SqliteLibraryStore.BatchFailureKind kind, string message)
        => new(gameId, false, Failure: kind, Message: message);

    /// <summary>持久层的失败类别 → 线上错误码（Infrastructure 不依赖 Contracts，映射放在宿主）。</summary>
    private static string? CodeOf(SqliteLibraryStore.BatchFailureKind kind) => kind switch
    {
        SqliteLibraryStore.BatchFailureKind.None => null,
        SqliteLibraryStore.BatchFailureKind.InvalidArgument => ErrorCodes.InvalidArgument,
        SqliteLibraryStore.BatchFailureKind.NotFound => ErrorCodes.NotFound,
        SqliteLibraryStore.BatchFailureKind.RevisionConflict => ErrorCodes.RevisionConflict,
        _ => ErrorCodes.InternalError,
    };

    /// <summary>整批成功信封：items 与输入同序，updated/failed 为计数（ok 恒为 true）。</summary>
    public static Envelope<object> Respond(IpcRequest request, IReadOnlyList<SqliteLibraryStore.BatchWriteOutcome> items)
    {
        var results = new BatchItemResult[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            results[index] = new BatchItemResult(item.GameId, item.Ok, item.Revision, CodeOf(item.Failure), item.Message);
        }

        var updated = 0;
        foreach (var result in results)
        {
            if (result.Ok) updated++;
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                items = results,
                updated,
                failed = results.Length - updated,
            },
        };
    }

    /// <summary>逐项回执形状（冻结契约）：成功项只有 gameId/ok/revision，失败项带 errorCode/message。</summary>
    private sealed record BatchItemResult(string GameId, bool Ok, int? Revision, string? ErrorCode, string? Message);
}
