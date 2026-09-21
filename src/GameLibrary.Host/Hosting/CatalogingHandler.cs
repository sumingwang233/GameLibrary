using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.Host.Tools;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 编目域处理器：roots.add / roots.list / roots.remove 三操作 + candidates.list /
/// candidates.get 两操作 + fields.set / fields.clear / fields.reset 三操作 +
/// assets.import / list / get / choose / crop / reset / remove 七操作 +
/// metadata.preview / metadata.refresh 两操作 + events.read 一操作，共十八臂
/// （私有助手 FieldRevisionResult / AssetDto / AutoTitle 与静态成员
/// ImageExtensions / MaxAssetBytes 随域整体迁入）。Store 经委托每请求取当前值
/// （library.init / backups.restore 会整体替换 Library，禁止构造时缓存 store
/// 引用）；Roots/Events/Candidates/Jobs 为 init-only 引用（HostRuntimeState
/// 构造后整体不可替换；换库由 EventStream.BindStore 在其内部重绑）。
/// 由 DispatchCore 调用，天然继承幂等收据（fields.set/clear/reset、
/// assets.import/choose/crop/reset/remove、metadata.refresh、roots.remove 在
/// ReceiptOperations）与串行门、权限、维护模式等中间件；域内仅 roots.remove
/// 发布 root.removed 事件。
/// </summary>
internal sealed class CatalogingHandler
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private const long MaxAssetBytes = 5 * 1024 * 1024;

    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly RootRegistry _roots;
    private readonly EventStream _events;
    private readonly CandidateRegistry _candidates;
    private readonly JobManager _jobs;
    private readonly string _dataDirectory;

    /// <summary>
    /// roots/events/candidates/jobs 以 init-only 引用直传：HostRuntimeState 构造后
    /// 整体不可替换（HostRuntime.cs）；Store 经委托每请求取当前值。
    /// </summary>
    public CatalogingHandler(
        Func<SqliteLibraryStore?> storeAccessor,
        RootRegistry roots,
        EventStream events,
        CandidateRegistry candidates,
        JobManager jobs,
        string dataDirectory)
    {
        _storeAccessor = storeAccessor;
        _roots = roots;
        _events = events;
        _candidates = candidates;
        _jobs = jobs;
        _dataDirectory = dataDirectory;
    }

    /// <summary>注册库根（显式授权动作）；重复注册同一规范化路径幂等；同步落库（v13+）。</summary>
    public Envelope<object> RootsAdd(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "root", out var root))
        {
            return IpcRequests.InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        try
        {
            var libraryRoot = _roots.Add(root);
            _storeAccessor()?.UpsertRoot(
                new PersistedRoot(libraryRoot.RootId, libraryRoot.Path.PhysicalPath, libraryRoot.Revision, libraryRoot.CreatedUtc),
                DateTime.UtcNow);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = libraryRoot.ToDto(),
            };
        }
        catch (Scanning.RootRegistryException ex)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ex.Code,
                    Message = ex.Message,
                    Retryable = false,
                },
            };
        }
    }

    /// <summary>
    /// 移除库根并清空不再由其他根覆盖的游戏和候选；不触碰磁盘文件。
    /// </summary>
    public Envelope<object> RootsRemove(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "rootId", out var rootId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 rootId 参数");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        var root = _roots.List().FirstOrDefault(r => string.Equals(r.RootId, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            return IpcRequests.NotFound(request, $"库根不存在：{rootId}");
        }

        if (root.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"库根 Revision 不一致：期望 {expectedRevision}，当前 {root.Revision}",
                    Retryable = false,
                    CurrentRevision = root.Revision,
                },
            };
        }

        var removedGames = _storeAccessor()?.RemoveRootGames(rootId, root.Path.PhysicalPath, DateTime.UtcNow) ?? 0;
        var removed = _roots.Remove(rootId);
        if (removed is null)
        {
            return IpcRequests.NotFound(request, $"库根不存在：{rootId}");
        }

        _events.Publish("root.removed", $"root:{rootId}", new { rootId, removedGames }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { rootId, removed = true, removedGames },
        };
    }

    /// <summary>roots.list：列出全部已注册库根。</summary>
    public Envelope<object> RootsList(IpcRequest request)
    {
        var roots = _roots.List();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = roots.Count,
                items = roots.Select(r => r.ToDto()).ToArray(),
            },
        };
    }

    /// <summary>候选列表：有库走持久层查询，无库回退内存注册表（T11 双路径）。</summary>
    public Envelope<object> CandidatesList(IpcRequest request)
    {
        string? jobId = null;
        string? state = null;
        var limit = 0;
        var offset = 0;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } listParameters)
        {
            if (listParameters.TryGetProperty("jobId", out var jobElement)
                && jobElement.ValueKind == JsonValueKind.String)
            {
                jobId = jobElement.GetString();
            }

            if (listParameters.TryGetProperty("state", out var stateElement)
                && stateElement.ValueKind == JsonValueKind.String)
            {
                state = stateElement.GetString();
            }

            if (listParameters.TryGetProperty("limit", out var limitElement)
                && limitElement.ValueKind == JsonValueKind.Number
                && limitElement.TryGetInt32(out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 1000);
                if (listParameters.TryGetProperty("offset", out var offsetElement)
                    && offsetElement.ValueKind == JsonValueKind.Number
                    && offsetElement.TryGetInt32(out var parsedOffset))
                {
                    offset = Math.Max(0, parsedOffset);
                }
            }
        }

        // T11 起以库内候选为事实来源（重扫刷新、审核状态演进）；无库时退回内存注册表。
        var store = _storeAccessor();
        if (store is not null)
        {
            var (total, persisted) = store.QueryCandidates(jobId, state, limit, offset);
            var items = persisted
                .Select(c => new
                {
                    candidateId = c.CandidateId,
                    jobId = c.JobId,
                    kind = c.Kind,
                    relativePath = c.RelativePath,
                    physicalPath = c.PhysicalPath,
                    reviewState = c.ReviewState,
                    revision = c.Revision,
                    gameId = c.GameId,
                    observedUtc = c.ObservedUtc.ToString("O"),
                })
                .ToArray();
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new { total, items },
            };
        }

        var filteredCandidates = _candidates.List(jobId)
            .Where(candidate => state is null
                || string.Equals(candidate.ReviewState.ToString(), state, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var candidates = limit > 0
            ? filteredCandidates.Skip(offset).Take(limit).ToArray()
            : filteredCandidates;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = filteredCandidates.Length,
                items = candidates.Select(c => c.ToListItem()).ToArray(),
            },
        };
    }

    /// <summary>候选详情：有库走持久层，无库回退内存注册表（T11 双路径）。</summary>
    public Envelope<object> CandidatesGet(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "candidateId", out var candidateId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 candidateId 参数");
        }

        var store = _storeAccessor();
        if (store is not null)
        {
            var persisted = store.TryGetCandidate(candidateId);
            if (persisted is null)
            {
                return IpcRequests.NotFound(request, $"候选不存在：{candidateId}");
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    candidateId = persisted.CandidateId,
                    jobId = persisted.JobId,
                    kind = persisted.Kind,
                    relativePath = persisted.RelativePath,
                    physicalPath = persisted.PhysicalPath,
                    reviewState = persisted.ReviewState,
                    revision = persisted.Revision,
                    gameId = persisted.GameId,
                    detail = JsonSerializer.Deserialize<JsonElement>(persisted.PayloadJson, ContractJson.Options).Clone(),
                    observedUtc = persisted.ObservedUtc.ToString("O"),
                    updatedUtc = persisted.UpdatedUtc.ToString("O"),
                },
            };
        }

        var candidate = _candidates.Get(candidateId);
        if (candidate is null)
        {
            return IpcRequests.NotFound(request, $"候选不存在：{candidateId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = candidate.ToDetail(),
        };
    }

    /// <summary>资料字段设置（T14，fields.set）：Revision 即游戏卡片 Revision；title 变更镜像到 games 列表。</summary>
    public Envelope<object> FieldsSet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "field", out var field)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "fields.set 需要 gameId、field、expectedRevision 参数");
        }

        if (field is not ("title" or "summary"))
        {
            return IpcRequests.InvalidArgument(request, $"不支持的字段：{field}（当前支持 title、summary）");
        }

        string? value = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } fsParameters
            && fsParameters.TryGetProperty("value", out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String)
        {
            value = valueElement.GetString();
        }

        var newRevision = store.SetGameField(gameId, field, value, "user", expectedRevision.Value, DateTime.UtcNow);
        if (newRevision is null)
        {
            var card = store.TryGetGame(gameId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = card is null ? ErrorCodes.NotFound : ErrorCodes.RevisionConflict,
                    Message = card is null ? $"游戏不存在：{gameId}" : $"Revision 不一致：期望 {expectedRevision}，当前 {card.Revision}",
                    Retryable = false,
                },
            };
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, field, value, source = "user", revision = newRevision },
        };
    }

    /// <summary>
    /// 用户主动清空（fields.clear）：字段层 value=null（≠继承自动值）；title 镜像为空串。
    /// </summary>
    public Envelope<object> FieldsClear(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "field", out var field)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return IpcRequests.InvalidArgument(request, "fields.clear 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var newRevision = store.SetGameField(gameId, field, null, "user", expectedRevision.Value, DateTime.UtcNow);
        return FieldRevisionResult(request, gameId, field, newRevision, "user");
    }

    /// <summary>恢复自动值（fields.reset）：删除用户层；title 回退自动层值（首次覆盖前自动层已登记）。</summary>
    public Envelope<object> FieldsReset(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "field", out var field)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return IpcRequests.InvalidArgument(request, "fields.reset 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        var fallback = field == "title"
            ? Path.GetFileName(card.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? ""
            : "";
        var newRevision = store.ResetGameField(gameId, field, fallback, expectedRevision.Value, DateTime.UtcNow);
        return FieldRevisionResult(request, gameId, field, newRevision, "auto");
    }

    private static Envelope<object> FieldRevisionResult(IpcRequest request, string gameId, string field, int? newRevision, string source)
    {
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
                    Message = "Revision 不一致（游戏卡片可能已被其他入口修改）",
                    Retryable = false,
                },
            };
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, field, source, revision = newRevision },
        };
    }

    /// <summary>封面导入（T14，assets.import）：用户图片复制入应用自有目录，不反写游戏目录。</summary>
    public Envelope<object> AssetsImport(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "sourcePath", out var sourcePath))
        {
            return IpcRequests.InvalidArgument(request, "assets.import 需要 gameId、sourcePath 参数");
        }

        if (store.TryGetGame(gameId) is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
        {
            return IpcRequests.InvalidArgument(request, $"不支持的图片格式：{extension}（支持 {string.Join("/", ImageExtensions)}）");
        }

        if (!File.Exists(sourcePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.NotFound,
                    Message = $"源图片不存在：{sourcePath}",
                    Retryable = false,
                },
            };
        }

        if (new FileInfo(sourcePath).Length > MaxAssetBytes)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ResourceTooLarge,
                    Message = $"图片超过 5 MiB 上限：{sourcePath}",
                    Retryable = false,
                },
            };
        }

        var assetDirectory = Path.Combine(_dataDirectory, "assets", gameId);
        Directory.CreateDirectory(assetDirectory);
        var importedPath = Path.Combine(assetDirectory, $"{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, importedPath, overwrite: false);

        var asset = store.ImportAsset(gameId, importedPath, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = AssetDto(asset),
        };
    }

    /// <summary>资产列表：按 gameId 列出全部资产（AssetDto 字段形状单源）。</summary>
    public Envelope<object> AssetsList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var listGameId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 gameId 参数");
        }

        var assets = store.ListAssets(listGameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = assets.Count, items = assets.Select(AssetDto).ToArray() },
        };
    }

    /// <summary>资产读取：原图最多 5 MiB，优先返回缓存预览；缓存不可用时返回原图。</summary>
    public Envelope<object> AssetsGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "assetId", out var assetId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 assetId 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return IpcRequests.NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return IpcRequests.NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        if (new FileInfo(asset.FilePath).Length > MaxAssetBytes)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ResourceTooLarge,
                    Message = "资产超过 5 MiB 上限",
                    Retryable = false,
                },
            };
        }

        var bytes = File.ReadAllBytes(asset.FilePath);
        var mimeType = asset.FilePath switch
        {
            var p when p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
            var p when p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) => "image/gif",
            var p when p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) => "image/webp",
            _ => "image/jpeg",
        };
        var settings = store.ReadSettings();
        var preview = settings.CacheParentDirectory is not null
            && _roots.Contains(settings.CacheParentDirectory)
                ? new OwnedPreviewCache.Preview(bytes, mimeType)
                : OwnedPreviewCache.GetOrCreate(
                    _dataDirectory,
                    settings.CacheParentDirectory,
                    asset.AssetId,
                    asset.FilePath,
                    bytes,
                    mimeType);

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                assetId = asset.AssetId,
                gameId = asset.GameId,
                kind = asset.Kind,
                isCurrent = asset.IsCurrent,
                mimeType = preview.MimeType,
                sizeBytes = preview.Bytes.LongLength,
                // byte[] 由 JSON 序列化器直接写为 Base64，避免 '+' 的额外转义膨胀。
                dataBase64 = preview.Bytes,
            },
        };
    }

    /// <summary>选择候选封面（assets.choose）：校验游戏 Revision；封面切换不递增卡片 Revision。</summary>
    public Envelope<object> AssetsChoose(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "assetId", out var assetId)
            || !IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "assets.choose 需要 gameId、assetId、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        if (card.Revision != expectedRevision.Value)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"Revision 不一致：期望 {expectedRevision}，当前 {card.Revision}",
                    Retryable = false,
                },
            };
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null || !string.Equals(asset.GameId, gameId, StringComparison.Ordinal))
        {
            return IpcRequests.NotFound(request, $"资产不存在或不属于该游戏：{assetId}");
        }

        store.ChooseAsset(gameId, assetId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, assetId, isCurrent = true },
        };
    }

    /// <summary>裁切封面（assets.crop）：真实像素裁切，产出新资产并设为当前封面。</summary>
    public Envelope<object> AssetsCrop(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "assetId", out var assetId)
            || !IpcRequests.TryGetIntParameter(request, "x", out var x) || x is null
            || !IpcRequests.TryGetIntParameter(request, "y", out var y) || y is null
            || !IpcRequests.TryGetIntParameter(request, "width", out var width) || width is null
            || !IpcRequests.TryGetIntParameter(request, "height", out var height) || height is null)
        {
            return IpcRequests.InvalidArgument(request, "assets.crop 需要 assetId、x、y、width、height 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return IpcRequests.NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return IpcRequests.NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        try
        {
            var destDirectory = Path.Combine(_dataDirectory, "assets", asset.GameId);
            var croppedPath = ImageCropper.Crop(
                asset.FilePath, destDirectory, x.Value, y.Value, width.Value, height.Value);
            var newAsset = store.ImportAsset(asset.GameId, croppedPath, DateTime.UtcNow);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    sourceAssetId = asset.AssetId,
                    newAssetId = newAsset.AssetId,
                    isCurrent = true,
                    x = x.Value,
                    y = y.Value,
                    width = width.Value,
                    height = height.Value,
                },
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException
            or InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return IpcRequests.InvalidArgument(request, $"裁切失败：{ex.Message}");
        }
    }

    /// <summary>重置封面（assets.reset）：全部封面置为非当前，游戏回到无封面展示。</summary>
    public Envelope<object> AssetsReset(IpcRequest request)
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

        var previous = store.ResetCover(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { gameId, previousAssetId = previous, isCurrent = false },
        };
    }

    /// <summary>移除资产（assets.remove）：仅限应用自有且非当前引用的资源。</summary>
    public Envelope<object> AssetsRemove(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "assetId", out var assetId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 assetId 参数");
        }

        var removedPath = store.RemoveAsset(assetId);
        if (removedPath is null)
        {
            var asset = store.TryGetAsset(assetId);
            return asset is null
                ? IpcRequests.NotFound(request, $"资产不存在：{assetId}")
                : IpcRequests.InvalidArgument(request, "当前封面不可移除；先 choose 其他封面或 reset");
        }

        try
        {
            File.Delete(removedPath);
        }
        catch (IOException)
        {
            // 行已删；文件残留不阻塞（仅应用自有副本）。
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { assetId, removed = true },
        };
    }

    /// <summary>
    /// 元数据建议预览（metadata.preview）：仅本地证据——自动标题（根目录名）与
    /// 引擎/入口描述；无在线元数据源，如实标注。
    /// </summary>
    public Envelope<object> MetadataPreview(IpcRequest request)
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

        var autoTitle = AutoTitle(game);
        var autoSummary = $"自动识别：引擎 {game.Engine ?? "未识别"}，入口 {game.EntryPath ?? "未确定"}。";

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                gameId,
                note = "仅本地证据建议；无在线元数据源，建议一律 source=auto，refresh 只更新 AutoValue 不覆盖用户层",
                suggestions = new object[]
                {
                    new { field = "title", value = autoTitle, source = "auto", evidence = "安装根目录名（目录名仅 contextual）" },
                    new { field = "summary", value = autoSummary, source = "auto", evidence = "本地检测证据（引擎/入口）" },
                },
            },
        };
    }

    /// <summary>元数据刷新（metadata.refresh）：作业式更新 AutoValue，不覆盖用户层。</summary>
    public Envelope<object> MetadataRefresh(IpcRequest request)
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

        var autoTitle = AutoTitle(game);
        var autoSummary = $"自动识别：引擎 {game.Engine ?? "未识别"}，入口 {game.EntryPath ?? "未确定"}。";

        var jobId = _jobs.Create("metadata-refresh", context =>
        {
            store.WriteAutoField(gameId, "title", autoTitle, DateTime.UtcNow);
            store.WriteAutoField(gameId, "summary", autoSummary, DateTime.UtcNow);
            context.ReportProgress(new { gameId, updatedFields = new[] { "title", "summary" } });
            return Task.FromResult(JobOutcome.Succeeded());
        });

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, gameId, state = "running" },
        };
    }

    /// <summary>事件增量读取（T16，events.read）：游标不跨重启；过期返回 CursorExpired。</summary>
    public Envelope<object> EventsRead(IpcRequest request)
    {
        long? cursor = null;
        int limit = 100;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } erParameters)
        {
            if (erParameters.TryGetProperty("cursor", out var cursorElement)
                && cursorElement.ValueKind == JsonValueKind.Number
                && cursorElement.TryGetInt64(out var parsedCursor))
            {
                cursor = parsedCursor;
            }

            if (erParameters.TryGetProperty("limit", out var limitElement)
                && limitElement.ValueKind == JsonValueKind.Number
                && limitElement.TryGetInt32(out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 4096);
            }
        }

        var events = _events.ReadAfter(cursor, limit);
        if (events is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.CursorExpired,
                    Message = "事件游标已过期（宿主重启或事件已被淘汰）；请不带 cursor 重新全量读取",
                    Retryable = false,
                },
            };
        }

        var nextCursor = events.Count > 0 ? events[events.Count - 1].Sequence : cursor ?? 0;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                nextCursor,
                latestCursor = _events.LatestSequence,
                items = events.Select(ev => new
                {
                    sequence = ev.Sequence,
                    timestampUtc = ev.TimestampUtc.ToString("O"),
                    type = ev.Type,
                    entityKey = ev.EntityKey,
                    payload = JsonSerializer.Deserialize<JsonElement>(ev.PayloadJson, ContractJson.Options).Clone(),
                }).ToArray(),
            },
        };
    }

    private static object AssetDto(GameAsset asset) => new
    {
        assetId = asset.AssetId,
        gameId = asset.GameId,
        kind = asset.Kind,
        isCurrent = asset.IsCurrent,
        importedUtc = asset.ImportedUtc.ToString("O"),
    };

    private static string AutoTitle(GameCard game) =>
        game.Kind is "manualFile" or "manualShortcut"
            ? Path.GetFileNameWithoutExtension(game.RootPath)
            : Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? game.Title;
}
