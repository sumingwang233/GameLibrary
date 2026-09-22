using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 标签域处理器：tags.list/create/update/remove/assign/unassign/suppress/reset 八操作。
/// 标签按 (类型, 规范值) 唯一——engine 标签由引擎识别在入库时自动创建，重扫只替换
/// 同源记录；user 标签由用户维护。删除/解除 engine 标签登记 Suppress 覆盖（阻止扫描
/// 恢复）；tags.reset 清除覆盖并按当前引擎恢复。Store 经委托每请求取当前值
/// （library.init / backups.restore 会整体替换 Library，禁止构造时缓存 store 引用）；
/// Events 为 init-only 引用（换库由 EventStream.BindStore 在其内部重绑，若未来宿主改为
/// 整体替换 Events 实例，此处与 CandidateReviewHandler 须同步改为委托取值）。由
/// DispatchCore 调用，天然继承幂等收据（tags.* 变更操作在 ReceiptOperations）与
/// 串行门等中间件。
/// </summary>
internal sealed class TagsHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly EventStream _events;

    public TagsHandler(Func<SqliteLibraryStore?> storeAccessor, EventStream events)
    {
        _storeAccessor = storeAccessor;
        _events = events;
    }

    /// <summary>列出全部标签：保持 store.ListTags() 返回顺序，不新增排序。</summary>
    public Envelope<object> TagsList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
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

    /// <summary>创建 user 标签：名称 1–100 字符且不含控制字符，color 须为 #RRGGBB；feat-3 起接受 category/sortOrder/starred/displayName。</summary>
    public Envelope<object> TagsCreate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "name", out var name))
        {
            return IpcRequests.InvalidArgument(request, "缺少 name 参数");
        }

        name = name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
        {
            return IpcRequests.InvalidArgument(request, "标签名必须是 1–100 字符且不含控制字符");
        }

        string? color = null;
        if (IpcRequests.TryGetStringParameter(request, "color", out var colorValue))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(colorValue, "^#[0-9a-fA-F]{6}$"))
            {
                return IpcRequests.InvalidArgument(request, "color 必须是 #RRGGBB 十六进制格式");
            }

            color = colorValue;
        }

        // feat-3：category 缺省按 kind 推断——本操作只建 user 标签（kind 恒 'user'）故缺省
        // 'special'；engine 标签由扫描经 EnsureEngineTagAssigned 创建并固定 'engine'。
        var category = "special";
        if (IpcRequests.TryGetStringParameter(request, "category", out var categoryValue))
        {
            if (categoryValue is not ("engine" or "gameplay" or "social" or "special"))
            {
                return IpcRequests.InvalidArgument(request, "category 只支持 engine/gameplay/social/special");
            }

            category = categoryValue;
        }

        var sortOrder = 0;
        if (IpcRequests.TryGetIntParameter(request, "sortOrder", out var sortOrderValue))
        {
            sortOrder = sortOrderValue!.Value;
        }

        var starred = 0;
        if (IpcRequests.TryGetIntParameter(request, "starred", out var starredValue))
        {
            // v1.5.2：starred 语义升级为星级评分 0–5（0=无评分），列类型不变无需迁移。
            starred = starredValue!.Value;
            if (starred is < 0 or > 5)
            {
                return IpcRequests.InvalidArgument(request, "starred 星级评分只支持 0–5（0=无评分）");
            }
        }

        string? displayName = null;
        if (IpcRequests.TryGetStringParameter(request, "displayName", out var displayNameValue))
        {
            var trimmed = displayNameValue.Trim();
            if (trimmed.Length is < 1 or > 100 || trimmed.Any(char.IsControl))
            {
                return IpcRequests.InvalidArgument(request, "displayName 必须是 1–100 字符且不含控制字符");
            }

            displayName = trimmed;
        }

        if (store.TryGetTagByName("user", name) is not null)
        {
            return IpcRequests.InvalidArgument(request, $"同名用户标签已存在：{name}");
        }

        var utcNow = DateTime.UtcNow;
        var tag = new PersistedTag(
            $"tag-{Guid.NewGuid():N}", "user", name, color, 1, 0, utcNow, utcNow,
            category, sortOrder, starred, displayName);
        store.CreateTag(tag);
        _events.Publish("tag.created", $"tag:{tag.TagId}", new { tagId = tag.TagId, name }, utcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TagDto(tag),
        };
    }

    /// <summary>
    /// 更新标签（feat-3）：color/category/sortOrder/starred/displayName 对 engine 与 user
    /// 标签均开放；name 是 engine 标签的身份键（扫描识别、Suppress/Reset 覆盖均按
    /// (kind, name) 匹配）不可变——engine 标签改名走 displayName，user 标签 name 可改。
    /// expectedRevision 乐观并发校验。
    /// </summary>
    public Envelope<object> TagsUpdate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "tagId", out var tagId)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 tagId 或 expectedRevision 参数");
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return IpcRequests.NotFound(request, $"标签不存在：{tagId}");
        }

        string? name = null;
        if (IpcRequests.TryGetStringParameter(request, "name", out var nameValue))
        {
            if (tag.Kind != "user")
            {
                return IpcRequests.InvalidArgument(request, "自动标签 name 不可变（身份键，扫描与 Suppress 均按 name 识别）；改名请用 displayName");
            }

            name = nameValue.Trim();
            if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            {
                return IpcRequests.InvalidArgument(request, "标签名必须是 1–100 字符且不含控制字符");
            }

            var duplicate = store.TryGetTagByName("user", name);
            if (duplicate is not null && duplicate.TagId != tagId)
            {
                return IpcRequests.InvalidArgument(request, $"同名用户标签已存在：{name}");
            }
        }

        string? color = null;
        if (IpcRequests.TryGetStringParameter(request, "color", out var colorValue))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(colorValue, "^#[0-9a-fA-F]{6}$"))
            {
                return IpcRequests.InvalidArgument(request, "color 必须是 #RRGGBB 十六进制格式");
            }

            color = colorValue;
        }

        string? category = null;
        if (IpcRequests.TryGetStringParameter(request, "category", out var categoryValue))
        {
            if (categoryValue is not ("engine" or "gameplay" or "social" or "special"))
            {
                return IpcRequests.InvalidArgument(request, "category 只支持 engine/gameplay/social/special");
            }

            category = categoryValue;
        }

        int? sortOrder = null;
        if (IpcRequests.TryGetIntParameter(request, "sortOrder", out var sortOrderValue))
        {
            sortOrder = sortOrderValue;
        }

        int? starred = null;
        if (IpcRequests.TryGetIntParameter(request, "starred", out var starredUpdate))
        {
            // v1.5.2：starred 语义升级为星级评分 0–5（0=无评分），列类型不变无需迁移。
            starred = starredUpdate;
            if (starred is < 0 or > 5)
            {
                return IpcRequests.InvalidArgument(request, "starred 星级评分只支持 0–5（0=无评分）");
            }
        }

        // displayName 支持显式 null 清除（回落 name）；字符串走与 name 相同的清洗校验。
        string? displayName = null;
        var clearDisplayName = false;
        if (request.Parameters is { ValueKind: System.Text.Json.JsonValueKind.Object } parameters
            && parameters.TryGetProperty("displayName", out var displayNameElement))
        {
            if (displayNameElement.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                clearDisplayName = true;
            }
            else if (displayNameElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var trimmed = (displayNameElement.GetString() ?? "").Trim();
                if (trimmed.Length is < 1 or > 100 || trimmed.Any(char.IsControl))
                {
                    return IpcRequests.InvalidArgument(request, "displayName 必须是 1–100 字符且不含控制字符");
                }

                displayName = trimmed;
            }
            else
            {
                return IpcRequests.InvalidArgument(request, "displayName 必须是字符串或 null");
            }
        }

        var newRevision = store.UpdateTag(
            tagId, name, color, category, sortOrder, starred, displayName, clearDisplayName,
            expectedRevision.Value, DateTime.UtcNow);
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
        _events.Publish("tag.updated", $"tag:{tagId}", new { tagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TagDto(updated),
        };
    }

    /// <summary>
    /// 删除标签：契约 note 要求返回受影响游戏列表；engine 标签删除时逐游戏登记
    /// Suppress（阻止扫描恢复用户删除的自动标签，策划案 TagOverride 语义）。
    /// </summary>
    public Envelope<object> TagsRemove(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "tagId", out var tagId)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 tagId 或 expectedRevision 参数");
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return IpcRequests.NotFound(request, $"标签不存在：{tagId}");
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

        _events.Publish("tag.removed", $"tag:{tagId}", new { tagId, affectedCount = affectedGames.Count }, DateTime.UtcNow);
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
        var store = _storeAccessor();
        if (store is null)
        {
            return (IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）"), null!, "", null!);
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "tagId", out var tagId)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return (IpcRequests.InvalidArgument(request, "缺少 gameId、tagId 或 expectedRevision 参数"), store, "", null!);
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return (IpcRequests.NotFound(request, $"游戏不存在：{gameId}"), store, gameId, null!);
        }

        var tag = store.TryGetTag(tagId);
        if (tag is null)
        {
            return (IpcRequests.NotFound(request, $"标签不存在：{tagId}"), store, gameId, null!);
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

    /// <summary>为游戏挂标签：AssignTag 幂等，已挂时 alreadyPresent=true。</summary>
    public Envelope<object> TagsAssign(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        var changed = store.AssignTag(gameId, tag.TagId, DateTime.UtcNow);
        _events.Publish("tag.assigned", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, assigned = true, alreadyPresent = !changed },
        };
    }

    /// <summary>解除游戏标签：engine 标签被移除时登记 Suppress，重扫不再恢复。</summary>
    public Envelope<object> TagsUnassign(IpcRequest request)
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

        _events.Publish("tag.unassigned", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, assigned = false, suppressedAuto },
        };
    }

    /// <summary>手动 Suppress 标签（含 user 标签）：登记覆盖，重扫不恢复该标签。</summary>
    public Envelope<object> TagsSuppress(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        store.SetTagOverride(gameId, tag.Kind, tag.Name, "suppress", DateTime.UtcNow);
        _events.Publish("tag.suppressed", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, suppressed = true },
        };
    }

    /// <summary>重置 engine 标签：清除 Suppress 覆盖并按当前引擎恢复挂载（user 标签无意义）。</summary>
    public Envelope<object> TagsReset(IpcRequest request)
    {
        var (error, store, gameId, tag) = TagGamePrecondition(request);
        if (error is not null)
        {
            return error;
        }

        if (tag.Kind != "engine")
        {
            return IpcRequests.InvalidArgument(request, "tags.reset 仅对自动标签有意义（清除 Suppress 并按当前引擎恢复）");
        }

        var cleared = store.ClearTagOverride(gameId, tag.Kind, tag.Name, DateTime.UtcNow);
        var restored = store.EnsureEngineTagAssigned(gameId, tag.Name, DateTime.UtcNow);
        _events.Publish("tag.reset", $"game:{gameId}", new { gameId, tagId = tag.TagId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, tagId = tag.TagId, clearedOverride = cleared, restored },
        };
    }

    /// <summary>标签 DTO（feat-3 起含 category/sortOrder/starred/displayName；UI 展示名优先 displayName）。</summary>
    private static object TagDto(PersistedTag tag) => new
    {
        tagId = tag.TagId,
        kind = tag.Kind,
        name = tag.Name,
        color = tag.Color,
        category = tag.Category,
        sortOrder = tag.SortOrder,
        starred = tag.Starred,
        displayName = tag.DisplayName,
        gameCount = tag.GameCount,
        revision = tag.Revision,
        createdUtc = tag.CreatedUtc.ToString("O"),
        updatedUtc = tag.UpdatedUtc.ToString("O"),
    };
}
