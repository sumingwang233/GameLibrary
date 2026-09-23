using System.Text.Json;
using System.Text.Json.Nodes;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Identity;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;
using GameLibrary.Infrastructure.Shell;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 游戏卡域处理器：games.list / games.get / games.create / games.remove /
/// games.update / games.relink 六操作；GameDto 两个重载（单游戏充实与批量充实
/// 共用，字段形状只有一个真源）与 HasReparseAncestor 随域整体迁入。
/// Store 经委托每请求取当前值（library.init / backups.restore 会整体替换 Library，
/// 禁止构造时缓存 store 引用）；Roots/Events 为 init-only 引用（HostRuntimeState
/// 构造后整体不可替换；换库由 EventStream.BindStore 在其内部重绑）。由 DispatchCore
/// 调用，天然继承幂等收据（games.update/create/remove/relink 在 ReceiptOperations）
/// 与串行门等中间件。
/// </summary>
internal sealed class GamesHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly RootRegistry _roots;
    private readonly EventStream _events;

    /// <summary>
    /// roots/events 以 init-only 引用直传：HostRuntimeState 构造后整体不可替换
    /// （HostRuntime.cs）；Store 经委托每请求取当前值。
    /// </summary>
    public GamesHandler(Func<SqliteLibraryStore?> storeAccessor, RootRegistry roots, EventStream events)
    {
        _storeAccessor = storeAccessor;
        _roots = roots;
        _events = events;
    }

    /// <summary>games.list（阶段三）：搜索/过滤/排序/分页全部下沉 SQL；viewId 直套视图筛选/排序。</summary>
    public Envelope<object> GamesList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        // 阶段三：搜索/过滤/排序/分页全部下沉 SQL——5000+ 条目不再整表载入内存。
        string? search = null;
        var favoriteFilter = false;
        string? sort = null;
        string? tagId = null;
        var limit = 0;
        var offset = 0;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } glParameters)
        {
            if (glParameters.TryGetProperty("search", out var searchElement) && searchElement.ValueKind == JsonValueKind.String)
            {
                search = searchElement.GetString();
            }

            if (glParameters.TryGetProperty("favorite", out var favElement) && favElement.ValueKind == JsonValueKind.True)
            {
                favoriteFilter = true;
            }

            if (glParameters.TryGetProperty("sort", out var sortElement) && sortElement.ValueKind == JsonValueKind.String)
            {
                sort = sortElement.GetString();
            }

            if (glParameters.TryGetProperty("tagId", out var tagElement) && tagElement.ValueKind == JsonValueKind.String)
            {
                tagId = tagElement.GetString();
            }

            // T15-C：viewId 直接套用该视图的筛选/排序语义（agent 可不先读视图定义）。
            if (glParameters.TryGetProperty("viewId", out var viewElement) && viewElement.ValueKind == JsonValueKind.String)
            {
                var viewId = viewElement.GetString();
                var builtinView = BuiltInViews.All.FirstOrDefault(v => v.ViewId == viewId);
                if (builtinView.ViewId == "favorites")
                {
                    favoriteFilter = true;
                }
                else if (builtinView.ViewId is null)
                {
                    var view = store.TryGetView(viewId!);
                    if (view is not null)
                    {
                        // 自定义收藏夹是一个完整的查询快照。旧实现只在调用方省略
                        // 参数时套用视图，导致 UI 里残留的排序/标签/搜索条件覆盖收藏夹。
                        search = view.Search;
                        favoriteFilter = view.FavoriteOnly;
                        tagId = view.TagId;
                        sort = view.Sort;
                    }
                }
            }

            // 分页：未携带 limit 保持全量（兼容既有 CLI/MCP 消费方）；携带后按 offset 截页。
            if (glParameters.TryGetProperty("limit", out var limitElement)
                && limitElement.ValueKind == JsonValueKind.Number
                && limitElement.TryGetInt32(out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 1000);
                if (glParameters.TryGetProperty("offset", out var offsetElement)
                    && offsetElement.ValueKind == JsonValueKind.Number
                    && offsetElement.TryGetInt32(out var parsedOffset))
                {
                    offset = Math.Max(0, parsedOffset);
                }
            }
        }

        if (sort is not null and not ("title" or "title-asc" or "title-desc" or "recent" or "updated-desc" or "accepted-desc"))
        {
            return IpcRequests.InvalidArgument(request, "sort 只支持 title/title-asc、title-desc、recent/updated-desc 或 accepted-desc");
        }

        var (total, games) = store.QueryGames(search, favoriteFilter, tagId, sort, limit, offset);
        // R41：批量充实取代逐游戏 4 次查询（各过一次存储锁）。
        var enrichment = store.EnrichGameCards(games);
        // feat-1：游玩统计与充实同批 SQL 聚合（launch_attempts，见 QueryPlaytimeStats）。
        var playtime = store.QueryPlaytimeStats(games.Select(g => g.GameId).ToArray());
        var dtos = games.Select(g => GameDto(g, enrichment[g.GameId],
            playtime.TryGetValue(g.GameId, out var stats) ? stats : null)).ToArray();

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total, items = dtos },
        };
    }

    /// <summary>games.get：按 gameId 取单游戏详情（单游戏充实路径）。</summary>
    public Envelope<object> GamesGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        // 详情页指纹面（WithHints 同款 JsonNode 后处理）：GameDto 是 list/detail 共用唯一真源，
        // similarTo 塞进 DTO 会让 games.list 变 O(N²)——只在 games.get 注入，单游戏 one-vs-N 实时可算。
        var node = JsonSerializer.SerializeToNode(GameDto(store, game), ContractJson.Options)
            ?? throw new InvalidOperationException("GameDto 序列化失败");
        var row = store.TryGetGameFingerprint(gameId);
        var entries = row is null ? null : TryDeserializeEntries(row.EntriesJson);
        if (row is not null)
        {
            node["fingerprint"] = new JsonObject
            {
                ["strategyVersion"] = row.StrategyVersion,
                ["entryCount"] = entries?.Count ?? 0,
                ["computedUtc"] = row.ComputedUtc.ToString("O"),
            };
        }
        else
        {
            node["fingerprint"] = null;
        }

        var similarTo = row is not null && entries is not null
            ? FingerprintSuggestions.Compute(
                store,
                new MatchFingerprint { StrategyVersion = row.StrategyVersion, Entries = entries },
                gameId)
            : [];
        node["similarTo"] = new JsonArray(
            [.. FingerprintSuggestions.ToPayload(similarTo)
                .Select(s => JsonSerializer.SerializeToNode(s, ContractJson.Options))]);

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = node,
        };
    }

    /// <summary>手动建卡：支持未被扫描器识别的目录和独立 EXE/SWF；不猜测启动方式。</summary>
    public Envelope<object> GamesCreate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "sourcePath", out var sourcePath))
        {
            return IpcRequests.InvalidArgument(request, "缺少 sourcePath（游戏目录或独立 EXE/SWF 的绝对路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(sourcePath);
        if (!validation.IsValid)
        {
            return IpcRequests.InvalidArgument(request, $"游戏路径非法（{validation.Reason}）：{sourcePath}");
        }

        var normalized = validation.Path!;
        if (IpcRequests.RejectPathOutsideRoots(request, normalized.PhysicalPath, _roots) is { } outsideRoot)
        {
            return outsideRoot;
        }

        var isDirectory = Directory.Exists(normalized.PhysicalPath);
        var isFile = File.Exists(normalized.PhysicalPath);
        if (!isDirectory && !isFile)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"游戏目录或文件不存在：{normalized.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        var extension = Path.GetExtension(normalized.PhysicalPath);
        if (isFile && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".swf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return IpcRequests.InvalidArgument(request, "独立文件仅支持 EXE、SWF 或 Windows 快捷方式（LNK）");
        }

        try
        {
            if (HasReparseAncestor(normalized.PhysicalPath))
            {
                return IpcRequests.InvalidArgument(request, "所选路径包含目录联接或符号链接，请选择真实游戏位置");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IpcRequests.InvalidArgument(request, $"无法验证游戏路径：{ex.Message}");
        }

        string? entryPath = isFile ? normalized.PhysicalPath : null;
        string? executablePath = isFile && extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized.PhysicalPath
            : null;
        IReadOnlyList<string> arguments = [];
        string? workingDirectory = executablePath is null ? null : Path.GetDirectoryName(executablePath);
        var isShortcut = isFile && extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        if (isShortcut)
        {
            var resolution = new LnkResolver(_roots.List().Select(root => root.Path))
                .Resolve(normalized.PhysicalPath);
            if (!resolution.IsUsableEntry || resolution.Info?.TargetPath is null)
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = resolution.Status switch
                        {
                            LnkStatus.OutOfScopeTarget => ErrorCodes.PermissionDenied,
                            LnkStatus.MissingTarget => ErrorCodes.EntryMissing,
                            LnkStatus.ShellCommand => ErrorCodes.ConfigurationInvalid,
                            _ => ErrorCodes.InvalidPath,
                        },
                        Message = $"快捷方式不可作为游戏入口（{resolution.Status}）：{resolution.Detail}",
                        Retryable = false,
                    },
                };
            }

            var targetValidation = Domain.Paths.GamePath.TryCreate(resolution.Info.TargetPath);
            if (!targetValidation.IsValid)
            {
                return IpcRequests.InvalidArgument(request, "快捷方式目标不是受支持的本地绝对路径");
            }

            entryPath = targetValidation.Path!.PhysicalPath;
            var targetExtension = Path.GetExtension(entryPath);
            if (!targetExtension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !targetExtension.Equals(".swf", StringComparison.OrdinalIgnoreCase))
            {
                return IpcRequests.InvalidArgument(request, "快捷方式目标只能是 EXE 或 SWF 游戏文件");
            }

            if (IpcRequests.RejectPathOutsideRoots(request, entryPath, _roots) is { } targetOutsideRoot)
            {
                return targetOutsideRoot;
            }

            try
            {
                if (HasReparseAncestor(entryPath))
                {
                    return IpcRequests.InvalidArgument(request, "快捷方式目标包含目录联接或符号链接");
                }

                if (resolution.Info.Arguments.Length >= 1023)
                {
                    return IpcRequests.InvalidArgument(request, "快捷方式参数可能超出解析缓冲区，不能安全导入");
                }

                arguments = WindowsCommandLine.ParseArguments(resolution.Info.Arguments);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return IpcRequests.InvalidArgument(request, $"快捷方式无法安全解析：{ex.Message}");
            }

            if (Path.GetExtension(entryPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = entryPath;
                workingDirectory = string.IsNullOrWhiteSpace(resolution.Info.WorkingDirectory)
                    ? Path.GetDirectoryName(entryPath)
                    : resolution.Info.WorkingDirectory;
                if (workingDirectory is null || !Directory.Exists(workingDirectory))
                {
                    return IpcRequests.InvalidArgument(request, "快捷方式的工作目录不存在");
                }

                if (IpcRequests.RejectPathOutsideRoots(request, workingDirectory, _roots) is { } workingOutsideRoot)
                {
                    return workingOutsideRoot;
                }

                try
                {
                    if (HasReparseAncestor(workingDirectory))
                    {
                        return IpcRequests.InvalidArgument(request, "快捷方式的工作目录包含目录联接或符号链接");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return IpcRequests.InvalidArgument(request, $"无法验证快捷方式的工作目录：{ex.Message}");
                }
            }
        }

        var existing = store.ListGames().FirstOrDefault(game =>
        {
            var prior = Domain.Paths.GamePath.TryCreate(game.RootPath);
            return game.Membership == "active"
                && prior.IsValid
                && prior.Path!.ComparisonKey == normalized.ComparisonKey;
        });
        if (existing is not null)
        {
            return IpcRequests.InvalidArgument(request, $"该游戏位置已入库：{existing.GameId}");
        }

        string title;
        JsonElement titleElement = default;
        var hasTitle = request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty("title", out titleElement);
        if (hasTitle)
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                return IpcRequests.InvalidArgument(request, "title 必须是字符串");
            }

            title = titleElement.GetString()?.Trim() ?? "";
        }
        else
        {
            title = isFile
                ? Path.GetFileNameWithoutExtension(normalized.PhysicalPath)
                : Path.GetFileName(normalized.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar));
        }

        if (title.Length is < 1 or > 200 || title.Any(char.IsControl))
        {
            return IpcRequests.InvalidArgument(request, "游戏标题必须是 1–200 字符且不含控制字符");
        }

        var utcNow = DateTime.UtcNow;
        var game = new GameCard
        {
            GameId = $"game-{Guid.NewGuid():N}",
            Title = title,
            RootPath = normalized.PhysicalPath,
            Kind = isShortcut ? "manualShortcut" : isFile ? "manualFile" : "manualDirectory",
            EntryPath = entryPath,
            Membership = "active",
            Availability = "available",
            TranslationInherited = normalized.Segments.Any(segment =>
                segment.Equals("[toolNeed]", StringComparison.OrdinalIgnoreCase)),
            AcceptedUtc = utcNow,
            UpdatedUtc = utcNow,
        };
        store.InsertGame(game);
        if (hasTitle)
        {
            var revision = store.SetGameField(game.GameId, "title", title, "user", 1, utcNow);
            game = game with { Revision = revision ?? 1 };
        }

        // 手动建卡同计算器接入（与 accept 语义对齐）：锁外哈希，指纹为 null 不阻塞建卡。
        UpsertFingerprint(store, game, entryPath, utcNow);
        _ = GameCoverService.Synchronize(store, game);

        _events.Publish("game.created", $"game:{game.GameId}", new
        {
            gameId = game.GameId,
            source = "manual",
        }, utcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                gameId = game.GameId,
                title = game.Title,
                rootPath = game.RootPath,
                kind = game.Kind,
                entryPath = game.EntryPath,
                membership = game.Membership,
                revision = game.Revision,
                launchSuggestion = executablePath is null ? null : new
                {
                    executablePath,
                    argv = arguments,
                    cwd = workingDirectory,
                },
            },
        };
    }

    /// <summary>games.remove：软移除（membership=removed）并隐式创建 ExactPath 忽略规则，不删除任何文件。</summary>
    public Envelope<object> GamesRemove(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "games.remove 需要 gameId、expectedRevision 参数");
        }

        var current = store.TryGetGame(gameId);
        if (current is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        if (current.Membership != "active")
        {
            return IpcRequests.InvalidArgument(request, $"游戏已从库中移除：{gameId}");
        }

        IpcRequests.TryGetBoolParameter(request, "deleteFiles", out var deleteFilesValue);
        var deleteFiles = deleteFilesValue == true;
        if (deleteFiles)
        {
            var target = Path.GetFullPath(current.RootPath);
            if (current.Revision != expectedRevision.Value)
                return IpcRequests.InvalidArgument(request, "游戏记录已变化，请重新打开详情确认删除路径");
            if (!IpcRequests.TryGetStringParameter(request, "confirmedPath", out var confirmedPath)
                || !string.Equals(confirmedPath, current.RootPath, StringComparison.Ordinal))
                return IpcRequests.InvalidArgument(request, "删除原文件需要确认完整游戏路径");
            if (current.Kind == "manualShortcut")
                return IpcRequests.InvalidArgument(request, "快捷方式不能确定原文件范围，请在资源管理器中清理");
            if (IpcRequests.RejectPathOutsideRoots(request, target, _roots) is { } outside) return outside;
            if (target.TrimEnd('\\', '/') == Path.GetPathRoot(target)?.TrimEnd('\\', '/')
                || _roots.List().Any(root => RuntimeStateStore.ContainsPath(target, root.Path.PhysicalPath)))
                return IpcRequests.InvalidArgument(request, "不能删除游戏库根目录；如需删除请先移除该游戏库目录");
            if (store.ListGames().Any(game => game.GameId != gameId && game.Membership == "active"
                && (RuntimeStateStore.ContainsPath(target, game.RootPath) || RuntimeStateStore.ContainsPath(game.RootPath, target))))
                return IpcRequests.InvalidArgument(request, "该位置与其他游戏共用或包含其他游戏，请先整理重复记录");
            if (HasReparseAncestor(target))
                return IpcRequests.InvalidArgument(request, "不能删除包含目录联接或符号链接的路径");
            if (Directory.Exists(target))
            {
                // 只枚举普通目录，遇到联接立即拒绝，不沿联接遍历。
                var pending = new Stack<string>();
                pending.Push(target);
                while (pending.TryPop(out var directory))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            return IpcRequests.InvalidArgument(request, "游戏目录包含链接，请在资源管理器中确认删除范围");
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    }
                }
            }
            if (!File.Exists(target) && !Directory.Exists(target))
                return IpcRequests.NotFound(request, $"游戏文件或目录不存在：{target}");
            try
            {
                if (File.Exists(target))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(target, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return IpcRequests.InvalidArgument(request, $"原文件未能移入回收站，保留游戏库记录：{ex.Message}");
            }
        }

        var utcNow = DateTime.UtcNow;
        var ignore = new IgnoreRule
        {
            IgnoreId = $"ignore-{Guid.NewGuid():N}",
            Scope = "ExactPath",
            Path = current.RootPath,
            GameId = gameId,
            Reason = "games.remove",
            CreatedUtc = utcNow,
        };
        var newRevision = store.RemoveGame(gameId, expectedRevision.Value, ignore, utcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        _events.Publish("game.removed", $"game:{gameId}", new { gameId, ignoreId = ignore.IgnoreId }, utcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                gameId,
                membership = "removed",
                revision = newRevision.Value,
                ignoreId = ignore.IgnoreId,
                filesDeleted = deleteFiles,
                recycled = deleteFiles,
            },
        };
    }

    /// <summary>
    /// games.update（T13 补齐）：受限字段 patch。本步仅开放 favorite；
    /// 参数中出现任何未声明字段一律拒绝（契约 4：写请求只允许 schema 声明的字段）。
    /// </summary>
    public Envelope<object> GamesUpdate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 gameId 参数");
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
            { "gameId", "expectedRevision", "idempotencyKey", "favorite" };
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
                    Message = $"未知 patch 字段：{string.Join(", ", unknown)}；games.update 当前仅支持 favorite",
                    Retryable = false,
                },
            };
        }

        if (!parameters.TryGetProperty("favorite", out var favoriteElement)
            || favoriteElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return IpcRequests.InvalidArgument(request, "缺少 favorite 布尔字段（games.update 当前仅支持 favorite patch）");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        var newRevision = store.SetFavorite(gameId, favoriteElement.ValueKind == JsonValueKind.True, expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        var updatedGame = store.TryGetGame(gameId)!;
        _events.Publish("game.updated", $"game:{gameId}", new { gameId, revision = newRevision }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, favorite = updatedGame.Favorite, revision = newRevision.Value },
        };
    }

    /// <summary>
    /// games.relink（T17）：把游戏的路径绑定改到新目录——只改数据库，不移动/改名/复制任何文件
    /// （补充规格 1.3）。新路径必须在已注册库根内且当前存在；不可与其他活动游戏绑定冲突。
    /// </summary>
    public Envelope<object> GamesRelink(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 gameId 参数");
        }

        if (!IpcRequests.TryGetStringParameter(request, "newPath", out var newPath))
        {
            return IpcRequests.InvalidArgument(request, "缺少 newPath 参数（绝对本地目录路径）");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        var validation = Domain.Paths.GamePath.TryCreate(newPath);
        if (!validation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = validation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"新路径非法（{validation.Reason}）：{newPath}",
                    Retryable = false,
                },
            };
        }

        var newRoot = validation.Path!;
        if (IpcRequests.RejectPathOutsideRoots(request, newRoot.PhysicalPath, _roots) is { } outsideRoot)
        {
            return outsideRoot;
        }

        if (string.Equals(newRoot.PhysicalPath, game.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return IpcRequests.InvalidArgument(request, $"新路径与当前绑定相同：{newRoot.PhysicalPath}");
        }

        if (!Directory.Exists(newRoot.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"新路径当前不存在；重关联只接受可验证存在的目录：{newRoot.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        var conflicting = store.TryGetGameByRootPath(newRoot.PhysicalPath);
        if (conflicting is not null && !string.Equals(conflicting.GameId, gameId, StringComparison.Ordinal))
        {
            return IpcRequests.InvalidArgument(request, $"新路径已绑定到其他游戏：{conflicting.GameId}");
        }

        var newRevision = store.RelinkGame(gameId, newRoot.PhysicalPath, expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var latest = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {latest?.Revision}",
                    Retryable = false,
                    CurrentRevision = latest?.Revision,
                },
            };
        }

        _events.Publish("game.updated", $"game:{gameId}", new
        {
            gameId,
            rootPath = newRoot.PhysicalPath,
            availability = "available",
            revision = newRevision,
        }, DateTime.UtcNow);

        // relink 后重算指纹（防陈旧指纹污染建议）：入口按旧根内相对位置重映射到新根；
        // 新根不可读 → 指纹为 null 不落写（行保留），下轮 accept/relink 再刷新。
        var newEntry = RebaseEntry(game.RootPath, game.EntryPath, newRoot.PhysicalPath);
        UpsertFingerprint(store, game with { RootPath = newRoot.PhysicalPath }, newEntry, DateTime.UtcNow);

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                gameId,
                previousRootPath = game.RootPath,
                rootPath = newRoot.PhysicalPath,
                availability = "available",
                revision = newRevision.Value,
            },
        };
    }

    /// <summary>计算并落库指纹（handler 内、store 锁外哈希；计算失败静默跳过——指纹是线索不是身份）。</summary>
    private static void UpsertFingerprint(
        SqliteLibraryStore store, GameCard game, string? entryPath, DateTime utcNow)
    {
        var fingerprint = MatchFingerprintCalculator.Calculate(game.RootPath, entryPath, game.Engine);
        if (fingerprint is null)
        {
            return;
        }

        store.UpsertGameFingerprint(game.GameId, new GameFingerprintData(
            fingerprint.StrategyVersion,
            JsonSerializer.Serialize(fingerprint.Entries, ContractJson.Options),
            utcNow));
    }

    /// <summary>relink 的入口重映射：旧根内的入口按相对路径落到新根；不在旧根内则原样返回（按文件名回退由计算器处理）。</summary>
    private static string? RebaseEntry(string oldRootPath, string? entryPath, string newRootPath)
    {
        if (entryPath is null)
        {
            return null;
        }

        try
        {
            var oldRootFull = Path.GetFullPath(oldRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var entryFull = Path.GetFullPath(entryPath);
            var prefix = oldRootFull + Path.DirectorySeparatorChar;
            return entryFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(newRootPath, Path.GetRelativePath(oldRootFull, entryFull)))
                : entryPath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return entryPath;
        }
    }

    private static IReadOnlyList<MatchFingerprint.FingerprintEntry>? TryDeserializeEntries(string entriesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<MatchFingerprint.FingerprintEntry>>(
                entriesJson, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>检测路径或其任一祖先目录是否含重解析点（目录联接/符号链接）。</summary>
    private static bool HasReparseAncestor(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(current, Path.GetPathRoot(current), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return false;
    }

    /// <summary>单游戏充实入口：title/summary/封面/标签/游玩统计逐项查询后组装 DTO。</summary>
    private object GameDto(SqliteLibraryStore store, GameCard game)
    {
        var (title, titleSource) = store.EffectiveField(game.GameId, "title", game.Title);
        var (summary, summarySource) = store.EffectiveField(game.GameId, "summary", "");
        var coverAssetId = store.ListAssets(game.GameId).FirstOrDefault(a => a.IsCurrent)?.AssetId;
        var tags = store.ListGameTags(game.GameId);
        var playtime = store.QueryPlaytimeStats([game.GameId]);
        return GameDto(game, new GameCardEnrichment
        {
            Title = title,
            TitleSource = titleSource,
            Summary = summary ?? "",
            SummarySource = summarySource,
            CoverAssetId = coverAssetId,
            Tags = tags,
        }, playtime.TryGetValue(game.GameId, out var stats) ? stats : null);
    }

    /// <summary>DTO 组装（单游戏与 games.list 批量共用，字段形状只有一个真源）。</summary>
    private object GameDto(GameCard game, GameCardEnrichment enrichment, PlaytimeStats? playtime = null)
    {
        var tags = enrichment.Tags
            .Select(t => new { kind = t.Kind, name = t.Name })
            .ToArray();
        return new
        {
            gameId = game.GameId,
            favorite = game.Favorite,
            title = enrichment.Title ?? "",
            titleSource = enrichment.TitleSource,
            summary = enrichment.Summary,
            summarySource = enrichment.SummarySource,
            coverAssetId = enrichment.CoverAssetId,
            rootPath = game.RootPath,
            kind = game.Kind,
            engine = game.Engine,
            entryPath = game.EntryPath,
            membership = game.Membership,
            availability = game.Availability,
            missingSinceUtc = game.MissingSinceUtc?.ToString("O"),
            playtimeMinutes = playtime?.PlaytimeMinutes ?? 0,
            lastPlayedUtc = playtime?.LastPlayedUtc?.ToString("O"),
            tags,
            revision = game.Revision,
            acceptedUtc = game.AcceptedUtc.ToString("O"),
            updatedUtc = game.UpdatedUtc.ToString("O"),
        };
    }
}
