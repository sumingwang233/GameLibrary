using System.Collections.Concurrent;
using System.Text.Json;
using GameLibrary.Application.Backups;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Backups;
using GameLibrary.Infrastructure.Backups;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 备份域处理器：backups.list / backups.create / backups.inspect / backups.restore_plan /
/// backups.restore 五操作 + RestoreCore 与备份计划注册表等域内状态随域整体迁入
/// （原 OperationDispatcher.Backups 分部删除）。
/// Library 经委托每请求取当前值（restore 临界区内整体替换 Library 并对当前对象
/// 置空 Store，禁止构造时缓存引用）；BindLibraryStore 经委托走 HostRuntimeState
/// 单源（含 Events.BindStore 重绑与 ConnectionGeneration 递增，禁止手抄重实现）；
/// MaintenanceMode 为宿主 volatile 字段，经 setter 委托读写；Jobs 为 init-only 引用，
/// DataDirectory/AppVersion 为不可变值直传。由 DispatchCore 调用，天然继承幂等收据
/// （backups.create 在 ReceiptOperations；backups.restore 自带控制区收据）与串行门、
/// 权限、维护模式等中间件。
/// </summary>
internal sealed class BackupsHandler
{
    /// <summary>备份计划注册表：planId → (backupId, 过期时刻)。10 分钟有效期（契约 9.3）。</summary>
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);

    private sealed class BackupPlan
    {
        public required string BackupId { get; init; }

        public DateTime ExpiresUtc { get; init; }
    }

    private readonly ConcurrentDictionary<string, BackupPlan> _backupPlans = new(StringComparer.Ordinal);

    private readonly Func<HostLibraryState> _library;
    private readonly Action<SqliteLibraryStore?> _bindLibraryStore;
    private readonly JobManager _jobs;
    private readonly string _dataDirectory;
    private readonly string _appVersion;
    private readonly Action<bool> _setMaintenanceMode;
    private readonly HostRuntimeState _state;
    private readonly ConcurrentDictionary<string, (string JobId, Task<Envelope<object>> Result)> _restores = new(StringComparer.Ordinal);

    /// <summary>
    /// jobs 以 init-only 引用直传（HostRuntimeState 构造后整体不可替换）；
    /// Library 经委托每请求取当前值；BindLibraryStore/MaintenanceMode 经委托走
    /// HostRuntimeState 单源；dataDirectory/appVersion 为不可变值直传。
    /// </summary>
    public BackupsHandler(
        Func<HostLibraryState> library,
        Action<SqliteLibraryStore?> bindLibraryStore,
        JobManager jobs,
        string dataDirectory,
        string appVersion,
        Action<bool> setMaintenanceMode,
        HostRuntimeState state)
    {
        _library = library;
        _bindLibraryStore = bindLibraryStore;
        _jobs = jobs;
        _dataDirectory = dataDirectory;
        _appVersion = appVersion;
        _setMaintenanceMode = setMaintenanceMode;
        _state = state;
    }

    private string BackupsRoot => Path.Combine(_dataDirectory, "backups");

    private ControlAreaStore ControlArea => new(Path.Combine(_dataDirectory, "control"));

    private static object BackupDto(string backupId, BackupManifest manifest) => new
    {
        backupId,
        libraryInstanceId = manifest.LibraryInstanceId,
        sourceDataEpoch = manifest.SourceDataEpoch,
        appVersion = manifest.AppVersion,
        schemaVersion = manifest.SchemaVersion,
        createdUtc = manifest.CreatedUtc.ToString("O"),
        fileCount = manifest.Files.Count,
    };

    /// <summary>backups.create（作业）：SQLite 备份 API 一致快照 + 用户原图复制 + 清单哈希。</summary>
    public Envelope<object> BackupsCreate(IpcRequest request)
    {
        var store = _library().Store;
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        var backupId = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var jobId = _jobs.Create(
            "backup",
            async context =>
            {
                // 让出时间片：dispatcher 先把 accepted 响应与收据写完，避免与备份
                // 的连接使用并发（同一 SQLite 连接不允许跨线程并发操作）。
                await Task.Delay(100, context.Token);
                var result = await BackupCreateService.CreateAsync(
                    new LibraryBackupStorage(store, _dataDirectory, backupId), context.Token);
                context.ReportProgress(new { backupId = result.BackupId, assetCount = result.AssetCount, fileCount = result.FileCount });
                return JobOutcome.Succeeded();
            });

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Accepted,
            JobId = jobId,
            Data = new { jobId, kind = "backup", state = "running" },
        };
    }

    /// <summary>backups.list：扫描 backups 根下含有效清单的备份目录。</summary>
    public Envelope<object> BackupsList(IpcRequest request)
    {
        var items = new List<object>();
        if (Directory.Exists(BackupsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(BackupsRoot))
            {
                var manifest = Infrastructure.Backups.BackupArchive.TryReadManifest(dir);
                if (manifest is null)
                {
                    continue;
                }

                items.Add(new
                {
                    backupId = manifest.BackupId,
                    libraryInstanceId = manifest.LibraryInstanceId,
                    createdUtc = manifest.CreatedUtc.ToString("O"),
                    schemaVersion = manifest.SchemaVersion,
                    fileCount = manifest.Files.Count,
                });
            }
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = items.Count, items },
        };
    }

    /// <summary>backups.inspect：清单 + 逐文件 SHA-256 完整性核查。</summary>
    public Envelope<object> BackupsInspect(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "backupId", out var backupId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 backupId 参数");
        }

        var backupDir = Infrastructure.Backups.BackupArchive.BackupDirectory(BackupsRoot, backupId);
        var manifest = Infrastructure.Backups.BackupArchive.TryReadManifest(backupDir);
        if (manifest is null)
        {
            return IpcRequests.NotFound(request, $"备份不存在或清单损坏：{backupId}");
        }

        var problems = Infrastructure.Backups.BackupArchive.Verify(backupDir, manifest);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = problems.Count == 0,
            Status = problems.Count == 0 ? OperationStatus.Completed : OperationStatus.Failed,
            Error = problems.Count == 0 ? null : new RequestError
            {
                Code = ErrorCodes.InvalidArgument,
                Message = $"备份完整性校验失败：{string.Join("; ", problems)}",
                Retryable = false,
            },
            Data = new
            {
                integrity = problems.Count == 0 ? "ok" : "failed",
                problems,
                manifest.LibraryInstanceId,
                manifest.SourceDataEpoch,
                manifest.CreatedUtc,
                manifest.Files.Count,
            } as object ?? new { backupId, integrity = problems.Count == 0 ? "ok" : "failed", problems },
        };
    }

    /// <summary>backups.restore_plan：影响预览 + 10 分钟有效的计划 ID。</summary>
    public Envelope<object> BackupsRestorePlan(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "backupId", out var backupId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 backupId 参数");
        }

        var backupDir = Infrastructure.Backups.BackupArchive.BackupDirectory(BackupsRoot, backupId);
        var manifest = Infrastructure.Backups.BackupArchive.TryReadManifest(backupDir);
        if (manifest is null)
        {
            return IpcRequests.NotFound(request, $"备份不存在或清单损坏：{backupId}");
        }

        var problems = Infrastructure.Backups.BackupArchive.Verify(backupDir, manifest);
        if (problems.Count > 0)
        {
            return IpcRequests.InvalidArgument(request, $"备份完整性校验失败：{string.Join("; ", problems)}");
        }

        var planId = $"plan-{Guid.NewGuid():N}";
        _backupPlans[planId] = new BackupPlan { BackupId = backupId, ExpiresUtc = DateTime.UtcNow + PlanLifetime };
        var store = _library().Store;
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                planId,
                expiresUtc = DateTime.UtcNow.Add(PlanLifetime).ToString("O"),
                backupId,
                backupLibraryInstanceId = manifest.LibraryInstanceId,
                currentLibraryInstanceId = store?.Info.LibraryInstanceId,
                currentDataEpoch = store?.Info.DataEpoch,
                // 恢复后 dataEpoch 更换：旧游标/旧计划/旧 Revision 语境全部失效（AI-10）。
                willRenewDataEpoch = true,
                assetFiles = manifest.Files.Count(f => f.RelativePath.StartsWith("assets/", StringComparison.Ordinal)),
            },
        };
    }

    /// <summary>
    /// backups.restore（REC-02）：控制收据（控制区文件，不随业务库回滚）→ 先备份当前状态
    /// → 关连接 → 暂存替换 → 重开校验 → dataEpoch 续期 → 维护日志每步落盘。
    /// 同键重试返回原结果，不再覆盖。
    /// </summary>
    public async Task<Envelope<object>> BackupsRestore(IpcRequest request, bool startOnly = false)
    {
        if (!IpcRequests.TryGetStringParameter(request, "backupId", out var backupId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 backupId 参数");
        }

        if (!IpcRequests.TryGetStringParameter(request, "planId", out var planId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 planId 参数（先 backups.restore_plan）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "idempotencyKey", out var idempotencyKey))
        {
            return IpcRequests.InvalidArgument(request, "缺少 idempotencyKey 参数");
        }

        // 控制收据检查优先于 plan 校验：同键同参 → 重试返回原结果；同键异参 → IdempotencyConflict。
        var retryParameters = JsonSerializer.Serialize(new { backupId, planId }, ContractJson.Options);
        var retryDigest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retryParameters)));
        var control = ControlArea;
        var (existingResult, conflict) = control.BeginRestoreReceipt(idempotencyKey, retryDigest);
        if (conflict is not null)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.IdempotencyConflict,
                    Message = conflict,
                    Retryable = false,
                },
            };
        }

        if (existingResult is not null)
        {
            // REC-02：相同恢复请求重试返回原结果，不再覆盖。
            // 信封内容不变，但 RequestId 必须对齐本次请求（客户端按其校验）。
            var replayed = JsonSerializer.Deserialize<Envelope<object>>(existingResult, ContractJson.Options);
            if (replayed is null)
            {
                return IpcRequests.InvalidArgument(request, "控制收据损坏");
            }

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

        if (_restores.TryGetValue(idempotencyKey, out var running))
        {
            if (running.Result.IsCompletedSuccessfully && running.Result.Result.Error?.Retryable == true)
                _restores.TryRemove(idempotencyKey, out _);
            else
                return startOnly ? Accepted(request, running.JobId) : WithRequestId(request, await running.Result);
        }
        var journal = LibraryRestoreStorage.ReadJournal(_dataDirectory);
        if (journal is { Phase: "committed", Result: not null } && journal.IdempotencyKey == idempotencyKey)
        {
            if (journal.RequestDigest != retryDigest)
                return IpcRequests.Failure(request, ErrorCodes.IdempotencyConflict, "幂等键已被不同恢复请求使用");
            var recovered = Completed(request, journal.Result);
            control.CompleteRestoreReceipt(idempotencyKey, retryDigest, JsonSerializer.Serialize(recovered, ContractJson.Options));
            return recovered;
        }

        if (!_backupPlans.TryGetValue(planId, out var plan) || plan.ExpiresUtc < DateTime.UtcNow)
        {
            // 已有同键完成收据时按原结果重放，不会被 plan 校验打断。
            var completed = control.TryReadRestoreResult(idempotencyKey);
            if (completed is not null)
            {
                return JsonSerializer.Deserialize<Envelope<object>>(completed, ContractJson.Options)
                    ?? IpcRequests.InvalidArgument(request, "控制收据损坏");
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.PlanExpired,
                    Message = $"恢复计划不存在或已过期（10 分钟有效）：{planId}；请重新 restore_plan",
                    Retryable = false,
                },
            };
        }

        if (plan.BackupId != backupId)
        {
            return IpcRequests.InvalidArgument(request, $"计划 {planId} 对应备份 {plan.BackupId}，与请求的 {backupId} 不一致");
        }

        var backupDir = Infrastructure.Backups.BackupArchive.BackupDirectory(BackupsRoot, backupId);
        var manifest = Infrastructure.Backups.BackupArchive.TryReadManifest(backupDir);
        if (manifest is null)
        {
            return IpcRequests.NotFound(request, $"备份不存在或清单损坏：{backupId}");
        }

        control.AppendMaintenanceLog($"restore begin: backup={backupId} plan={planId}");
        var store = _library().Store;
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化；恢复目标必须存在已初始化的库");
        }

        var drained = _state.Sessions.TryBeginMaintenance(() =>
            _jobs.ActiveJobCount() != 0 || _state.Coordinator.IsRunning || _state.Launches.HasActiveAttempts);
        if (drained is null) return IpcRequests.Failure(request, ErrorCodes.DatabaseBusy,
            "扫描、备份、核对或游戏仍在运行；请结束后重试恢复", retryable: true);
        _setMaintenanceMode(true);
        var completion = new TaskCompletionSource<Envelope<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        string jobId;
        try
        {
            jobId = _jobs.Create("restore", async context =>
            {
                Envelope<object> result;
                try
                {
                    await drained;
                    result = _state.Jobs.ActiveJobCount() > 1 || _state.Launches.HasActiveAttempts
                        ? IpcRequests.Failure(request, ErrorCodes.DatabaseBusy, "已有请求启动了后台活动，请结束后重试", retryable: true)
                        : await RestoreCore(request, control, backupDir, manifest, idempotencyKey, retryDigest, context.JobId);
                }
                catch (Exception ex)
                {
                    result = IpcRequests.Failure(request, ErrorCodes.InternalError, ex.Message, retryable: true);
                }
                finally
                {
                    _state.Sessions.EndMaintenance();
                    _setMaintenanceMode(false);
                }
                context.ReportProgress(result);
                completion.TrySetResult(result);
                return result.Ok ? JobOutcome.Succeeded() : JobOutcome.Failed(result.Error?.Message ?? "恢复失败");
            }, ownsMaintenance: true);
        }
        catch
        {
            _state.Sessions.EndMaintenance();
            _setMaintenanceMode(false);
            throw;
        }
        _restores[idempotencyKey] = (jobId, completion.Task);
        return startOnly ? Accepted(request, jobId) : await completion.Task;
    }

    private static Envelope<object> WithRequestId(IpcRequest request, Envelope<object> result) => new()
    {
        RequestId = request.RequestId,
        ApiVersion = result.ApiVersion,
        LibraryInstanceId = result.LibraryInstanceId,
        DataEpoch = result.DataEpoch,
        Ok = result.Ok,
        Status = result.Status,
        Data = result.Data,
        JobId = result.JobId,
        Error = result.Error,
        Warnings = result.Warnings,
        NextActions = result.NextActions,
    };

    private static Envelope<object> Accepted(IpcRequest request, string jobId) => new()
    {
        RequestId = request.RequestId,
        Ok = true,
        Status = OperationStatus.Accepted,
        JobId = jobId,
        Data = new { jobId, kind = "restore" },
    };

    private static Envelope<object> Completed(IpcRequest request, RestoreResult result) => new()
    {
        RequestId = request.RequestId,
        LibraryInstanceId = result.RestoredLibraryInstanceId,
        DataEpoch = result.DataEpoch,
        Ok = true,
        Status = OperationStatus.Completed,
        Data = result,
    };

    private async Task<Envelope<object>> RestoreCore(IpcRequest request, ControlAreaStore control,
        string backupDir, BackupManifest manifest, string key, string digest, string jobId)
    {
        try
        {
            var storage = new LibraryRestoreStorage(_dataDirectory, backupDir, manifest, _library().Store!,
                new SqliteLibraryStoreOptions { AppVersion = _appVersion, ApiVersion = ApiConstants.ApiVersion },
                key, digest, () => _library().Store = null, _bindLibraryStore, jobId);
            var result = Completed(request, await BackupRestoreService.RestoreAsync(storage));
            control.CompleteRestoreReceipt(key, digest, JsonSerializer.Serialize(result, ContractJson.Options));
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            if (_library().Store is null)
                _state.Library = HostLibraryState.NotInitialized(LibraryOpenStatus.RecoveryRequired, ex.Message);
            control.AppendMaintenanceLog($"restore failed: {ex.Message}");
            var failure = IpcRequests.Failure(request, ErrorCodes.InternalError, $"恢复失败，请检查安全备份与控制区：{ex.Message}", retryable: true);
            var journal = LibraryRestoreStorage.ReadJournal(_dataDirectory);
            if (journal is { Phase: "committed", Result: not null } && journal.IdempotencyKey == key)
                return Completed(request, journal.Result);
            control.CompleteRestoreReceipt(key, digest, JsonSerializer.Serialize(failure, ContractJson.Options));
            return failure;
        }
    }
}
