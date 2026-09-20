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

/// <summary>宿主运行时身份：实例 ID、版本、启动时间；握手与 host.status 共用。</summary>
public sealed class HostIdentity
{
    public HostIdentity()
    {
        InstanceId = Guid.NewGuid().ToString("N");
        StartedAtUtc = DateTime.UtcNow;
        AppVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
    }

    public string InstanceId { get; }

    public string AppVersion { get; }

    public DateTime StartedAtUtc { get; }

    public int ProcessId => Environment.ProcessId;
}

/// <summary>
/// 操作分发器（按域拆分为 partial：OperationDispatcher.Backups/Cataloging/Observability；
/// 候选审核/忽略规则/标签/工具验证/视图设置/启动/游戏卡七域已独立为 Handler 类）。
/// 本文件承载：请求门/纪元校验/收据中间件/路由 + 系统（capabilities/schema/host）与扫描候选域。
/// </summary>
public sealed partial class OperationDispatcher
{
    private readonly HostRuntimeState _state;

    /// <summary>候选审核域（candidates.accept/defer/ignore）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library），events 为 init-only 引用。</summary>
    private readonly CandidateReviewHandler _candidateReview;

    /// <summary>忽略规则域（ignores.list/create/remove）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library），roots 为 init-only 引用。</summary>
    private readonly IgnoreRulesHandler _ignoreRules;

    /// <summary>标签域（tags.* 八操作）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library），events 为 init-only 引用。</summary>
    private readonly TagsHandler _tags;

    /// <summary>工具验证域（verification.* 五操作）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library）；域内零事件，不注入 EventStream。</summary>
    private readonly VerificationHandler _verification;

    /// <summary>视图设置域（views.*/notifications.*/settings.* 十三操作）：store 经委托每请求
    /// 取当前值（library.init/restore 整体替换 Library），events 为 init-only 引用，
    /// ActiveViewId/StartupShortcuts 为宿主可变态经委托读写。</summary>
    private readonly ViewSettingsHandler _viewSettings;

    /// <summary>启动域（launch.*/profiles.*/translation.* 十三操作）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library），launches/roots/events 为 init-only 引用。</summary>
    private readonly LaunchingHandler _launching;

    /// <summary>游戏卡域（games.* 六操作）：store 经委托每请求取当前值
    /// （library.init/restore 整体替换 Library），roots/events 为 init-only 引用。</summary>
    private readonly GamesHandler _games;

    public OperationDispatcher(HostRuntimeState state)
    {
        _state = state;
        _candidateReview = new CandidateReviewHandler(() => state.Library.Store, state.Events);
        _ignoreRules = new IgnoreRulesHandler(() => state.Library.Store, state.Roots);
        _tags = new TagsHandler(() => state.Library.Store, state.Events);
        _verification = new VerificationHandler(() => state.Library.Store);
        _viewSettings = new ViewSettingsHandler(
            () => state.Library.Store,
            state.Events,
            () => state.ActiveViewId,
            value => state.ActiveViewId = value,
            state.Roots,
            state.DataDirectory,
            () => state.StartupShortcuts);
        _launching = new LaunchingHandler(() => state.Library.Store, state.Launches, state.Roots, state.Events);
        _games = new GamesHandler(() => state.Library.Store, state.Roots, state.Events);
    }

    /// <summary>已接入收据的操作子集：catalog 声明 requiresIdempotencyKey 的已实现操作。
    /// scan.start 的作业收据随 T16（作业/收据同事务）接入，内存态作业先行。</summary>
    private static readonly HashSet<string> ReceiptOperations = new(StringComparer.Ordinal)
    {
        "launch.execute",
        "profiles.create",
        "profiles.update",
        "profiles.set_default",
        "profiles.remove",
        "translation.set",
        "games.update",
        "games.create",
        "games.remove",
        "games.relink",
        "views.create",
        "views.update",
        "views.remove",
        "views.activate",
        "notifications.acknowledge",
        "notifications.defer",
        "diagnostics.cache_rebuild",
        "backups.create",
        "settings.update",
        "settings.reset",
        "scan.start",
        "candidates.accept",
        "candidates.defer",
        "candidates.ignore",
        "fields.set",
        "verification.start",
        "verification.report",
        "verification.invalidate",
        "fields.clear",
        "fields.reset",
        "assets.import",
        "assets.choose",
        "assets.crop",
        "assets.reset",
        "assets.remove",
        "metadata.refresh",
        "ignores.create",
        "ignores.remove",
        "roots.remove",
        "tags.create",
        "tags.update",
        "tags.remove",
        "tags.assign",
        "tags.unassign",
        "tags.suppress",
        "tags.reset",
    };

    /// <summary>
    /// 维护模式下仍可用的操作（REC-02）：只读诊断/状态与恢复自身、停机；
    /// 其余变更请求一律 MaintenanceMode 拒绝。
    /// </summary>
    private static readonly HashSet<string> MaintenanceAllowedOperations = new(StringComparer.Ordinal)
    {
        "host.status", "host.stop", "capabilities.get", "schema.get",
        "jobs.get", "scan.status", "scan.coverage",
        "diagnostics.status", "diagnostics.logs", "events.read",
        "backups.list", "backups.inspect", "backups.restore_plan", "backups.restore",
    };

