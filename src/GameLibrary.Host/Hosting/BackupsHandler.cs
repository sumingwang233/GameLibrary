using System.Collections.Concurrent;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
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
        Action<bool> setMaintenanceMode)
    {
        _library = library;
        _bindLibraryStore = bindLibraryStore;
        _jobs = jobs;
        _dataDirectory = dataDirectory;
        _appVersion = appVersion;
        _setMaintenanceMode = setMaintenanceMode;
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
                var backupDir = Infrastructure.Backups.BackupArchive.BackupDirectory(BackupsRoot, backupId);
                Directory.CreateDirectory(backupDir);
                var stagedDb = Path.Combine(backupDir, Infrastructure.Backups.BackupArchive.DatabaseFileName);

                // 1. 一致性库快照（SQLite 备份 API）。
                store.CreateBackupAsync(stagedDb, context.Token).GetAwaiter().GetResult();

                // 2. 用户原图复制（应用目录 assets/；缓存与外部游戏不入备份）。
                var assetsSource = Path.Combine(_dataDirectory, "assets");
                var assetCount = Directory.Exists(assetsSource)
                    ? Infrastructure.Backups.BackupArchive.CopyDirectory(assetsSource, Path.Combine(backupDir, "assets"))
                    : 0;

                // 3. 清单（逐文件哈希）。
                var manifest = new Infrastructure.Backups.BackupManifest
                {
                    BackupId = backupId,
                    LibraryInstanceId = store.Info.LibraryInstanceId,
                    SourceDataEpoch = store.Info.DataEpoch,
                    AppVersion = store.Info.AppVersion,
                    SchemaVersion = store.Info.SchemaVersion,
                    CreatedUtc = DateTime.UtcNow,
                    Files = Infrastructure.Backups.BackupArchive.EnumerateFiles(backupDir),
                };
                Infrastructure.Backups.BackupArchive.WriteManifest(backupDir, manifest);
                context.ReportProgress(new { backupId, assetCount, fileCount = manifest.Files.Count });
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
    public async Task<Envelope<object>> BackupsRestore(IpcRequest request)
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

        // 维护模式（REC-02）：进入恢复临界区——新变更请求被拒绝，只读与恢复自身可用。
        _setMaintenanceMode(true);
        try
        {
            return RestoreCore(request, control, backupDir, manifest, backupId, planId, idempotencyKey, retryDigest)
                .GetAwaiter().GetResult();
        }
        finally
        {
            _setMaintenanceMode(false);
        }
    }

    private async Task<Envelope<object>> RestoreCore(
        IpcRequest request,
        Infrastructure.Backups.ControlAreaStore control,
        string backupDir,
        Infrastructure.Backups.BackupManifest manifest,
        string backupId,
        string planId,
        string idempotencyKey,
        string retryDigest)
    {
        try
        {
            // 1. 恢复前先备份当前状态（原库保留语义的第一层）。
            var safetyId = $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var safetyDir = Infrastructure.Backups.BackupArchive.BackupDirectory(BackupsRoot, safetyId);
            Directory.CreateDirectory(safetyDir);
            var safetyDb = Path.Combine(safetyDir, Infrastructure.Backups.BackupArchive.DatabaseFileName);
            await _library().Store!.CreateBackupAsync(safetyDb, CancellationToken.None);
            control.AppendMaintenanceLog($"safety backup: {safetyId}");

            // 2. 校验备份完整性。
            var problems = Infrastructure.Backups.BackupArchive.Verify(backupDir, manifest);
            if (problems.Count > 0)
            {
                control.AppendMaintenanceLog($"verify failed: {string.Join("; ", problems)}");
                return IpcRequests.InvalidArgument(request, $"备份完整性校验失败：{string.Join("; ", problems)}");
            }

            // 3. 关闭旧连接（WAL checkpoint 归属旧连接）后才能替换文件。
            var previousStore = _library().Store!;
            _library().Store = null;
            await previousStore.DisposeAsync();
            control.AppendMaintenanceLog("old connection closed");

            SqliteLibraryStore? restoredStore = null;
            try
            {
                // 4. 替换库文件 → 重开并走完整校验/迁移路径。
                var stagedDb = Path.Combine(backupDir, Infrastructure.Backups.BackupArchive.DatabaseFileName);
                File.Copy(stagedDb, Path.Combine(_dataDirectory, "library.db"), overwrite: true);
                foreach (var residue in new[] { "library.db-wal", "library.db-shm" })
                {
                    var residuePath = Path.Combine(_dataDirectory, residue);
                    if (File.Exists(residuePath))
                    {
                        File.Delete(residuePath);
                    }
                }

                restoredStore = (await SqliteLibraryStore.TryOpenAsync(_dataDirectory, new SqliteLibraryStoreOptions
                {
                    AppVersion = _appVersion,
                    ApiVersion = ApiConstants.ApiVersion,
                }, CancellationToken.None)).Store;
                if (restoredStore is null)
                {
                    throw new InvalidOperationException("恢复后的库无法打开");
                }

                // v1 审查修复：库会话整体切换——事件流同步重绑到新 Store（新纪元、序号从新库恢复），
                // 不再指向已关闭的旧连接；连接代数递增使旧纪元客户端断连。
                _bindLibraryStore(restoredStore);
                control.AppendMaintenanceLog("database swapped");
            }
            catch (Exception ex)
            {
                // 恢复失败：从安全备份文件回退，重开旧库继续服务。
                control.AppendMaintenanceLog($"swap failed: {ex.Message}; rolling back to safety backup");
                File.Copy(safetyDb, Path.Combine(_dataDirectory, "library.db"), overwrite: true);
                var rolledBack = await SqliteLibraryStore.TryOpenAsync(_dataDirectory, new SqliteLibraryStoreOptions
                {
                    AppVersion = _appVersion,
                    ApiVersion = ApiConstants.ApiVersion,
                }, CancellationToken.None);
                if (rolledBack.IsOpened)
                {
                    _bindLibraryStore(rolledBack.Store);
                }

                throw;
            }

            // 5. 用户原图恢复（assets/ 覆盖回应用目录）。
            var backupAssets = Path.Combine(backupDir, "assets");
            if (Directory.Exists(backupAssets))
            {
                Infrastructure.Backups.BackupArchive.CopyDirectory(
                    backupAssets, Path.Combine(_dataDirectory, "assets"));
                control.AppendMaintenanceLog("assets restored");
            }

            // 6. dataEpoch 续期：旧游标/旧计划/旧 Revision 语境全部失效。
            var newEpoch = restoredStore!.RenewDataEpochAsync(CancellationToken.None).GetAwaiter().GetResult();
            control.AppendMaintenanceLog($"epoch renewed: {newEpoch}");

            var result = new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new
                {
                    backupId,
                    restoredLibraryInstanceId = manifest.LibraryInstanceId,
                    dataEpoch = newEpoch,
                    safetyBackupId = safetyId,
                    restored = true,
                },
            };
            control.AppendMaintenanceLog($"restore completed: backup={backupId}");
            control.CompleteRestoreReceipt(idempotencyKey, retryDigest, JsonSerializer.Serialize(result, ContractJson.Options));
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            control.AppendMaintenanceLog($"restore failed: {ex.Message}");
            var failure = new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InternalError,
                    Message = $"恢复失败（原库保留；安全备份与控制日志在 backups/control 区）：{ex.Message}",
                    Retryable = true,
                },
            };
            control.CompleteRestoreReceipt(idempotencyKey, retryDigest, JsonSerializer.Serialize(failure, ContractJson.Options));
            return failure;
        }
    }
}
