using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Observability;
using GameLibrary.Host.Scanning;
using GameLibrary.Host.Tools;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 观察域处理器：diagnostics.status / diagnostics.logs / diagnostics.cache_rebuild /
/// tools.discover 四操作。Store 与 Library 均经委托每请求取当前值
/// （library.init / backups.restore 会整体替换 Library；DiagnosticsStatus 读取其
/// Status/Initialized/SchemaVersion/Detail 快照，构造时直引旧引用会读到过期状态）；
/// Identity/Jobs/Metrics/Events/Roots/AuditLog 为 init-only 引用（HostRuntimeState
/// 构造后整体不可替换）。由 DispatchCore 调用，天然继承幂等收据
/// （diagnostics.cache_rebuild 在 ReceiptOperations）与串行门、权限、维护模式等
/// 中间件。tools.discover 契约声明 execution:job 与实现同步返回 Completed 的
/// 既有差异保持原样（不修，仅记录）。
/// </summary>
internal sealed class ObservabilityHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly Func<HostLibraryState> _libraryAccessor;
    private readonly HostIdentity _identity;
    private readonly JobManager _jobs;
    private readonly HostMetrics _metrics;
    private readonly EventStream _events;
    private readonly RootRegistry _roots;
    private readonly AuditLogWriter _auditLog;
    private readonly string _dataDirectory;

    /// <summary>
    /// identity/jobs/metrics/events/roots/auditLog 以 init-only 引用直传
    /// （HostRuntimeState 构造后整体不可替换）；Store 与 Library 经委托每请求取
    /// 当前值（Library 属性可 set 整体替换，直引会读到替换前的旧快照）。
    /// </summary>
    public ObservabilityHandler(
        Func<SqliteLibraryStore?> storeAccessor,
        Func<HostLibraryState> libraryAccessor,
        HostIdentity identity,
        JobManager jobs,
        HostMetrics metrics,
        EventStream events,
        RootRegistry roots,
        AuditLogWriter auditLog,
        string dataDirectory)
    {
        _storeAccessor = storeAccessor;
        _libraryAccessor = libraryAccessor;
        _identity = identity;
        _jobs = jobs;
        _metrics = metrics;
        _events = events;
        _roots = roots;
        _auditLog = auditLog;
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// diagnostics.cache_rebuild（T27/REC-03）：清理可再生缓存目录中的普通文件，
    /// 不穿越链接/junction，不触碰用户原图（assets/）与游戏目录。
    /// </summary>
    public Envelope<object> CacheRebuild(IpcRequest request)
    {
        var cacheParentDirectory = _storeAccessor()?.ReadSettings().CacheParentDirectory;
        if (cacheParentDirectory is not null && _roots.Contains(cacheParentDirectory))
        {
            return IpcRequests.InvalidArgument(request, "当前缓存位置位于已添加的游戏库内，请先在设置中更换位置");
        }

        var cacheDirectory = OwnedPreviewCache.GetRoot(_dataDirectory, cacheParentDirectory);
        CacheDirectoryCleaner.Result cleanup;
        try
        {
            var contentDirectory = OwnedPreviewCache.GetValidatedContentDirectoryForCleanup(
                _dataDirectory, cacheParentDirectory);
            cleanup = contentDirectory is null
                ? new CacheDirectoryCleaner.Result(0, 0, 0, 0)
                : CacheDirectoryCleaner.Clear(contentDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return IpcRequests.InvalidArgument(request, ex.Message);
        }

        var assetsDirectory = Path.Combine(_dataDirectory, "assets");
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

    /// <summary>诊断状态（T24）：进程/库/审计日志统计与队列指标；不含任何业务数据原文。</summary>
    public Envelope<object> DiagnosticsStatus(IpcRequest request)
    {
        var (currentFile, currentBytes, fileCount) = _auditLog.Describe();
        var library = _libraryAccessor();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                processId = _identity.ProcessId,
                startedAtUtc = _identity.StartedAtUtc.ToString("O"),
                appVersion = _identity.AppVersion,
                apiVersion = ApiConstants.ApiVersion,
                library = new
                {
                    status = library.Status.ToString(),
                    initialized = library.Initialized,
                    schemaVersion = library.SchemaVersion,
                    detail = LogSanitizer.Sanitize(library.Detail, _dataDirectory),
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
                jobs = new { activeCount = _jobs.ActiveJobCount() },
                metrics = _metrics.ToDto(),
                eventStream = new
                {
                    occupiedSlots = _events.OccupiedSlots,
                    overflowed = _events.OverflowedCount,
                },
            },
        };
    }

    /// <summary>脱敏审计日志读取（diagnostics.logs）：返回最近 limit 条审计记录。</summary>
    public Envelope<object> DiagnosticsLogs(IpcRequest request)
    {
        var limit = 100;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } logParameters
            && logParameters.TryGetProperty("limit", out var limitElement)
            && limitElement.ValueKind == JsonValueKind.Number
            && limitElement.TryGetInt32(out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 1000);
        }

        var lines = _auditLog.ReadRecentLines(limit);
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
    public Envelope<object> ToolsDiscover(IpcRequest request)
    {
        IpcRequests.TryGetStringParameter(request, "tool", out var discoverTool);
        var hasPath = IpcRequests.TryGetStringParameter(request, "path", out var path) && path.Length > 0;

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
                    notice = LogSanitizer.Sanitize(steam.Notice, _dataDirectory),
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
                if (validation.IsValid && _roots.Contains(validation.Path!.PhysicalPath))
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
            return IpcRequests.InvalidArgument(request, "缺少 path 参数（游戏根的绝对路径）");
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
                    notice = LogSanitizer.Sanitize(renpy.Notice, _dataDirectory),
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
                notice = LogSanitizer.Sanitize(mtoolDiscovery.Notice, _dataDirectory),
            },
        };
    }

    /// <summary>
    /// 路径包含校验（CWE-22 边界）：调用方路径必须在已注册库根内。与
    /// OperationDispatcher / LaunchingHandler / GamesHandler 的同构副本保持一致
    /// （多源同构，先例 IgnoreRulesHandler；待剩余域拆完在收尾片收敛到共享处）。
    /// </summary>
    private Envelope<object>? RejectPathOutsideRoots(IpcRequest request, string physicalPath)
    {
        if (_roots.Contains(physicalPath))
        {
            return null;
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.PermissionDenied,
                Message = $"路径不在已注册库根内（先通过 roots.add 注册）：{physicalPath}",
                Retryable = false,
            },
        };
    }
}