    /// <summary>宿主级请求门：所有 IPC 请求在此串行（v1 审查修复：单写队列语义——
    /// 请求处理不并发进入，后台作业另由 Store 内部锁串行化）。</summary>
    private readonly object _requestGate = new();

    public Envelope<object> Dispatch(IpcRequest request)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = DispatchGated(request);
        started.Stop();

        // 业务审计（T24，补充规格 4.3）：固定字段；参数原文不写入，actor 来自握手回填。
        _state.AuditLog.Append(new Observability.AuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Level = result.Ok ? "information" : "warning",
            Component = "dispatcher",
            OperationId = request.OperationId,
            RequestId = request.RequestId,
            Actor = LogSanitizer.Sanitize(string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName, _state.DataDirectory),
            JobId = result.JobId,
            GameId = TryGetStringParameter(request, "gameId", out var auditGameId) ? auditGameId : null,
            ProfileId = TryGetStringParameter(request, "profileId", out var auditProfileId) ? auditProfileId : null,
            RuleId = TryGetStringParameter(request, "ignoreId", out var auditRuleId) ? auditRuleId : null,
            ResultCode = result.Ok ? result.Status.ToString() : result.Error?.Code ?? "Unknown",
            DurationMs = started.ElapsedMilliseconds,
        });
        _state.Metrics.RecordRequest(request.OperationId, result.Ok, result.Ok ? null : result.Error?.Code, started.ElapsedMilliseconds);

        // 信封补全（契约 3）：响应必须携带当前库实例与数据纪元——
        // 旧纪元客户端由此发现恢复已发生，重连后携带新纪元重试。
        return Stamp(result);
    }

    private Envelope<object> DispatchGated(IpcRequest request)
    {
        // host.stop 是控制面操作：不依赖业务库、不参与串行（停机不得被长请求阻塞）。
        if (request.OperationId == "host.stop")
        {
            return DispatchInternal(request);
        }

        lock (_requestGate)
        {
            var info = OperationCatalog.Catalog.Find(request.OperationId);
            if (info is not null && !HasPermission(request, info.Permission))
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.PermissionDenied,
                        Message = $"客户端握手未声明操作所需权限（{info.Permission}）：{request.OperationId}",
                        Retryable = false,
                    },
                };
            }

            if (_state.MaintenanceMode && !MaintenanceAllowedOperations.Contains(request.OperationId))
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.MaintenanceMode,
                        Message = "库处于维护状态（备份恢复进行中），变更请求已拒绝；请稍后重试",
                        Retryable = true,
                    },
                };
            }

            if (ValidateEpoch(request) is { } epochError)
            {
                return epochError;
            }

            return DispatchInternal(request);
        }
    }

    /// <summary>
    /// 库实例/纪元校验（契约 5）：客户端握手获得 libraryInstanceId/dataEpoch 后，
    /// 变更请求应原样携带。不携带时跳过（兼容未升级的 CLI/MCP）；携带不一致即拒绝。
    /// </summary>
    private Envelope<object>? ValidateEpoch(IpcRequest request)
    {
        var library = _state.Library;
        if (request.LibraryInstanceId is { Length: > 0 } requestedInstance
            && library.Store is not null
            && !string.Equals(requestedInstance, library.Store.Info.LibraryInstanceId, StringComparison.Ordinal))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.LibraryInstanceMismatch,
                    Message = "请求携带的库实例与当前宿主不一致（库可能已被重建）；请重连后重试",
                    Retryable = false,
                },
            };
        }

        if (request.ExpectedDataEpoch is { Length: > 0 } requestedEpoch
            && library.Store is not null
            && !string.Equals(requestedEpoch, library.Store.Info.DataEpoch, StringComparison.Ordinal))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.DataEpochMismatch,
                    Message = "数据纪元已更换（备份恢复已发生）；请重连获取新纪元后重试",
                    Retryable = false,
                },
            };
        }

        return null;
    }

    /// <summary>
    /// 权限校验（契约 access.*）：握手未声明权限集 = 不限权（第一方 CLI/Desktop）；
    /// 声明后须包含操作所需权限，或持有 access.admin（全权）。
    /// </summary>
    private static bool HasPermission(IpcRequest request, string permission)
    {
        if (permission == "none" || request.GrantedPermissions is null)
        {
            return true;
        }

        return request.GrantedPermissions.Contains(permission, StringComparer.Ordinal)
            || request.GrantedPermissions.Contains("access.admin", StringComparer.Ordinal);
    }

    /// <summary>以当前库状态补全信封的库实例/纪元字段（null 键保留，值以当前状态为准）。</summary>
    private Envelope<object> Stamp(Envelope<object> result)
    {
        var library = _state.Library;
        return new Envelope<object>
        {
            ApiVersion = result.ApiVersion,
            RequestId = result.RequestId,
            LibraryInstanceId = library.LibraryInstanceId,
            DataEpoch = library.DataEpoch,
            Ok = result.Ok,
            Status = result.Status,
            Data = result.Data,
            JobId = result.JobId,
            Error = result.Error,
            Warnings = result.Warnings,
            NextActions = result.NextActions,
        };
    }

    private Envelope<object> DispatchInternal(IpcRequest request)
    {
        // host.stop 是控制面操作：不依赖业务库（未 init 也必须能停机），不走库收据中间件。
        if (request.OperationId == "host.stop")
        {
            return DispatchCore(request);
        }

        var info = OperationCatalog.Catalog.Find(request.OperationId);
        if (info is { RequiresIdempotencyKey: true, IsAvailable: true }
            && ReceiptOperations.Contains(request.OperationId))
        {
            return DispatchWithReceipt(request);
        }

        return DispatchCore(request);
    }

    /// <summary>
    /// 幂等收据（契约 7.1）：同键同摘要重放返回原结果，不重复执行；同键不同摘要返回
    /// IdempotencyConflict。launch.execute 的 prepared 收据在进程已创建但结果未落时，
    /// 按 PID+启动时间+路径尽力核实，无法证明即 UnknownOutcome，原键重试不再启动。
    /// </summary>
    private Envelope<object> DispatchWithReceipt(IpcRequest request)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = "库未初始化（先 library.init）；幂等收据需要库实例",
                    Retryable = false,
                },
            };
        }

        if (!TryGetStringParameter(request, "idempotencyKey", out var key))
        {
            return InvalidArgument(request, $"{request.OperationId} 需要 idempotencyKey 参数");
        }

        if (!IsValidIdempotencyKey(key))
        {
            return InvalidArgument(request, "idempotencyKey 非法（1–200 字符、不含控制字符与路径分隔符）");
        }

        var actor = string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName!;
        var digest = RequestDigest(request);
        var existing = store.TryGetReceipt(actor, request.OperationId, key);
        if (existing is not null)
        {
            if (existing.RequestDigest != digest)
            {
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.Failed,
                    Error = new RequestError
                    {
                        Code = ErrorCodes.IdempotencyConflict,
                        Message = $"幂等键已被不同请求使用：{key}",
                        Retryable = false,
                    },
                };
            }

            if (existing.Status == "completed" && existing.ResultJson is not null)
            {
                var replayed = JsonSerializer.Deserialize<Envelope<object>>(existing.ResultJson, ContractJson.Options);
                if (replayed is not null)
                {
                    // 收据重放：结果内容不变，但 RequestId 必须对齐本次请求（客户端按其校验）。
                    return new Envelope<object>
                    {
                        ApiVersion = replayed.ApiVersion,
                        RequestId = request.RequestId,
                        LibraryInstanceId = replayed.LibraryInstanceId,
                        DataEpoch = replayed.DataEpoch,
                        Ok = replayed.Ok,
                        Status = replayed.Status,
                        Data = replayed.Data,
                        JobId = replayed.JobId,
                        Error = replayed.Error,
                        Warnings = replayed.Warnings,
                        NextActions = replayed.NextActions,
                    };
                }
            }

            if (request.OperationId == "launch.execute")
            {
                var recovery = RecoverLaunchReceipt(request, store, existing);
                if (recovery is not null)
                {
                    return recovery;
                }
            }
        }

        var receipt = existing ?? NewReceipt(store, actor, request, key, digest);
        if (existing is null)
        {
            store.InsertPreparedReceipt(receipt);
        }

        var result = DispatchCore(request);
        var resultJson = JsonSerializer.Serialize(result, ContractJson.Options);
        if (request.OperationId == "launch.execute" && result.Ok)
        {
            TryAttachAttemptRef(store, receipt, resultJson);
        }

        store.CompleteReceipt(receipt, resultJson);
        return result;
    }

    /// <summary>进程已创建的 launch 收据立即记录尝试引用：缩小"已启动未落收据"的崩溃歧义窗口。</summary>
    private static void TryAttachAttemptRef(
        Infrastructure.Persistence.SqliteLibraryStore store,
        Infrastructure.Persistence.RequestReceipt receipt,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var data = document.RootElement.GetProperty("data");
            if (data.GetProperty("state").GetString() != "processCreated")
            {
                return;
            }

            var attemptRef = new AttemptRef
            {
                AttemptId = data.GetProperty("attemptId").GetString() ?? "",
                ProcessId = data.GetProperty("processId").GetInt32(),
                ProcessStartedUtc = DateTime.Parse(
                    data.GetProperty("processStartedUtc").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                ExecutablePath = data.GetProperty("executablePath").GetString() ?? "",
            };
            store.UpdateReceiptAttempt(receipt, JsonSerializer.Serialize(attemptRef, ContractJson.Options));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // 尝试引用写不进去只是保留较宽的歧义窗口，不影响执行结果。
        }
    }

    /// <summary>
    /// prepared 收据恢复：有尝试引用则核实（本进程注册表优先，再按 PID/启动时间核实）；
    /// 核实成功返回尝试现状，无法证明返回 UnknownOutcome 并终结收据；无尝试引用
    /// （进程尚未创建即中断）返回 null，允许本次执行继续。
    /// </summary>
    private Envelope<object>? RecoverLaunchReceipt(IpcRequest request, Infrastructure.Persistence.SqliteLibraryStore store, Infrastructure.Persistence.RequestReceipt existing)
    {
        if (existing.AttemptJson is null)
        {
            return null;
        }

        AttemptRef? attemptRef;
        try
        {
            attemptRef = JsonSerializer.Deserialize<AttemptRef>(existing.AttemptJson, ContractJson.Options);
        }
        catch (JsonException)
        {
            attemptRef = null;
        }

        if (attemptRef is null)
        {
            return null;
        }

        var attempt = _state.Launches.GetAttempt(attemptRef.AttemptId);
        if (attempt is not null)
        {
            var live = new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = attempt.ToDto(),
            };
            store.CompleteReceipt(existing, JsonSerializer.Serialize(live, ContractJson.Options));
            return live;
        }

        if (IsProcessVerified(attemptRef))
        {
            // 进程仍在但注册表无记录（不应发生）：保守返回 UnknownOutcome，不重启。
        }

        var unknown = new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.UnknownOutcome,
                Message = $"上次执行结果无法证明（attempt {attemptRef.AttemptId}，pid {attemptRef.ProcessId}）；请检查运行状态后用新幂等键显式重试，本键不再启动",
                Retryable = false,
            },
        };
        store.CompleteReceipt(existing, JsonSerializer.Serialize(unknown, ContractJson.Options));
        return unknown;
    }

    private static bool IsProcessVerified(AttemptRef attemptRef)
    {
        try
        {
            using var process = Process.GetProcessById(attemptRef.ProcessId);
            process.Refresh();
            if (process.HasExited)
            {
                return false;
            }

            var drift = Math.Abs((process.StartTime.ToUniversalTime() - attemptRef.ProcessStartedUtc).TotalSeconds);
            return drift <= 5;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static Infrastructure.Persistence.RequestReceipt NewReceipt(
        Infrastructure.Persistence.SqliteLibraryStore store, string actor, IpcRequest request, string key, string digest) =>
        new()
        {
            LibraryInstanceId = store.Info.LibraryInstanceId,
            Actor = actor,
            OperationId = request.OperationId,
            IdempotencyKey = key,
            RequestDigest = digest,
            Status = "prepared",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

    private static string RequestDigest(IpcRequest request) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.Parameters?.GetRawText() ?? "")));

    /// <summary>
    /// 幂等键约束（v1 审查意见）：长度受限、不含控制字符。路径分隔符允许——
    /// 收据主键在 SQLite、控制区文件名已改为键的 SHA-256，路径字符不再产生穿越风险。
    /// </summary>
    private static bool IsValidIdempotencyKey(string key)
    {
        if (key.Length is < 1 or > 200)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record AttemptRef
    {
        public string AttemptId { get; init; } = "";

        public int ProcessId { get; init; }

        public DateTime ProcessStartedUtc { get; init; }

        public string ExecutablePath { get; init; } = "";
    }

    private Envelope<object> DispatchCore(IpcRequest request) => request.OperationId switch
    {
        "library.init" => LibraryInit(request),
        "host.status" => new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                hostInstanceId = _state.Identity.InstanceId,
                processId = _state.Identity.ProcessId,
                startedAtUtc = _state.Identity.StartedAtUtc.ToString("O"),
                appVersion = _state.Identity.AppVersion,
                apiVersion = ApiConstants.ApiVersion,
                libraryInitialized = _state.Library.Initialized,
                libraryState = _state.Library.Status.ToString(),
                libraryInstanceId = _state.Library.LibraryInstanceId,
                dataEpoch = _state.Library.DataEpoch,
                schemaVersion = _state.Library.SchemaVersion,
            },
        },
        "capabilities.get" => new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = BuildCapabilities(),
        },
        "schema.get" => BuildSchema(request),
        "scan.start" => ScanStart(request),
        "scan.status" or "jobs.get" => JobSnapshotEnvelope(request, request.OperationId == "scan.status" ? "scan" : null),
        "scan.cancel" => ScanCancel(request),
        "scan.coverage" => ScanCoverage(request),
        "scan.inspect" => ScanInspect(request),
        "roots.add" => RootsAdd(request),
        "roots.list" => RootsList(request),
        "roots.remove" => RootsRemove(request),
        "candidates.list" => CandidatesList(request),
        "candidates.get" => CandidatesGet(request),
        "candidates.accept" => _candidateReview.CandidateReview(request, "accept"),
        "candidates.defer" => _candidateReview.CandidateReview(request, "defer"),
        "candidates.ignore" => _candidateReview.CandidateReview(request, "ignore"),
        "games.list" => _games.GamesList(request),
        "games.get" => _games.GamesGet(request),
        "games.create" => _games.GamesCreate(request),
        "games.remove" => _games.GamesRemove(request),
        "tags.list" => _tags.TagsList(request),
        "tags.create" => _tags.TagsCreate(request),
        "tags.update" => _tags.TagsUpdate(request),
        "tags.remove" => _tags.TagsRemove(request),
        "tags.assign" => _tags.TagsAssign(request),
        "tags.unassign" => _tags.TagsUnassign(request),
        "tags.suppress" => _tags.TagsSuppress(request),
        "tags.reset" => _tags.TagsReset(request),
        "events.read" => EventsRead(request),
        "diagnostics.status" => DiagnosticsStatus(request),
        "diagnostics.logs" => DiagnosticsLogs(request),
        "diagnostics.cache_rebuild" => CacheRebuild(request),
        "backups.list" => BackupsList(request),
        "backups.create" => BackupsCreate(request),
        "backups.inspect" => BackupsInspect(request),
        "backups.restore_plan" => BackupsRestorePlan(request),
        "backups.restore" => BackupsRestore(request).GetAwaiter().GetResult(),
        "tools.discover" => ToolsDiscover(request),
        "verification.start" => _verification.VerificationStart(request),
        "verification.report" => _verification.VerificationReport(request),
        "verification.invalidate" => _verification.VerificationInvalidate(request),
        "verification.get" => _verification.VerificationGet(request),
        "verification.list" => _verification.VerificationList(request),
        "fields.set" => FieldsSet(request),
        "fields.clear" => FieldsClear(request),
        "fields.reset" => FieldsReset(request),
        "assets.import" => AssetsImport(request),
        "assets.list" => AssetsList(request),
        "assets.get" => AssetsGet(request),
        "assets.choose" => AssetsChoose(request),
        "assets.crop" => AssetsCrop(request),
        "assets.reset" => AssetsReset(request),
        "assets.remove" => AssetsRemove(request),
        "metadata.preview" => MetadataPreview(request),
        "metadata.refresh" => MetadataRefresh(request),
        "ignores.list" => _ignoreRules.List(request),
        "ignores.create" => _ignoreRules.Create(request),
        "ignores.remove" => _ignoreRules.Remove(request),
        "profiles.create" => _launching.ProfilesCreate(request),
        "profiles.list" => _launching.ProfilesList(request),
        "profiles.get" => _launching.ProfilesGet(request),
        "profiles.update" => _launching.ProfilesUpdate(request),
        "profiles.set_default" => _launching.ProfilesSetDefault(request),
        "profiles.remove" => _launching.ProfilesRemove(request),
        "profiles.validate" => _launching.ProfilesValidate(request),
        "translation.get" => _launching.TranslationGet(request),
        "translation.set" => _launching.TranslationSet(request),
        "games.update" => _games.GamesUpdate(request),
        "games.relink" => _games.GamesRelink(request),
        "views.list" => _viewSettings.ViewsList(request),
        "views.get" => _viewSettings.ViewsGet(request),
        "views.create" => _viewSettings.ViewsCreate(request),
        "views.update" => _viewSettings.ViewsUpdate(request),
        "views.remove" => _viewSettings.ViewsRemove(request),
        "views.activate" => _viewSettings.ViewsActivate(request),
        "notifications.list" => _viewSettings.NotificationsList(request),
        "notifications.get" => _viewSettings.NotificationsGet(request),
        "notifications.acknowledge" => _viewSettings.NotificationTransition(request, "acknowledged"),
        "notifications.defer" => _viewSettings.NotificationTransition(request, "deferred"),
        "settings.get" => _viewSettings.SettingsGet(request),
        "settings.update" => _viewSettings.SettingsUpdate(request),
        "settings.reset" => _viewSettings.SettingsReset(request),
        "host.stop" => HostStop(request),
        "launch.plan" => _launching.LaunchPlanHandler(request),
        "launch.execute" => _launching.LaunchExecute(request),
        "launch.status" => _launching.LaunchStatus(request),
        "launch.history" => _launching.LaunchHistory(request),
        _ => new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = false,
            Status = OperationStatus.Failed,
            Error = new RequestError
            {
                Code = ErrorCodes.UnsupportedOperation,
                Message = $"操作 {request.OperationId} 已在 catalog 登记但尚未实现",
                Retryable = false,
            },
        },
    };

    private Envelope<object> ScanStart(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "root", out var root))
        {
            return InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(root);
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
                    Message = $"根路径非法（{validation.Reason}）：{root}",
                    Retryable = false,
                },
            };
        }

        var rootPath = validation.Path!;
        if (!Directory.Exists(rootPath.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"根路径不存在或离线：{rootPath.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        if (RejectPathOutsideRoots(request, rootPath.PhysicalPath) is { } outsideRoot)
        {
            return outsideRoot;
        }

        var jobId = _state.Jobs.Create(
            "scan",
            context =>
            {
                _state.Coordinator.ManualScanRunning = true;
                try
                {
                    var collector = new ScanCandidateCollector(rootPath, context.JobId, _state.Candidates);
                    // 规则在作业启动时快照：扫描期间的 ignores 变更自下一次扫描生效。
                    var rules = ScanIgnoreRuleSet.FromStore(_state.Library.Store);
                    ScanCoverageData? completedCoverage = null;
                    var outcome = ScanJobRunner.Run(
                        rootPath,
                        context,
                        collector,
                        onCompleted: coverage => completedCoverage = coverage,
                        rules: rules);
                    if (outcome.FinalState == "succeeded")
                    {
                        ScanCandidatePersistence.Persist(
                            _state.Library.Store,
                            _state.Events,
                            collector,
                            context.JobId,
                            readyForReview: true);
                        // T17：完整扫描成功后核对库内游戏可用性（ID-04/05）。
                        if (_state.Library.Store is not null
                            && completedCoverage?.Completion == ScanCompletion.Complete)
                        {
                            var report = ReconcileService.CheckGames(_state.Library.Store, DateTime.UtcNow);
                            foreach (var transition in report.Transitions)
                            {
                                _state.Events.Publish("game.updated", $"game:{transition.GameId}", new
                                {
                                    gameId = transition.GameId,
                                    availability = transition.To,
                                }, DateTime.UtcNow);
                            }
                        }

                        _state.Events.Publish("scan.completed", $"job:{context.JobId}", new
                        {
                            jobId = context.JobId,
                            kind = "manual",
                            root = rootPath.PhysicalPath,
                            completion = completedCoverage?.Completion.ToString().ToLowerInvariant(),
                        }, DateTime.UtcNow);
                    }

                    return Task.FromResult(outcome);
                }
                finally
                {
                    _state.Coordinator.ManualScanRunning = false;
                }
            },
            ScanJobRunner.InitialProgress(rootPath));

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, kind = "scan", state = "running" },
        };
    }

    private Envelope<object> JobSnapshotEnvelope(IpcRequest request, string? expectedKind)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var snapshot = _state.Jobs.Get(jobId);
        if (snapshot is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        if (expectedKind is not null && !string.Equals(snapshot.Kind, expectedKind, StringComparison.Ordinal))
        {
            return InvalidArgument(request, $"作业 {jobId} 类型是 {snapshot.Kind}，不是 {expectedKind}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId = snapshot.JobId,
                kind = snapshot.Kind,
                state = snapshot.State,
                createdUtc = snapshot.CreatedUtc.ToString("O"),
                startedUtc = snapshot.StartedUtc?.ToString("O"),
                finishedUtc = snapshot.FinishedUtc?.ToString("O"),
                error = snapshot.Error,
            },
        };
    }

    private Envelope<object> ScanCancel(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        if (!_state.Jobs.RequestCancel(jobId))
        {
            return _state.Jobs.Get(jobId) is null
                ? NotFound(request, $"作业不存在：{jobId}")
                : InvalidArgument(request, $"作业已进入终态，无法取消：{jobId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { jobId, state = "cancelRequested" },
        };
    }

    private Envelope<object> ScanCoverage(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "jobId", out var jobId))
        {
            return InvalidArgument(request, "缺少 jobId 参数");
        }

        var progress = _state.Jobs.TryGetProgress(jobId);
        if (progress is null)
        {
            return NotFound(request, $"作业不存在：{jobId}");
        }

        var (state, data) = progress.Value;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                jobId,
                state,
                coverage = data,
            },
        };
    }

    /// <summary>注册库根（显式授权动作）；重复注册同一规范化路径幂等；同步落库（v13+）。</summary>
    private Envelope<object> RootsAdd(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "root", out var root))
        {
            return InvalidArgument(request, "缺少 root 参数（绝对本地路径）");
        }

        try
        {
            var libraryRoot = _state.Roots.Add(root);
            _state.Library.Store?.UpsertRoot(
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
    /// 移除库根（roots.remove）：仅解除扫描/启动边界，不触碰游戏数据与记录；
    /// 库内已绑定该根的游戏保留并按 RootUnbound 语义提示。期望 Revision 乐观校验。
    /// </summary>
    private Envelope<object> RootsRemove(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "rootId", out var rootId))
        {
            return InvalidArgument(request, "缺少 rootId 参数");
        }

        if (!TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        var root = _state.Roots.List().FirstOrDefault(r => string.Equals(r.RootId, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            return NotFound(request, $"库根不存在：{rootId}");
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

        var removed = _state.Roots.Remove(rootId);
        if (removed is null)
        {
            return NotFound(request, $"库根不存在：{rootId}");
        }

        _state.Library.Store?.DeleteRoot(rootId);
        _state.Events.Publish("root.removed", $"root:{rootId}", new { rootId }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { rootId, removed = true },
        };
    }

    private Envelope<object> RootsList(IpcRequest request)
    {
        var roots = _state.Roots.List();
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

    /// <summary>
    /// 路径包含校验（CWE-22 边界）：调用方路径必须在已注册库根内。
    /// 启动域的同构校验见 LaunchingHandler.RejectPathOutsideRoots（双源同构，
    /// 先例 IgnoreRulesHandler；待剩余域拆完在收尾片收敛到共享处）。
    /// </summary>
    private Envelope<object>? RejectPathOutsideRoots(IpcRequest request, string physicalPath)
    {
        if (_state.Roots.Contains(physicalPath))
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

    /// <summary>只读单路径识别（契约 scan.inspect）：不落候选、不启动作业。</summary>
    private Envelope<object> ScanInspect(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "path", out var path))
        {
            return InvalidArgument(request, "缺少 path 参数（绝对本地目录路径）");
        }

        var validation = Domain.Paths.GamePath.TryCreate(path);
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
                    Message = $"路径非法（{validation.Reason}）：{path}",
                    Retryable = false,
                },
            };
        }

        if (!Directory.Exists(validation.Path!.PhysicalPath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RootOffline,
                    Message = $"路径不存在或离线：{validation.Path.PhysicalPath}",
                    Retryable = true,
                },
            };
        }

        if (RejectPathOutsideRoots(request, validation.Path.PhysicalPath) is { } inspectOutsideRoot)
        {
            return inspectOutsideRoot;
        }

        var snapshot = new FileSystemDirectorySnapshot(validation.Path);
        var report = new EngineDetectorSet(DefaultDetectors()).DetectAll(snapshot);
        var confirmed = report.Results
            .Where(r => r.Confidence >= DetectionConfidence.Medium)
            .ToArray();
        var generic = confirmed.Length == 0
            ? GenericGameCandidateDetector.Inspect(snapshot)
            : new GenericCandidateFinding([], []);
        var entryCandidates = confirmed.Length > 0
            ? confirmed.SelectMany(result => result.EntryCandidates).ToArray()
            : generic.EntryCandidates;
        var evidence = confirmed.Length > 0
            ? report.Results.SelectMany(result => result.Evidence).ToArray()
            : generic.Evidence;

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                path = validation.Path.PhysicalPath,
                recognized = confirmed.Length > 0 || generic.IsCandidate,
                candidateKind = confirmed.Length > 0 ? "gameRoot" : generic.IsCandidate ? "unknown" : null,
                engines = confirmed.Select(r => new
                {
                    engine = r.Engine,
                    detectorVersion = r.DetectorVersion,
                    confidence = r.Confidence,
                    likelyRoots = r.LikelyRootRelativePaths,
                    entryCandidates = r.EntryCandidates.Select(e => new
                    {
                        relativePath = e.RelativePath,
                        score = e.Score,
                        reasons = e.Reasons,
                    }).ToArray(),
                }).ToArray(),
                engineConflict = report.Conflict is not null,
                entryCandidates = entryCandidates.Select(e => new
                {
                    relativePath = e.RelativePath,
                    score = e.Score,
                    reasons = e.Reasons,
                }).ToArray(),
                evidence = evidence.Select(e => new
                {
                    ruleId = e.RuleId,
                    relativePath = e.RelativePath,
                    observation = e.Observation,
                    polarity = e.Polarity,
                    detail = e.Detail,
                }).ToArray(),
            },
        };
    }


    private Envelope<object> CandidatesList(IpcRequest request)
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
        var store = _state.Library.Store;
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

        var filteredCandidates = _state.Candidates.List(jobId)
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

    private Envelope<object> CandidatesGet(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "candidateId", out var candidateId))
        {
            return InvalidArgument(request, "缺少 candidateId 参数");
        }

        var store = _state.Library.Store;
        if (store is not null)
        {
            var persisted = store.TryGetCandidate(candidateId);
            if (persisted is null)
            {
                return NotFound(request, $"候选不存在：{candidateId}");
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

        var candidate = _state.Candidates.Get(candidateId);
        if (candidate is null)
        {
            return NotFound(request, $"候选不存在：{candidateId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = candidate.ToDetail(),
        };
    }

    /// <summary>
    /// host.stop（T18）：先返回已接收收据，随后在响应送达后请求宿主优雅停机
    /// （排空连接后退出进程；不杀游戏/翻译器）。stop 属持久收据操作，同键重放幂等。
    /// </summary>
    private Envelope<object> HostStop(IpcRequest request)
    {
        _state.NotifyStopRequested?.Invoke();
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { stopping = true, hostInstanceId = _state.Identity.InstanceId },
        };
    }

    /// <summary>
    /// 显式建库（library.init）。自举豁免前置收据：建库成功后在新库中登记收据，
    /// 同键重放返回原结果；DB 已存在但收据缺失（建库后、收据前中断）返回 AlreadyInitialized。
    /// </summary>
    private Envelope<object> LibraryInit(IpcRequest request)
    {
        if (_state.Library.Store is not null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = "库已初始化，不能重复执行 library.init",
                    Retryable = false,
                },
            };
        }

        var identity = _state.Identity;
        var options = new Infrastructure.Persistence.SqliteLibraryStoreOptions
        {
            AppVersion = identity.AppVersion,
            ApiVersion = ApiConstants.ApiVersion,
        };

        // InitializeAsync 全部同步完成（本地 SQLite），分发器保持同步签名。
        var init = Infrastructure.Persistence.SqliteLibraryStore.InitializeAsync(_state.DataDirectory, options, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!init.IsOpened)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = $"建库失败（{init.Status}）：{init.Detail}",
                    Retryable = false,
                },
            };
        }

        // v1 审查修复：库会话整体切换——Library 状态与事件流同步重绑新 Store，
        // 连接代数递增使旧连接（绑定 null Store 的引导期连接）失效、客户端重连。
        _state.Library = new HostLibraryState
        {
            Status = init.Status,
            Store = init.Store,
            Detail = init.Detail,
        };
        _state.Events.BindStore(init.Store);
        Interlocked.Increment(ref _state.ConnectionGeneration);

        var result = new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                libraryInstanceId = init.Store!.Info.LibraryInstanceId,
                dataEpoch = init.Store.Info.DataEpoch,
                schemaVersion = init.Store.Info.SchemaVersion,
            },
        };

        if (TryGetStringParameter(request, "idempotencyKey", out var key))
        {
            var actor = string.IsNullOrWhiteSpace(request.ClientName) ? "anonymous" : request.ClientName!;
            var receipt = new Infrastructure.Persistence.RequestReceipt
            {
                LibraryInstanceId = init.Store.Info.LibraryInstanceId,
                Actor = actor,
                OperationId = request.OperationId,
                IdempotencyKey = key,
                RequestDigest = RequestDigest(request),
                Status = "prepared",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            init.Store.InsertPreparedReceipt(receipt);
            init.Store.CompleteReceipt(receipt, JsonSerializer.Serialize(result, ContractJson.Options));
        }

        return result;
    }

    private static IEngineDetector[] DefaultDetectors() =>
    [
        new UnityDetector(),
        new RpgMakerMvMzDetector(),
        new RenpyDetector(),
        new KirikiriDetector(),
        new FlashDetector(),
    ];

    /// <summary>参数解析与错误信封统一转发 IpcRequests 单源（分部文件内调用点零改动）。</summary>
    private static bool TryGetStringParameter(IpcRequest request, string name, out string value) =>
        IpcRequests.TryGetStringParameter(request, name, out value);

    private static bool TryGetStringListParameter(IpcRequest request, string name, out IReadOnlyList<string> values) =>
        IpcRequests.TryGetStringListParameter(request, name, out values);

    private static bool TryGetBoolParameter(IpcRequest request, string name, out bool? value) =>
        IpcRequests.TryGetBoolParameter(request, name, out value);

    private static bool TryGetIntParameter(IpcRequest request, string name, out int? value) =>
        IpcRequests.TryGetIntParameter(request, name, out value);

    private static Envelope<object> NotFound(IpcRequest request, string message) =>
        IpcRequests.NotFound(request, message);

    private static object BuildCapabilities()
    {
        var catalog = OperationCatalog.Catalog;
        return new
        {
            apiVersion = catalog.ApiVersion,
            catalogVersion = catalog.CatalogVersion,
            appVersion = _identityAppVersion.Value,
            availableOperations = catalog.AvailableOperations.Select(op => op.OperationId).ToArray(),
            availableToolDetails = catalog.AvailableOperations.Select(op => new
            {
                operationId = op.OperationId,
                mcpTool = op.McpTool,
                cli = op.Cli,
                permission = op.Permission,
            }).ToArray(),
            plannedOperationsCount = catalog.Operations.Count - catalog.AvailableOperations.Count,
            permissions = catalog.Permissions,
            supportsCancellation = false,
            supportsEventSubscription = false,
        };
    }

    private static readonly Lazy<string> _identityAppVersion = new(() =>
        typeof(OperationDispatcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0");

    private static Envelope<object> BuildSchema(IpcRequest request)
    {
        string? operationId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters
            && parameters.TryGetProperty("operationId", out var operationElement)
            && operationElement.ValueKind == JsonValueKind.String)
        {
            operationId = operationElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            return InvalidArgument(request, "缺少 operationId 参数");
        }

        var info = OperationCatalog.Catalog.Find(operationId);
        if (info is null)
        {
            return InvalidArgument(request, $"未知操作：{operationId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                operationId = info.OperationId,
                cli = info.Cli,
                mcpTool = info.McpTool,
                handler = info.Handler,
                permission = info.Permission,
                requiresRevision = info.RequiresRevision,
                requiresIdempotencyKey = info.RequiresIdempotencyKey,
                execution = info.Execution,
                available = info.IsAvailable,
                note = info.Note,
                // v1 审查修复：返回程序化生成的真实 JSON Schema（draft 2020-12 子集），
                // 不再把 inputSchemaFile/outputSchemaFile 置 null 冒充机器可发现契约。
                inputSchema = Contracts.OperationSchemas.BuildInputSchema(info.OperationId),
                outputSchema = Contracts.OperationSchemas.BuildOutputSchema(),
            },
        };
    }

    private static Envelope<object> InvalidArgument(IpcRequest request, string message) =>
        IpcRequests.InvalidArgument(request, message);
}
