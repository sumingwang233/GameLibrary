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
using GameLibrary.Infrastructure.Shell;
namespace GameLibrary.Host.Hosting;



/// <summary>OperationDispatcher 的 Cataloging 域 handler（阶段三按域拆分，partial）。</summary>
public sealed partial class OperationDispatcher
{
    /// <summary>手动建卡：支持未被扫描器识别的目录和独立 EXE/SWF；不猜测启动方式。</summary>
    private Envelope<object> GamesCreate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "sourcePath", out var sourcePath))
        {
            return InvalidArgument(request, "缺少 sourcePath（游戏目录或独立 EXE/SWF 的绝对路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(sourcePath);
        if (!validation.IsValid)
        {
            return InvalidArgument(request, $"游戏路径非法（{validation.Reason}）：{sourcePath}");
        }

        var normalized = validation.Path!;
        if (RejectPathOutsideRoots(request, normalized.PhysicalPath) is { } outsideRoot)
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
            return InvalidArgument(request, "独立文件仅支持 EXE、SWF 或 Windows 快捷方式（LNK）");
        }

        try
        {
            if (HasReparseAncestor(normalized.PhysicalPath))
            {
                return InvalidArgument(request, "所选路径包含目录联接或符号链接，请选择真实游戏位置");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InvalidArgument(request, $"无法验证游戏路径：{ex.Message}");
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
            var resolution = new LnkResolver(_state.Roots.List().Select(root => root.Path))
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
                return InvalidArgument(request, "快捷方式目标不是受支持的本地绝对路径");
            }

            entryPath = targetValidation.Path!.PhysicalPath;
            var targetExtension = Path.GetExtension(entryPath);
            if (!targetExtension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !targetExtension.Equals(".swf", StringComparison.OrdinalIgnoreCase))
            {
                return InvalidArgument(request, "快捷方式目标只能是 EXE 或 SWF 游戏文件");
            }

            if (RejectPathOutsideRoots(request, entryPath) is { } targetOutsideRoot)
            {
                return targetOutsideRoot;
            }

            try
            {
                if (HasReparseAncestor(entryPath))
                {
                    return InvalidArgument(request, "快捷方式目标包含目录联接或符号链接");
                }

                if (resolution.Info.Arguments.Length >= 1023)
                {
                    return InvalidArgument(request, "快捷方式参数可能超出解析缓冲区，不能安全导入");
                }

                arguments = WindowsCommandLine.ParseArguments(resolution.Info.Arguments);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return InvalidArgument(request, $"快捷方式无法安全解析：{ex.Message}");
            }

            if (Path.GetExtension(entryPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = entryPath;
                workingDirectory = string.IsNullOrWhiteSpace(resolution.Info.WorkingDirectory)
                    ? Path.GetDirectoryName(entryPath)
                    : resolution.Info.WorkingDirectory;
                if (workingDirectory is null || !Directory.Exists(workingDirectory))
                {
                    return InvalidArgument(request, "快捷方式的工作目录不存在");
                }

                if (RejectPathOutsideRoots(request, workingDirectory) is { } workingOutsideRoot)
                {
                    return workingOutsideRoot;
                }

                try
                {
                    if (HasReparseAncestor(workingDirectory))
                    {
                        return InvalidArgument(request, "快捷方式的工作目录包含目录联接或符号链接");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return InvalidArgument(request, $"无法验证快捷方式的工作目录：{ex.Message}");
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
            return InvalidArgument(request, $"该游戏位置已入库：{existing.GameId}");
        }

        string title;
        JsonElement titleElement = default;
        var hasTitle = request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty("title", out titleElement);
        if (hasTitle)
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                return InvalidArgument(request, "title 必须是字符串");
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
            return InvalidArgument(request, "游戏标题必须是 1–200 字符且不含控制字符");
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

        _state.Events.Publish("game.created", $"game:{game.GameId}", new
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

    private Envelope<object> GamesRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetIntParameter(request, "expectedRevision", out var expectedRevision)
            || expectedRevision is null)
        {
            return InvalidArgument(request, "games.remove 需要 gameId、expectedRevision 参数");
        }

        var current = store.TryGetGame(gameId);
        if (current is null)
        {
            return NotFound(request, $"游戏不存在：{gameId}");
        }

        if (current.Membership != "active")
        {
            return InvalidArgument(request, $"游戏已从库中移除：{gameId}");
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

        _state.Events.Publish("game.removed", $"game:{gameId}", new { gameId, ignoreId = ignore.IgnoreId }, utcNow);
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
                filesDeleted = false,
            },
        };
    }

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

    private object GameDto(SqliteLibraryStore store, GameCard game)
    {
        var (title, titleSource) = store.EffectiveField(game.GameId, "title", game.Title);
        var (summary, summarySource) = store.EffectiveField(game.GameId, "summary", "");
        var coverAssetId = store.ListAssets(game.GameId).FirstOrDefault(a => a.IsCurrent)?.AssetId;
        var tags = store.ListGameTags(game.GameId);
        return GameDto(game, new GameCardEnrichment
        {
            Title = title,
            TitleSource = titleSource,
            Summary = summary ?? "",
            SummarySource = summarySource,
            CoverAssetId = coverAssetId,
            Tags = tags,
        });
    }

    /// <summary>DTO 组装（单游戏与 games.list 批量共用，字段形状只有一个真源）。</summary>
    private object GameDto(GameCard game, GameCardEnrichment enrichment)
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
            tags,
            revision = game.Revision,
            acceptedUtc = game.AcceptedUtc.ToString("O"),
            updatedUtc = game.UpdatedUtc.ToString("O"),
        };
    }

    private static string AutoTitle(GameCard game) =>
        game.Kind is "manualFile" or "manualShortcut"
            ? Path.GetFileNameWithoutExtension(game.RootPath)
            : Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar)) ?? game.Title;

    // T-collections tags.×8（阶段三）：标签按 (类型, 规范值) 唯一——engine 标签由引擎识别
    // 在入库时自动创建，重扫只替换同源记录；user 标签由用户维护。删除/解除 engine 标签
    // 登记 Suppress 覆盖（阻止扫描恢复）；tags.reset 清除覆盖并按当前引擎恢复。

    private Envelope<object> IgnoresList(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var rules = store.ListIgnoreRules();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = rules.Count, items = rules.Select(IgnoreDto).ToArray() },
        };
    }

    private Envelope<object> IgnoresCreate(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "scope", out var scope)
            || scope is not ("ExactPath" or "Subtree" or "ConfirmedIdentity"))
        {
            return InvalidArgument(request, "缺少 scope 参数（ExactPath/Subtree/ConfirmedIdentity）");
        }

        TryGetStringParameter(request, "path", out var path);
        TryGetStringParameter(request, "gameId", out var gameId);
        TryGetStringParameter(request, "reason", out var reason);
        if (scope != "ConfirmedIdentity" && path.Length == 0)
        {
            return InvalidArgument(request, $"{scope} 需要 path 参数（规范化绝对路径）");
        }

        if (scope == "ConfirmedIdentity" && gameId.Length == 0)
        {
            return InvalidArgument(request, "ConfirmedIdentity 需要用户确认的 gameId");
        }

        if (path.Length > 0 && !_state.Roots.Contains(path))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.PermissionDenied,
                    Message = $"路径不在已注册库根内：{path}",
                    Retryable = false,
                },
            };
        }

        var rule = new IgnoreRule
        {
            IgnoreId = $"ignore-{Guid.NewGuid():N}",
            Scope = scope,
            Path = path.Length > 0 ? path : null,
            GameId = gameId.Length > 0 ? gameId : null,
            Reason = reason.Length > 0 ? reason : null,
            CreatedUtc = DateTime.UtcNow,
        };
        store.InsertIgnoreRule(rule);

        // 抑制立即生效：撤销前匹配的待审核候选转入 ignored（幂等补登记，不覆盖已有终态）。
        var suppressed = 0;
        foreach (var candidate in store.ListCandidates())
        {
            if (candidate.ReviewState is "observed" or "stabilizing" or "pendingReview"
                && store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, candidate.GameId))
            {
                var transitioned = store.TransitionCandidate(
                    candidate.CandidateId, candidate.ReviewState, "ignored", candidate.Revision, null, DateTime.UtcNow);
                if (transitioned is not null)
                {
                    suppressed++;
                }
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { ignoreId = rule.IgnoreId, scope, suppressedCandidates = suppressed },
        };
    }

    /// <summary>撤销忽略（恢复候选提示的唯一途径）：匹配的 ignored 候选回到 Observed（状态机 Ignored→Observed）。</summary>
    private Envelope<object> IgnoresRemove(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!TryGetStringParameter(request, "ignoreId", out var ignoreId))
        {
            return InvalidArgument(request, "缺少 ignoreId 参数");
        }

        var rule = store.ListIgnoreRules()
            .FirstOrDefault(r => string.Equals(r.IgnoreId, ignoreId, StringComparison.Ordinal));
        if (rule is null)
        {
            return NotFound(request, $"忽略规则不存在：{ignoreId}");
        }

        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
        if (expectedRevision is not null && expectedRevision.Value != rule.Revision)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"忽略规则 Revision 不一致：期望 {expectedRevision}，当前 {rule.Revision}",
                    Retryable = false,
                },
            };
        }

        var coveredPaths = store.RemoveIgnoreRule(ignoreId);
        var restored = 0;
        foreach (var candidate in store.ListCandidates())
        {
            if (candidate.ReviewState != "ignored")
            {
                continue;
            }

            var candidatePath = candidate.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar);
            var covered = coveredPaths.Any(p =>
                string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), candidatePath, StringComparison.OrdinalIgnoreCase));
            if (!covered && rule.Scope == "Subtree" && rule.Path is not null)
            {
                var rulePath = rule.Path.TrimEnd(Path.DirectorySeparatorChar);
                covered = candidatePath.StartsWith(rulePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            if (covered
                && !store.IsSuppressedByIgnoreRule(candidate.PhysicalPath, candidate.GameId))
            {
                var transitioned = store.TransitionCandidate(
                    candidate.CandidateId, "ignored", "observed", candidate.Revision, null, DateTime.UtcNow);
                if (transitioned is not null)
                {
                    restored++;
                }
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { ignoreId, removed = true, restoredCandidates = restored },
        };
    }

    private static object IgnoreDto(IgnoreRule rule) => new
    {
        ignoreId = rule.IgnoreId,
        scope = rule.Scope,
        path = rule.Path,
        gameId = rule.GameId,
        reason = rule.Reason,
        revision = rule.Revision,
        createdUtc = rule.CreatedUtc.ToString("O"),
    };
}
