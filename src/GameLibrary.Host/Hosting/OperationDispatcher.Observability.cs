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



/// <summary>OperationDispatcher 的 Observability 域 handler（阶段三按域拆分，partial）。</summary>
public sealed partial class OperationDispatcher
{
    /// <summary>
    /// diagnostics.cache_rebuild（T27/REC-03）：清理可再生缓存目录中的普通文件，
    /// 不穿越链接/junction，不触碰用户原图（assets/）与游戏目录。
    /// </summary>
    private Envelope<object> CacheRebuild(IpcRequest request)
    {
        var cacheParentDirectory = _state.Library.Store?.ReadSettings().CacheParentDirectory;
        if (cacheParentDirectory is not null && _state.Roots.Contains(cacheParentDirectory))
        {
            return InvalidArgument(request, "当前缓存位置位于已添加的游戏库内，请先在设置中更换位置");
        }

        var cacheDirectory = OwnedPreviewCache.GetRoot(_state.DataDirectory, cacheParentDirectory);
        CacheDirectoryCleaner.Result cleanup;
        try
        {
            var contentDirectory = OwnedPreviewCache.GetValidatedContentDirectoryForCleanup(
                _state.DataDirectory, cacheParentDirectory);
            cleanup = contentDirectory is null
                ? new CacheDirectoryCleaner.Result(0, 0, 0, 0)
                : CacheDirectoryCleaner.Clear(contentDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return InvalidArgument(request, ex.Message);
        }

        var assetsDirectory = Path.Combine(_state.DataDirectory, "assets");
        var userAssets = CacheDirectoryCleaner.CountRegularFiles(assetsDirectory);

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                cacheDirectory,
                removedFiles = cleanup.RemovedFiles,
                removedBytes = cleanup.RemovedBytes,
                skippedLinks = cleanup.SkippedLinks,
                skippedErrors = cleanup.SkippedErrors,
                userAssetFilesUntouched = userAssets,
            },
        };
    }

    /// <summary>备份计划注册表：planId → (backupId, 过期时刻)。10 分钟有效期（契约 9.3）。</summary>
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);

    private sealed class BackupPlan
    {
        public required string BackupId { get; init; }

        public DateTime ExpiresUtc { get; init; }
    }

    private readonly ConcurrentDictionary<string, BackupPlan> _backupPlans = new(StringComparer.Ordinal);

    private string BackupsRoot => Path.Combine(_state.DataDirectory, "backups");

    private ControlAreaStore ControlArea => new(Path.Combine(_state.DataDirectory, "control"));

    /// <summary>诊断状态（T24）：进程/库/审计日志统计与队列指标；不含任何业务数据原文。</summary>
    private Envelope<object> DiagnosticsStatus(IpcRequest request)
    {
        var (currentFile, currentBytes, fileCount) = _state.AuditLog.Describe();
        var library = _state.Library;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                processId = _state.Identity.ProcessId,
                startedAtUtc = _state.Identity.StartedAtUtc.ToString("O"),
                appVersion = _state.Identity.AppVersion,
                apiVersion = ApiConstants.ApiVersion,
                library = new
                {
                    status = library.Status.ToString(),
                    initialized = library.Initialized,
                    schemaVersion = library.SchemaVersion,
                    detail = LogSanitizer.Sanitize(library.Detail, _state.DataDirectory),
                },
                audit = new
                {
                    currentFile,
                    currentBytes,
                    fileCount,
                    maxFileBytes = Observability.AuditLogWriter.DefaultMaxFileBytes,
                    maxFiles = Observability.AuditLogWriter.DefaultMaxFiles,
                    retentionDays = Observability.AuditLogWriter.DefaultRetentionDays,
                },
                jobs = new { activeCount = _state.Jobs.ActiveJobCount() },
                metrics = _state.Metrics.ToDto(),
                eventStream = new
                {
                    occupiedSlots = _state.Events.OccupiedSlots,
                    overflowed = _state.Events.OverflowedCount,
                },
            },
        };
    }

    /// <summary>脱敏审计日志读取（diagnostics.logs）：返回最近 limit 条审计记录。</summary>
    private Envelope<object> DiagnosticsLogs(IpcRequest request)
    {
        var limit = 100;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } logParameters
            && logParameters.TryGetProperty("limit", out var limitElement)
            && limitElement.ValueKind == JsonValueKind.Number
            && limitElement.TryGetInt32(out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 1000);
        }

        var lines = _state.AuditLog.ReadRecentLines(limit);
        var records = new List<object>();
        foreach (var line in lines)
        {
            try
            {
                records.Add(JsonSerializer.Deserialize<JsonElement>(line, ContractJson.Options).Clone());
            }
            catch (JsonException)
            {
                // 单行损坏不阻塞诊断读取。
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = records.Count, items = records },
        };
    }

    /// <summary>
    /// 工具发现（T07/T09，tools.discover）：按 tool 分派——mtool（默认，游戏根配方）、
    /// renpythief（安装指纹与 Guided 计划）、player（常见播放器发现+本地文件参数模板）、
    /// steam（注册表/常见路径发现 + appmanifest 清单）。只读，不自启动任何进程；
    /// 调用方路径仍经库根白名单收口。
    /// </summary>
    private Envelope<object> ToolsDiscover(IpcRequest request)
    {
        TryGetStringParameter(request, "tool", out var discoverTool);
        var hasPath = TryGetStringParameter(request, "path", out var path) && path.Length > 0;

        if (string.Equals(discoverTool, "steam", StringComparison.Ordinal))
        {
            var steam = new Infrastructure.Tools.SteamAdapter().Discover();
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "steam",
                    found = steam.Found,
                    steamRoot = steam.SteamRoot,
                    steamExecutablePath = steam.SteamExecutablePath,
                    manifestMissing = steam.ManifestMissing,
                    capability = steam.Capability,
                    manifests = steam.Manifests.Select(m => new
                    {
                        appId = m.AppId,
                        name = m.Name,
                        installDir = m.InstallDir,
                    }).ToArray(),
                    notice = LogSanitizer.Sanitize(steam.Notice, _state.DataDirectory),
                },
            };
        }

        if (string.Equals(discoverTool, "player", StringComparison.Ordinal))
        {
            var players = new Infrastructure.Tools.PlayerAdapter().Discover();
            string? templateJson = null;
            string? templateTarget = null;
            if (hasPath && File.Exists(path) && players.Count > 0)
            {
                var validation = Domain.Paths.GamePath.TryCreate(path);
                if (validation.IsValid && _state.Roots.Contains(validation.Path!.PhysicalPath))
                {
                    var template = players[0] is { } first
                        ? new Infrastructure.Tools.PlayerAdapter().BuildLaunchTemplate(first, validation.Path.PhysicalPath)
                        : null;
                    if (template is not null)
                    {
                        templateTarget = validation.Path.PhysicalPath;
                        templateJson = JsonSerializer.Serialize(new
                        {
                            player = players[0].Name,
                            executablePath = template.ExecutablePath,
                            argv = template.Arguments,
                            cwd = template.WorkingDirectory,
                            waitForExit = template.WaitForExit,
                        }, ContractJson.Options);
                    }
                }
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "player",
                    players = players.Select(p => new { name = p.Name, executablePath = p.ExecutablePath }).ToArray(),
                    templateFor = templateTarget,
                    template = templateJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(templateJson, ContractJson.Options).Clone(),
                    notice = "参数模板仅覆盖公开稳定行为（打开本地文件）；参数差异须逐播放器以用户样本验证后保存 ExternalPlayer 配置",
                },
            };
        }

        if (!hasPath)
        {
            return InvalidArgument(request, "缺少 path 参数（游戏根的绝对路径）");
        }

        var pathValidation = Domain.Paths.GamePath.TryCreate(path);
        if (!pathValidation.IsValid)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = pathValidation.IsUnsupported ? ErrorCodes.UnsupportedPath : ErrorCodes.InvalidPath,
                    Message = $"路径非法（{pathValidation.Reason}）：{path}",
                    Retryable = false,
                },
            };
        }

        if (RejectPathOutsideRoots(request, pathValidation.Path!.PhysicalPath) is { } discoverOutsideRoot)
        {
            return discoverOutsideRoot;
        }

        if (!Directory.Exists(pathValidation.Path.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"路径不存在或离线：{pathValidation.Path.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        if (string.Equals(discoverTool, "renpythief", StringComparison.Ordinal))
        {
            var renpy = new Infrastructure.Tools.RenpyThiefAdapter().Discover(pathValidation.Path.PhysicalPath);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    toolId = "renpythief",
                    path = pathValidation.Path.PhysicalPath,
                    found = renpy.Found,
                    evidenceKind = "Static",
                    capability = renpy.Capability,
                    fingerprint = renpy.Fingerprint,
                    mainExecutablePath = renpy.MainExecutablePath,
                    launcherPath = renpy.LauncherPath,
                    guidedPlan = renpy.GuidedPlan is null ? null : new
                    {
                        executablePath = renpy.GuidedPlan.ExecutablePath,
                        argv = renpy.GuidedPlan.Arguments,
                        cwd = renpy.GuidedPlan.WorkingDirectory,
                    },
                    notice = LogSanitizer.Sanitize(renpy.Notice, _state.DataDirectory),
                },
            };
        }

        var mtoolDiscovery = new Infrastructure.Tools.MToolAdapter().Discover(pathValidation.Path.PhysicalPath);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                toolId = Infrastructure.Tools.MToolDiscovery.ToolId,
                path = pathValidation.Path.PhysicalPath,
                evidenceKind = mtoolDiscovery.EvidenceKind,
                capability = mtoolDiscovery.Capability,
                recipe = mtoolDiscovery.Recipe is null
                    ? null
                    : new
                    {
                        sourcePath = mtoolDiscovery.Recipe.SourcePath,
                        scriptSha256 = mtoolDiscovery.Recipe.ScriptSha256,
                        isBroken = mtoolDiscovery.Recipe.IsBroken,
                        brokenPaths = mtoolDiscovery.Recipe.BrokenPaths,
                        referencedFiles = mtoolDiscovery.Recipe.ReferencedFiles,
                        steps = mtoolDiscovery.Recipe.Steps.Select(step => new
                        {
                            sequence = step.Sequence,
                            executablePath = step.ExecutablePath,
                            argv = step.Arguments,
                            cwd = step.WorkingDirectory,
                            waitForExit = step.WaitForExit,
                        }).ToArray(),
                    },
                unsupportedReason = mtoolDiscovery.UnsupportedReason,
                notice = LogSanitizer.Sanitize(mtoolDiscovery.Notice, _state.DataDirectory),
            },
        };
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private const long MaxAssetBytes = 1 * 1024 * 1024;
}
