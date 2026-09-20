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



/// <summary>
/// OperationDispatcher 的 Cataloging 域 handler（阶段三按域拆分，partial）：
/// 资料字段（fields.*）/资产（assets.*）/元数据（metadata.*）/事件读取（events.read）；
/// games 游戏卡域（含 GameDto 字段形状真源）已独立为 GamesHandler。
/// </summary>
public sealed partial class OperationDispatcher
{
    /// <summary>资料字段设置（T14，fields.set）：Revision 即游戏卡片 Revision；title 变更镜像到 games 列表。</summary>
    private Envelope<object> FieldsSet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "fields.set 需要 gameId、field、expectedRevision 参数");
        }

        if (field is not ("title" or "summary"))
        {
            return InvalidArgument(request, $"不支持的字段：{field}（当前支持 title、summary）");
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

    /// <summary>封面导入（T14，assets.import）：用户图片复制入应用自有目录，不反写游戏目录。</summary>
    private Envelope<object> AssetsImport(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "sourcePath", out var sourcePath))
        {
            return InvalidArgument(request, "assets.import 需要 gameId、sourcePath 参数");
        }

        if (store.TryGetGame(gameId) is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
        {
            return InvalidArgument(request, $"不支持的图片格式：{extension}（支持 {string.Join("/", ImageExtensions)}）");
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
                    Message = $"图片超过 1 MiB 上限：{sourcePath}",
                    Retryable = false,
                },
            };
        }

        var assetDirectory = Path.Combine(_state.DataDirectory, "assets", gameId);
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

    private Envelope<object> AssetsList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var listGameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
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

    /// <summary>资产读取（契约 5.x）：受限预览 ≤1 MiB，base64 返回。</summary>
    private Envelope<object> AssetsGet(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId))
        {
            return InvalidArgument(request, "缺少 assetId 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        var bytes = File.ReadAllBytes(asset.FilePath);
        if (bytes.Length > MaxAssetBytes)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ResourceTooLarge,
                    Message = "资产超过 1 MiB 预览上限",
                    Retryable = false,
                },
            };
        }

        var mimeType = asset.FilePath switch
        {
            var p when p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
            var p when p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) => "image/gif",
            var p when p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) => "image/webp",
            _ => "image/jpeg",
        };
        var settings = store.ReadSettings();
        var preview = settings.CacheParentDirectory is not null
            && _state.Roots.Contains(settings.CacheParentDirectory)
                ? new OwnedPreviewCache.Preview(bytes, mimeType)
                : OwnedPreviewCache.GetOrCreate(
                    _state.DataDirectory,
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
                dataBase64 = Convert.ToBase64String(preview.Bytes),
            },
        };
    }

    /// <summary>
    /// 用户主动清空（fields.clear）：字段层 value=null（≠继承自动值）；title 镜像为空串。
    /// </summary>
    private Envelope<object> FieldsClear(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return InvalidArgument(request, "fields.clear 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var newRevision = store.SetGameField(gameId, field, null, "user", expectedRevision.Value, DateTime.UtcNow);
        return FieldRevisionResult(request, gameId, field, newRevision, "user");
    }

    /// <summary>恢复自动值（fields.reset）：删除用户层；title 回退自动层值（首次覆盖前自动层已登记）。</summary>
    private Envelope<object> FieldsReset(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "field", out var field)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null
            || field is not ("title" or "summary"))
        {
            return InvalidArgument(request, "fields.reset 需要 gameId、field（title/summary）、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
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

    /// <summary>选择候选封面（assets.choose）：校验游戏 Revision；封面切换不递增卡片 Revision。</summary>
    private Envelope<object> AssetsChoose(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "assetId", out var assetId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "assets.choose 需要 gameId、assetId、expectedRevision 参数");
        }

        var card = store.TryGetGame(gameId);
        if (card is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
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
            return NotFound(request, $"资产不存在或不属于该游戏：{assetId}");
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
    private Envelope<object> AssetsCrop(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId)
            || !TryGetIntParameter(request, "x", out var x) || x is null
            || !TryGetIntParameter(request, "y", out var y) || y is null
            || !TryGetIntParameter(request, "width", out var width) || width is null
            || !TryGetIntParameter(request, "height", out var height) || height is null)
        {
            return InvalidArgument(request, "assets.crop 需要 assetId、x、y、width、height 参数");
        }

        var asset = store.TryGetAsset(assetId);
        if (asset is null)
        {
            return NotFound(request, $"资产不存在：{assetId}");
        }

        if (!File.Exists(asset.FilePath))
        {
            return NotFound(request, $"资产文件缺失：{asset.FilePath}");
        }

        try
        {
            var destDirectory = Path.Combine(_state.DataDirectory, "assets", asset.GameId);
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
            return InvalidArgument(request, $"裁切失败：{ex.Message}");
        }
    }

    /// <summary>重置封面（assets.reset）：全部封面置为非当前，游戏回到无封面展示。</summary>
    private Envelope<object> AssetsReset(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
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
    private Envelope<object> AssetsRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "assetId", out var assetId))
        {
            return InvalidArgument(request, "缺少 assetId 参数");
        }

        var removedPath = store.RemoveAsset(assetId);
        if (removedPath is null)
        {
            var asset = store.TryGetAsset(assetId);
            return asset is null
                ? NotFound(request, $"资产不存在：{assetId}")
                : InvalidArgument(request, "当前封面不可移除；先 choose 其他封面或 reset");
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
    private Envelope<object> MetadataPreview(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
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
    private Envelope<object> MetadataRefresh(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId))
        {
            return InvalidArgument(request, "缺少 gameId 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        var autoTitle = AutoTitle(game);
        var autoSummary = $"自动识别：引擎 {game.Engine ?? "未识别"}，入口 {game.EntryPath ?? "未确定"}。";

        var jobId = _state.Jobs.Create("metadata-refresh", context =>
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
    private Envelope<object> EventsRead(IpcRequest request)
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

        var events = _state.Events.ReadAfter(cursor, limit);
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

    /// <summary>开始一次工具验证（T08）：绑定当前指纹与隔离样本，状态 Unknown。</summary>

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
