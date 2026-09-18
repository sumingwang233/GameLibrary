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



/// <summary>OperationDispatcher 的 ViewSettings 域 handler（阶段三按域拆分，partial）。</summary>
public sealed partial class OperationDispatcher
{
    /// <summary>内置视图 + 自定义视图（T15-C）；activeViewId 为宿主内存态。</summary>
    private Envelope<object> ViewsList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var builtin = BuiltInViews.All.Select(v => new
        {
            viewId = v.ViewId,
            name = v.Name,
            kind = "builtin",
            search = (string?)null,
            favoriteOnly = v.ViewId == "favorites",
            sort = "title",
            revision = (int?)null,
            active = string.Equals(_state.ActiveViewId, v.ViewId, StringComparison.Ordinal),
        });
        var custom = store.ListViews().Select(v => new
        {
            viewId = v.ViewId,
            name = v.Name,
            kind = "custom",
            search = v.Search,
            favoriteOnly = v.FavoriteOnly,
            sort = v.Sort,
            revision = (int?)v.Revision,
            active = string.Equals(_state.ActiveViewId, v.ViewId, StringComparison.Ordinal),
        });

        var items = builtin.Concat(custom).ToArray();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = items.Length, items, activeViewId = _state.ActiveViewId },
        };
    }

    private Envelope<object> ViewsGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "viewId", out var viewId))
        {
            return InvalidArgument(request, "缺少 viewId 参数");
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
                    sort = "title",
                    revision = (int?)null,
                },
            };
        }

        var view = store.TryGetView(viewId);
        if (view is null)
        {
            return NotFound(request, $"视图不存在：{viewId}");
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
                sort = view.Sort,
                revision = (int?)view.Revision,
            },
        };
    }

    private Envelope<object> ViewsCreate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "name", out var name) || name.Length == 0)
        {
            return InvalidArgument(request, "缺少 name 参数");
        }

        TryGetStringParameter(request, "search", out var search);
        TryGetBoolParameter(request, "favoriteOnly", out var favoriteOnly);
        TryGetStringParameter(request, "sort", out var sort);
        if (sort.Length > 0 && sort is not ("title" or "recent"))
        {
            return InvalidArgument(request, "sort 只支持 title/recent");
        }

        var now = DateTime.UtcNow;
        var view = new LibraryView
        {
            ViewId = $"view-{Guid.NewGuid():N}",
            Name = name,
            Search = search.Length > 0 ? search : null,
            FavoriteOnly = favoriteOnly == true,
            Sort = sort.Length > 0 ? sort : "title",
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        store.InsertView(view);
        _state.Events.Publish("view.updated", $"view:{view.ViewId}", new { viewId = view.ViewId, name }, now);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ViewDto(store, view.ViewId),
        };
    }

    private Envelope<object> ViewsUpdate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "viewId", out var viewId))
        {
            return InvalidArgument(request, "缺少 viewId 参数");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (BuiltInViews.All.Any(v => v.ViewId == viewId))
        {
            return InvalidArgument(request, $"内置视图 {viewId} 不可修改");
        }

        TryGetStringParameter(request, "name", out var name);
        TryGetStringParameter(request, "search", out var search);
        TryGetBoolParameter(request, "favoriteOnly", out var favoriteOnly);
        TryGetStringParameter(request, "sort", out var sort);
        if (sort.Length > 0 && sort is not ("title" or "recent"))
        {
            return InvalidArgument(request, "sort 只支持 title/recent");
        }

        var newRevision = store.UpdateView(
            viewId,
            name.Length > 0 ? name : null,
            search.Length > 0 ? search : null,
            favoriteOnly,
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

        _state.Events.Publish("view.updated", $"view:{viewId}", new { viewId, revision = newRevision }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ViewDto(store, viewId),
        };
    }

    private Envelope<object> ViewsRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "viewId", out var viewId))
        {
            return InvalidArgument(request, "缺少 viewId 参数");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (BuiltInViews.All.Any(v => v.ViewId == viewId))
        {
            return InvalidArgument(request, $"内置视图 {viewId} 不可删除");
        }

        var view = store.TryGetView(viewId);
        if (view is null)
        {
            return NotFound(request, $"视图不存在：{viewId}");
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
        if (string.Equals(_state.ActiveViewId, viewId, StringComparison.Ordinal))
        {
            _state.ActiveViewId = null;
            store.WriteSettingsKeys([("activeViewId", null)], DateTime.UtcNow);
        }

        _state.Events.Publish("view.updated", $"view:{viewId}", new { viewId, removed = true }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { viewId, removed = true },
        };
    }

    /// <summary>激活视图：校验存在性，记录内存态并广播 view.activated（瞬时语义，收据同键重放幂等）。</summary>
    private Envelope<object> ViewsActivate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "viewId", out var viewId))
        {
            return InvalidArgument(request, "缺少 viewId 参数");
        }

        var isBuiltin = BuiltInViews.All.Any(v => v.ViewId == viewId);
        var view = store.TryGetView(viewId);
        if (!isBuiltin && view is null)
        {
            return NotFound(request, $"视图不存在：{viewId}");
        }

        _state.ActiveViewId = viewId;
        // T-settings：激活视图持久化（跨重启恢复）。
        store.WriteSettingsKeys([("activeViewId", viewId)], DateTime.UtcNow);
        _state.Events.Publish("view.activated", $"view:{viewId}", new { viewId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { viewId, active = true },
        };
    }

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
            sort = view.Sort,
            revision = (int?)view.Revision,
        };
    }

    /// <summary>通知列表（T18）：state 过滤可选；通知是持久存储，重开不丢。</summary>
    private Envelope<object> NotificationsList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        string? state = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } nlParameters
            && nlParameters.TryGetProperty("state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String)
        {
            state = stateElement.GetString();
            if (state is not ("pending" or "acknowledged" or "deferred"))
            {
                return InvalidArgument(request, "state 只支持 pending/acknowledged/deferred");
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

    private Envelope<object> NotificationsGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "notificationId", out var notificationId))
        {
            return InvalidArgument(request, "缺少 notificationId 参数");
        }

        var notification = store.TryGetNotification(notificationId);
        if (notification is null)
        {
            return NotFound(request, $"通知不存在：{notificationId}");
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
    private Envelope<object> NotificationTransition(IpcRequest request, string toState)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "notificationId", out var notificationId))
        {
            return InvalidArgument(request, "缺少 notificationId 参数");
        }

        var notification = store.TryGetNotification(notificationId);
        if (notification is null)
        {
            return NotFound(request, $"通知不存在：{notificationId}");
        }

        var transitioned = store.TransitionNotification(notificationId, toState, DateTime.UtcNow);
        if (transitioned is null)
        {
            return InvalidArgument(request, $"通知当前状态 {notification.State}；仅 pending 可标记为 {toState}");
        }

        _state.Events.Publish("notification.updated", $"notification:{notificationId}", new
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
    private Envelope<object> SettingsGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
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
    private Envelope<object> SettingsUpdate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        if (request.Parameters is not { ValueKind: JsonValueKind.Object } parameters)
        {
            return InvalidArgument(request, "缺少 patch 字段");
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
                    return InvalidArgument(request, $"视图不存在：{viewId}");
                }

                activeViewId = viewId;
                keys.Add(("activeViewId", viewId));
            }
            else
            {
                return InvalidArgument(request, "activeViewId 必须是字符串或 null");
            }
        }

        if (parameters.TryGetProperty("autostartEnabled", out var autostartElement))
        {
            if (autostartElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return InvalidArgument(request, "autostartEnabled 必须是布尔值");
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
                return InvalidArgument(request, "scanIntervalMinutes 必须是 1–10080 的整数（分钟）");
            }

            scanIntervalMinutes = interval;
            keys.Add(("scanIntervalMinutes", interval.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (parameters.TryGetProperty("theme", out var themeElement))
        {
            if (themeElement.ValueKind != JsonValueKind.String
                || themeElement.GetString() is not ("dark" or "light" or "system"))
            {
                return InvalidArgument(request, "theme 只支持 dark/light/system");
            }

            theme = themeElement.GetString()!;
            keys.Add(("theme", theme));
        }

        if (parameters.TryGetProperty("closeToTray", out var trayElement))
        {
            if (trayElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return InvalidArgument(request, "closeToTray 必须是布尔值");
            }

            closeToTray = trayElement.ValueKind == JsonValueKind.True;
            keys.Add(("closeToTray", closeToTray ? "true" : null));
        }

        if (parameters.TryGetProperty("uiFontScale", out var scaleElement))
        {
            if (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetDouble(out var scale)
                || scale is < AppSettingsSnapshot.MinUiFontScale or > AppSettingsSnapshot.MaxUiFontScale)
            {
                return InvalidArgument(request,
                    $"uiFontScale 必须是 {AppSettingsSnapshot.MinUiFontScale}–{AppSettingsSnapshot.MaxUiFontScale} 之间的数字");
            }

            uiFontScale = scale;
            keys.Add(("uiFontScale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (parameters.TryGetProperty("uiFontFamily", out var fontElement))
        {
            if (fontElement.ValueKind != JsonValueKind.String)
            {
                return InvalidArgument(request, "uiFontFamily 必须是已安装字体的名称");
            }

            var family = fontElement.GetString()?.Trim() ?? "";
            if (family.Length is < 1 or > 100
                || family.Any(character => !char.IsLetterOrDigit(character)
                    && character is not (' ' or '-' or '_' or '.')))
            {
                return InvalidArgument(request, "uiFontFamily 只能包含 1–100 个字体名称字符");
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
                        requestedDirectory, _state.DataDirectory, out var canonical, out var error))
                {
                    return InvalidArgument(request, error);
                }

                if (_state.Roots.Contains(canonical))
                {
                    return InvalidArgument(request, "缓存位置不能位于已添加的游戏库内");
                }

                cacheParentDirectory = canonical;
                keys.Add(("cacheParentDirectory", canonical));
            }
            else
            {
                return InvalidArgument(request, "cacheParentDirectory 必须是绝对目录字符串或 null");
            }
        }

        // 所有字段均验证成功后才触碰 Windows 启动文件夹，避免无效 patch 留下副作用。
        if (autostartEnabled != current.AutostartEnabled)
        {
            var manager = _state.StartupShortcuts;
            var result = autostartEnabled ? manager.Enable(_state.DataDirectory) : manager.Disable();
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
        _state.ActiveViewId = activeViewId;
        _state.Events.Publish("settings.updated", "settings", new { revision = newRevision }, DateTime.UtcNow);

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
    private Envelope<object> SettingsReset(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var current = store.ReadSettings();
        if (current.AutostartEnabled)
        {
            var result = _state.StartupShortcuts.Disable();
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
        _state.ActiveViewId = null;
        _state.Events.Publish("settings.updated", "settings", new { revision, reset = true }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = SettingsDto(AppSettingsSnapshot.Defaults(revision)),
        };
    }

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

    /// <summary>
    /// host.stop（T18）：先返回已接收收据，随后在响应送达后请求宿主优雅停机
    /// （排空连接后退出进程；不杀游戏/翻译器）。stop 属持久收据操作，同键重放幂等。
    /// </summary>
}
