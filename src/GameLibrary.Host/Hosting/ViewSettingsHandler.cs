using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.Host.Tools;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Shell;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 视图设置域处理器：views.list/get/create/update/remove/activate 六操作 +
/// notifications.list/get/acknowledge/defer 四操作 + settings.get/update/reset 三操作。
/// Store 经委托每请求取当前值（library.init / backups.restore 会整体替换 Library，
/// 禁止构造时缓存 store 引用）；Events 为 init-only 引用（换库由 EventStream.BindStore
/// 在其内部重绑）；ActiveViewId 是宿主可变内存态，经 getter/setter 委托读写；
/// StartupShortcuts 为宿主 settable 属性，经委托每请求取值（测试可能在任意时序替换）。
/// 由 DispatchCore 调用，天然继承幂等收据（本域变更操作在 ReceiptOperations）、
/// 串行门、权限与维护模式等中间件。
/// </summary>
internal sealed class ViewSettingsHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly EventStream _events;
    private readonly Func<string?> _getActiveViewId;
    private readonly Action<string?> _setActiveViewId;
    private readonly RootRegistry _roots;
    private readonly string _dataDirectory;
    private readonly Func<StartupShortcutManager> _startupShortcuts;

    public ViewSettingsHandler(
        Func<SqliteLibraryStore?> storeAccessor,
        EventStream events,
        Func<string?> getActiveViewId,
        Action<string?> setActiveViewId,
        RootRegistry roots,
        string dataDirectory,
        Func<StartupShortcutManager> startupShortcuts)
    {
        _storeAccessor = storeAccessor;
        _events = events;
        _getActiveViewId = getActiveViewId;
        _setActiveViewId = setActiveViewId;
        _roots = roots;
        _dataDirectory = dataDirectory;
        _startupShortcuts = startupShortcuts;
    }

    /// <summary>内置视图 + 自定义视图（T15-C）；activeViewId 为宿主内存态。</summary>
    public Envelope<object> ViewsList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var builtin = BuiltInViews.All.Select(v => new
        {
            viewId = v.ViewId,
            name = v.Name,
            kind = "builtin",
            search = (string?)null,
            favoriteOnly = v.ViewId == "favorites",
            tagId = (string?)null,
            sort = "title",
            revision = (int?)null,
            active = string.Equals(_getActiveViewId(), v.ViewId, StringComparison.Ordinal),
        });
        var custom = store.ListViews().Select(v => new
        {
            viewId = v.ViewId,
            name = v.Name,
            kind = "custom",
            search = v.Search,
            favoriteOnly = v.FavoriteOnly,
            tagId = v.TagId,
            sort = v.Sort,
            revision = (int?)v.Revision,
            active = string.Equals(_getActiveViewId(), v.ViewId, StringComparison.Ordinal),
        });

        var items = builtin.Concat(custom).ToArray();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = items.Length, items, activeViewId = _getActiveViewId() },
        };
    }

    /// <summary>查询单个视图：内置视图先行命中，自定义视图走库。</summary>
    public Envelope<object> ViewsGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "viewId", out var viewId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 viewId 参数");
        }

        var builtin = BuiltInViews.All.FirstOrDefault(v => v.ViewId == viewId);
        if (builtin.ViewId is not null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    viewId = builtin.ViewId,
                    name = builtin.Name,
                    kind = "builtin",
                    search = (string?)null,
                    favoriteOnly = builtin.ViewId == "favorites",
                    tagId = (string?)null,
                    sort = "title",
                    revision = (int?)null,
                },
            };
        }

        var view = store.TryGetView(viewId);
        if (view is null)
        {
            return IpcRequests.NotFound(request, $"视图不存在：{viewId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                viewId = view.ViewId,
                name = view.Name,
                kind = "custom",
                search = view.Search,
                favoriteOnly = view.FavoriteOnly,
                tagId = view.TagId,
                sort = view.Sort,
                revision = (int?)view.Revision,
            },
        };
    }

    /// <summary>创建自定义视图：name 必填；sort 白名单与 games.list 完全一致（六值，bug-1）。</summary>
    public Envelope<object> ViewsCreate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "name", out var name) || name.Length == 0)
        {
            return IpcRequests.InvalidArgument(request, "缺少 name 参数");
        }

        IpcRequests.TryGetStringParameter(request, "search", out var search);
        IpcRequests.TryGetBoolParameter(request, "favoriteOnly", out var favoriteOnly);
        IpcRequests.TryGetStringParameter(request, "tagId", out var tagId);
        IpcRequests.TryGetStringParameter(request, "sort", out var sort);
        if (sort.Length > 0 && !IsValidSort(sort))
        {
            return IpcRequests.InvalidArgument(request, SortRuleMessage);
        }

        var now = DateTime.UtcNow;
        var view = new LibraryView
        {
            ViewId = $"view-{Guid.NewGuid():N}",
            Name = name,
            Search = search.Length > 0 ? search : null,
            FavoriteOnly = favoriteOnly == true,
            TagId = tagId.Length > 0 ? tagId : null,
            Sort = sort.Length > 0 ? sort : "title",
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        store.InsertView(view);
        _events.Publish("view.updated", $"view:{view.ViewId}", new { viewId = view.ViewId, name }, now);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ViewDto(store, view.ViewId),
        };
    }

    /// <summary>更新自定义视图（内置视图不可修改）：expectedRevision 乐观并发校验。</summary>
    public Envelope<object> ViewsUpdate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "viewId", out var viewId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 viewId 参数");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (BuiltInViews.All.Any(v => v.ViewId == viewId))
        {
            return IpcRequests.InvalidArgument(request, $"内置视图 {viewId} 不可修改");
        }

        IpcRequests.TryGetStringParameter(request, "name", out var name);
        IpcRequests.TryGetStringParameter(request, "search", out var search);
        IpcRequests.TryGetBoolParameter(request, "favoriteOnly", out var favoriteOnly);
        IpcRequests.TryGetStringParameter(request, "tagId", out var tagId);
        IpcRequests.TryGetStringParameter(request, "sort", out var sort);
        if (sort.Length > 0 && !IsValidSort(sort))
        {
            return IpcRequests.InvalidArgument(request, SortRuleMessage);
        }

        var newRevision = store.UpdateView(
            viewId,
            name.Length > 0 ? name : null,
            search.Length > 0 ? search : null,
            favoriteOnly,
            tagId.Length > 0 ? tagId : null,
            sort.Length > 0 ? sort : null,
            expectedRevision.Value,
            DateTime.UtcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetView(viewId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"视图 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        _events.Publish("view.updated", $"view:{viewId}", new { viewId, revision = newRevision }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ViewDto(store, viewId),
        };
    }

    /// <summary>
    /// 删除自定义视图（内置视图不可删除）：期望 Revision 乐观校验；删除的是当前激活
    /// 视图时清内存态并把 settings 表 activeViewId 置 null。
    /// </summary>
    public Envelope<object> ViewsRemove(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "viewId", out var viewId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 viewId 参数");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (BuiltInViews.All.Any(v => v.ViewId == viewId))
        {
            return IpcRequests.InvalidArgument(request, $"内置视图 {viewId} 不可删除");
        }

        var view = store.TryGetView(viewId);
        if (view is null)
        {
            return IpcRequests.NotFound(request, $"视图不存在：{viewId}");
        }

        if (view.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"视图 Revision 不一致：期望 {expectedRevision}，当前 {view.Revision}",
                    Retryable = false,
                    CurrentRevision = view.Revision,
                },
            };
        }

        store.DeleteView(viewId);
        if (string.Equals(_getActiveViewId(), viewId, StringComparison.Ordinal))
        {
            _setActiveViewId(null);
            store.WriteSettingsKeys([("activeViewId", null)], DateTime.UtcNow);
        }

        _events.Publish("view.updated", $"view:{viewId}", new { viewId, removed = true }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { viewId, removed = true },
        };
    }

    /// <summary>激活视图：校验存在性，记录内存态并广播 view.activated（瞬时语义，收据同键重放幂等）。</summary>
    public Envelope<object> ViewsActivate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "viewId", out var viewId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 viewId 参数");
        }

        var isBuiltin = BuiltInViews.All.Any(v => v.ViewId == viewId);
        var view = store.TryGetView(viewId);
        if (!isBuiltin && view is null)
        {
            return IpcRequests.NotFound(request, $"视图不存在：{viewId}");
        }

        _setActiveViewId(viewId);
        // T-settings：激活视图持久化（跨重启恢复）。
        store.WriteSettingsKeys([("activeViewId", viewId)], DateTime.UtcNow);
        _events.Publish("view.activated", $"view:{viewId}", new { viewId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { viewId, active = true },
        };
    }

    /// <summary>
    /// sort 白名单与 games.list 完全一致（bug-1：收藏夹默认携带 accepted-desc 保存被拒）。
    /// 文案与 OperationSchemas 中 games.list/views.* 的 sort 描述保持同源措辞。
    /// </summary>
    private static bool IsValidSort(string sort) =>
        sort is "title" or "title-asc" or "title-desc" or "recent" or "updated-desc" or "accepted-desc";

    private const string SortRuleMessage =
        "sort 只支持 title/title-asc、title-desc、recent/updated-desc 或 accepted-desc";

    /// <summary>自定义视图 DTO（视图存在性已由调用方保证）。</summary>
    private object ViewDto(SqliteLibraryStore store, string viewId)
    {
        var view = store.TryGetView(viewId)!;
        return new
        {
            viewId = view.ViewId,
            name = view.Name,
            kind = "custom",
            search = view.Search,
            favoriteOnly = view.FavoriteOnly,
            tagId = view.TagId,
            sort = view.Sort,
            revision = (int?)view.Revision,
        };
    }

    /// <summary>通知列表（T18）：state 过滤可选；通知是持久存储，重开不丢。</summary>
    public Envelope<object> NotificationsList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        string? state = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } nlParameters
            && nlParameters.TryGetProperty("state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String)
        {
            state = stateElement.GetString();
            if (state is not ("pending" or "acknowledged" or "deferred"))
            {
                return IpcRequests.InvalidArgument(request, "state 只支持 pending/acknowledged/deferred");
            }
        }

        var notifications = store.ListNotifications(state);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = notifications.Count,
                items = notifications.Select(n => new
                {
                    notificationId = n.NotificationId,
                    kind = n.Kind,
                    title = n.Title,
                    state = n.State,
                    candidateIds = n.CandidateIds,
                    createdUtc = n.CreatedUtc.ToString("O"),
                    updatedUtc = n.UpdatedUtc.ToString("O"),
                }).ToArray(),
            },
        };
    }

    /// <summary>查询单个通知：不存在即 NotFound。</summary>
    public Envelope<object> NotificationsGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "notificationId", out var notificationId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 notificationId 参数");
        }

        var notification = store.TryGetNotification(notificationId);
        if (notification is null)
        {
            return IpcRequests.NotFound(request, $"通知不存在：{notificationId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                notificationId = notification.NotificationId,
                kind = notification.Kind,
                title = notification.Title,
                state = notification.State,
                candidateIds = notification.CandidateIds,
                createdUtc = notification.CreatedUtc.ToString("O"),
                updatedUtc = notification.UpdatedUtc.ToString("O"),
            },
        };
    }

    /// <summary>
    /// 通知状态迁移（T18）：acknowledge ≠ 接受候选——只把通知标记为已读，
    /// 关联候选保持 pendingReview，需显式 candidates.accept/defer/ignore。
    /// </summary>
    public Envelope<object> NotificationTransition(IpcRequest request, string toState)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "notificationId", out var notificationId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 notificationId 参数");
        }

        var notification = store.TryGetNotification(notificationId);
        if (notification is null)
        {
            return IpcRequests.NotFound(request, $"通知不存在：{notificationId}");
        }

        var transitioned = store.TransitionNotification(notificationId, toState, DateTime.UtcNow);
        if (transitioned is null)
        {
            return IpcRequests.InvalidArgument(request, $"通知当前状态 {notification.State}；仅 pending 可标记为 {toState}");
        }

        _events.Publish("notification.updated", $"notification:{notificationId}", new
        {
            notificationId,
            state = toState,
        }, DateTime.UtcNow);

        // ack/defer 都不是候选决定：明确给出后续步骤（LA/AI-11 结构化引导）。
        var nextActions = new List<NextAction>();
        if (toState == "acknowledged")
        {
            nextActions.Add(new NextAction
            {
                OperationId = "candidates.list",
                Reason = "acknowledge 只标记通知已读；候选仍为 pendingReview，需显式 accept/defer/ignore",
            });
        }
        else
        {
            nextActions.Add(new NextAction
            {
                OperationId = "notifications.list",
                Reason = "deferred 的通知默认不再主动提醒；有全新候选时才会生成新通知",
            });
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                notificationId,
                state = toState,
                candidateIds = notification.CandidateIds,
                candidatesAccepted = (bool?)null,
            },
            NextActions = nextActions,
        };
    }

    /// <summary>
    /// settings.get：返回快照（受限字段集 + 单调 Revision）。字段应用点：
    /// activeViewId（宿主恢复/保存）、scanIntervalMinutes（宿主启动时构造核对周期）、
    /// autostartEnabled（启动文件夹快捷方式）；theme/closeToTray 供 Desktop 消费。
    /// </summary>
    public Envelope<object> SettingsGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = SettingsDto(store.ReadSettings()),
        };
    }

    /// <summary>
    /// settings.update：受限字段 patch（未知字段拒绝）；期望 Revision 乐观校验。
    /// autostartEnabled 先应用启动文件夹快捷方式（不写注册表），成功后才落库。
    /// </summary>
    public Envelope<object> SettingsUpdate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters)
        {
            return IpcRequests.InvalidArgument(request, "缺少 patch 字段");
        }

        var declared = new HashSet<string>(StringComparer.Ordinal)
            { "expectedRevision", "idempotencyKey", "activeViewId", "autostartEnabled", "scanIntervalMinutes", "theme", "closeToTray", "uiFontScale", "uiFontFamily", "cacheParentDirectory" };
        var unknown = parameters.EnumerateObject()
            .Where(p => !declared.Contains(p.Name))
            .Select(p => p.Name)
            .ToArray();
        if (unknown.Length > 0)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"未知 patch 字段：{string.Join(", ", unknown)}",
                    Retryable = false,
                },
            };
        }

        var current = store.ReadSettings();
        if (current.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"设置 Revision 不一致：期望 {expectedRevision}，当前 {current.Revision}",
                    Retryable = false,
                    CurrentRevision = current.Revision,
                },
            };
        }

        string? activeViewId = current.ActiveViewId;
        var autostartEnabled = current.AutostartEnabled;
        var scanIntervalMinutes = current.ScanIntervalMinutes;
        var theme = current.Theme;
        var closeToTray = current.CloseToTray;
        var uiFontScale = current.UiFontScale;
        var uiFontFamily = current.UiFontFamily;
        var cacheParentDirectory = current.CacheParentDirectory;
        var keys = new List<(string Key, string? Value)>();

        if (parameters.TryGetProperty("activeViewId", out var viewElement))
        {
            if (viewElement.ValueKind is JsonValueKind.Null)
            {
                activeViewId = null;
                keys.Add(("activeViewId", null));
            }
            else if (viewElement.ValueKind == JsonValueKind.String)
            {
                var viewId = viewElement.GetString();
                var known = GameLibrary.Infrastructure.Persistence.BuiltInViews.All.Any(v => v.ViewId == viewId)
                    || store.TryGetView(viewId!) is not null;
                if (!known)
                {
                    return IpcRequests.InvalidArgument(request, $"视图不存在：{viewId}");
                }

                activeViewId = viewId;
                keys.Add(("activeViewId", viewId));
            }
            else
            {
                return IpcRequests.InvalidArgument(request, "activeViewId 必须是字符串或 null");
            }
        }

        if (parameters.TryGetProperty("autostartEnabled", out var autostartElement))
        {
            if (autostartElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return IpcRequests.InvalidArgument(request, "autostartEnabled 必须是布尔值");
            }

            var desired = autostartElement.ValueKind == JsonValueKind.True;
            autostartEnabled = desired;
            keys.Add(("autostartEnabled", desired ? "true" : null));
        }

        if (parameters.TryGetProperty("scanIntervalMinutes", out var intervalElement))
        {
            if (intervalElement.ValueKind != JsonValueKind.Number || !intervalElement.TryGetInt32(out var interval)
                || interval is < 1 or > 10080)
            {
                return IpcRequests.InvalidArgument(request, "scanIntervalMinutes 必须是 1–10080 的整数（分钟）");
            }

            scanIntervalMinutes = interval;
            keys.Add(("scanIntervalMinutes", interval.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (parameters.TryGetProperty("theme", out var themeElement))
        {
            if (themeElement.ValueKind != JsonValueKind.String
                || themeElement.GetString() is not ("dark" or "light" or "system"))
            {
                return IpcRequests.InvalidArgument(request, "theme 只支持 dark/light/system");
            }

            theme = themeElement.GetString()!;
            keys.Add(("theme", theme));
        }

        if (parameters.TryGetProperty("closeToTray", out var trayElement))
        {
            if (trayElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return IpcRequests.InvalidArgument(request, "closeToTray 必须是布尔值");
            }

            closeToTray = trayElement.ValueKind == JsonValueKind.True;
            keys.Add(("closeToTray", closeToTray ? "true" : null));
        }

        if (parameters.TryGetProperty("uiFontScale", out var scaleElement))
        {
            if (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetDouble(out var scale)
                || scale is < AppSettingsSnapshot.MinUiFontScale or > AppSettingsSnapshot.MaxUiFontScale)
            {
                return IpcRequests.InvalidArgument(request,
                    $"uiFontScale 必须是 {AppSettingsSnapshot.MinUiFontScale}–{AppSettingsSnapshot.MaxUiFontScale} 之间的数字");
            }

            uiFontScale = scale;
            keys.Add(("uiFontScale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (parameters.TryGetProperty("uiFontFamily", out var fontElement))
        {
            if (fontElement.ValueKind != JsonValueKind.String)
            {
                return IpcRequests.InvalidArgument(request, "uiFontFamily 必须是已安装字体的名称");
            }

            var family = fontElement.GetString()?.Trim() ?? "";
            if (family.Length is < 1 or > 100
                || family.Any(character => !char.IsLetterOrDigit(character)
                    && character is not (' ' or '-' or '_' or '.')))
            {
                return IpcRequests.InvalidArgument(request, "uiFontFamily 只能包含 1–100 个字体名称字符");
            }

            uiFontFamily = family;
            keys.Add(("uiFontFamily", family));
        }

        if (parameters.TryGetProperty("cacheParentDirectory", out var cacheElement))
        {
            if (cacheElement.ValueKind == JsonValueKind.Null)
            {
                cacheParentDirectory = null;
                keys.Add(("cacheParentDirectory", null));
            }
            else if (cacheElement.ValueKind == JsonValueKind.String)
            {
                var requestedDirectory = cacheElement.GetString() ?? "";
                if (!OwnedPreviewCache.TryValidateParent(
                        requestedDirectory, _dataDirectory, out var canonical, out var error))
                {
                    return IpcRequests.InvalidArgument(request, error);
                }

                if (_roots.Contains(canonical))
                {
                    return IpcRequests.InvalidArgument(request, "缓存位置不能位于已添加的游戏库内");
                }

                cacheParentDirectory = canonical;
                keys.Add(("cacheParentDirectory", canonical));
            }
            else
            {
                return IpcRequests.InvalidArgument(request, "cacheParentDirectory 必须是绝对目录字符串或 null");
            }
        }

        // 所有字段均验证成功后才触碰 Windows 启动文件夹，避免无效 patch 留下副作用。
        if (autostartEnabled != current.AutostartEnabled)
        {
            var manager = _startupShortcuts();
            var result = autostartEnabled ? manager.Enable(_dataDirectory) : manager.Disable();
            if (!result.Success)
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.ConfigurationInvalid,
                        Message = $"开机启动配置失败：{result.Error}",
                        Retryable = true,
                    },
                };
            }
        }

        var newRevision = keys.Count > 0
            ? store.WriteSettingsKeys(keys, DateTime.UtcNow)
            : current.Revision;
        _setActiveViewId(activeViewId);
        _events.Publish("settings.updated", "settings", new { revision = newRevision }, DateTime.UtcNow);

        var updated = current with
        {
            Revision = newRevision,
            ActiveViewId = activeViewId,
            AutostartEnabled = autostartEnabled,
            ScanIntervalMinutes = scanIntervalMinutes,
            Theme = theme,
            CloseToTray = closeToTray,
            UiFontScale = uiFontScale,
            UiFontFamily = uiFontFamily,
            CacheParentDirectory = cacheParentDirectory,
        };
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = SettingsDto(updated),
        };
    }

    /// <summary>settings.reset：恢复默认值；开机启动一并关闭（移除快捷方式）。</summary>
    public Envelope<object> SettingsReset(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var current = store.ReadSettings();
        if (current.AutostartEnabled)
        {
            var result = _startupShortcuts().Disable();
            if (!result.Success)
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.ConfigurationInvalid,
                        Message = $"移除开机启动快捷方式失败：{result.Error}",
                        Retryable = true,
                    },
                };
            }
        }

        var revision = store.ResetSettings(DateTime.UtcNow);
        _setActiveViewId(null);
        _events.Publish("settings.updated", "settings", new { revision, reset = true }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = SettingsDto(AppSettingsSnapshot.Defaults(revision)),
        };
    }

    /// <summary>设置快照 DTO：九字段受限集，字段顺序即响应字段顺序。</summary>
    private static object SettingsDto(AppSettingsSnapshot settings) => new
    {
        revision = settings.Revision,
        activeViewId = settings.ActiveViewId,
        autostartEnabled = settings.AutostartEnabled,
        scanIntervalMinutes = settings.ScanIntervalMinutes,
        theme = settings.Theme,
        closeToTray = settings.CloseToTray,
        uiFontScale = settings.UiFontScale,
        uiFontFamily = settings.UiFontFamily,
        cacheParentDirectory = settings.CacheParentDirectory,
    };
}
