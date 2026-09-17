using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Classification;
using GameLibrary.Domain.Detection;
using GameLibrary.Domain.Detection.Detectors;
using GameLibrary.Host.Observability;
using GameLibrary.Host.Scanning;
using GameLibrary.Host.Tools;
using GameLibrary.Infrastructure.Backups;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
namespace GameLibrary.Host.Hosting;



/// <summary>OperationDispatcher 的 Tags 域 handler（阶段三按域拆分，partial）。</summary>
public sealed partial class OperationDispatcher
{
    // T-collections tags.×8（阶段三）：标签按 (类型, 规范值) 唯一——engine 标签由引擎识别
    // 在入库时自动创建，重扫只替换同源记录；user 标签由用户维护。删除/解除 engine 标签
    // 登记 Suppress 覆盖（阻止扫描恢复）；tags.reset 清除覆盖并按当前引擎恢复。

    private static object TagDto(PersistedTag tag) => new
    {
        tagId = tag.TagId,
        kind = tag.Kind,
        name = tag.Name,
        color = tag.Color,
        gameCount = tag.GameCount,
        revision = tag.Revision,
        createdUtc = tag.CreatedUtc.ToString("O"),
        updatedUtc = tag.UpdatedUtc.ToString("O"),
    };

    private Envelope<object> TagsList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var tags = store.ListTags();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = tags.Count, items = tags.Select(TagDto).ToArray() },
        };
    }

    private Envelope<object> TagsCreate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "name", out var name))
        {
            return InvalidArgument(request, "缺少 name 参数");
        }

        name = name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
        {
            return InvalidArgument(request, "标签名必须是 1–100 字符且不含控制字符");
        }

        string? color = null;
        if (TryGetStringParameter(request, "color", out var colorValue))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(colorValue, "^#[0-9a-fA-F]{6}$"))
            {
                return InvalidArgument(request, "color 必须是 #RRGGBB 十六进制格式");
            }

            color = colorValue;
        }

        if (store.TryGetTagByName("user", name) is not null)
        {
            return InvalidArgument(request, $"同名用户标签已存在：{name}");
        }

        var utcNow = DateTime.UtcNow;
        var tag = new PersistedTag($"tag-{Guid.NewGuid():N}", "user", name, color, 1, 0, utcNow, utcNow);
        store.CreateTag(tag);
        _state.Events.Publish("tag.created", $"tag:{tag.TagId}", new { tagId = tag.TagId, name }, utcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TagDto(tag),
        };
    }

    private Envelope<object> TagsUpdate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "tagId", out var tagId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 tagId 或 expectedRevision 参数");
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return NotFound(request, $"标签不存在：{tagId}");
        }

        if (tag.Kind != "user")
        {
            return InvalidArgument(request, "自动标签不可编辑（由引擎识别维护；可删除后 Suppress）");
        }

        string? name = null;
        if (TryGetStringParameter(request, "name", out var nameValue))
        {
            name = nameValue.Trim();
            if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            {
                return InvalidArgument(request, "标签名必须是 1–100 字符且不含控制字符");
            }

            var duplicate = store.TryGetTagByName("user", name);
            if (duplicate is not null && duplicate.TagId != tagId)
            {
                return InvalidArgument(request, $"同名用户标签已存在：{name}");
            }
        }

        string? color = null;
        if (TryGetStringParameter(request, "color", out var colorValue))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(colorValue, "^#[0-9a-fA-F]{6}$"))
            {
                return InvalidArgument(request, "color 必须是 #RRGGBB 十六进制格式");
            }

            color = colorValue;
        }

        var newRevision = store.UpdateTag(tagId, name, color, expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"标签 Revision 不一致：期望 {expectedRevision}，当前 {tag.Revision}",
                    Retryable = false,
                    CurrentRevision = tag.Revision,
                },
            };
        }

        var updated = store.TryGetTag(tagId)!;
        _state.Events.Publish("tag.updated", $"tag:{tagId}", new { tagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TagDto(updated),
        };
    }

    private Envelope<object> TagsRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "tagId", out var tagId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 tagId 或 expectedRevision 参数");
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return NotFound(request, $"标签不存在：{tagId}");
        }

        if (tag.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"标签 Revision 不一致：期望 {expectedRevision}，当前 {tag.Revision}",
                    Retryable = false,
                    CurrentRevision = tag.Revision,
                },
            };
        }

        // 契约 note：remove 必须返回受影响游戏列表；自动标签删除时逐游戏登记 Suppress，
        // 使重扫不会恢复用户删除的自动标签（策划案 TagOverride 语义）。
        var affectedGames = store.RemoveTag(tagId);
        if (tag.Kind == "engine")
        {
            foreach (var gameId in affectedGames)
            {
                store.SetTagOverride(gameId, "engine", tag.Name, "suppress", DateTime.UtcNow);
            }
        }

        _state.Events.Publish("tag.removed", $"tag:{tagId}", new { tagId, affectedCount = affectedGames.Count }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { tagId, affectedGames, suppressedAuto = tag.Kind == "engine" },
        };
    }

    /// <summary>tags.assign/unassign/suppress/reset 公共前置：游戏与标签存在 + 游戏 Revision 乐观校验。</summary>
    private (Envelope<object>? Error, SqliteLibraryStore Store, string GameId, PersistedTag Tag) TagGamePrecondition(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return (InvalidArgument(request, "库未初始化（先 library.init）"), null!, "", null!);
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "tagId", out var tagId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return (InvalidArgument(request, "缺少 gameId、tagId 或 expectedRevision 参数"), store, "", null!);
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return (NotFound(request, $"游戏不存在：{gameId}"), store, gameId, null!);
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return (NotFound(request, $"标签不存在：{tagId}"), store, gameId, null!);
        }

        if (game.Revision != expectedRevision.Value)
        {
            return (new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {game.Revision}",
                    Retryable = false,
                    CurrentRevision = game.Revision,
                },
            }, store, gameId, tag);
        }

        return (null, store, gameId, tag);
    }

    private Envelope<object> TagsAssign(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        var changed = store.AssignTag(gameId, tag.TagId, DateTime.UtcNow);
        _state.Events.Publish("tag.assigned", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, assigned = true, alreadyPresent = !changed },
        };
    }

    private Envelope<object> TagsUnassign(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        var kind = store.UnassignTag(gameId, tag.TagId);
        var suppressedAuto = false;
        if (kind == "engine")
        {
            // 自动标签被用户移除：登记 Suppress，重扫不再恢复（策划案 TagOverride）。
            store.SetTagOverride(gameId, "engine", tag.Name, "suppress", DateTime.UtcNow);
            suppressedAuto = true;
        }

        _state.Events.Publish("tag.unassigned", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, assigned = false, suppressedAuto },
        };
    }

    private Envelope<object> TagsSuppress(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        store.SetTagOverride(gameId, tag.Kind, tag.Name, "suppress", DateTime.UtcNow);
        _state.Events.Publish("tag.suppressed", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, suppressed = true },
        };
    }

    private Envelope<object> TagsReset(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        if (tag.Kind != "engine")
        {
            return InvalidArgument(request, "tags.reset 仅对自动标签有意义（清除 Suppress 并按当前引擎恢复）");
        }

        var cleared = store.ClearTagOverride(gameId, tag.Kind, tag.Name, DateTime.UtcNow);
        var restored = store.EnsureEngineTagAssigned(gameId, tag.Name, DateTime.UtcNow);
        _state.Events.Publish("tag.reset", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, clearedOverride = cleared, restored },
        };
    }
}
