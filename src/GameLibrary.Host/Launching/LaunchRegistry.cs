using System.Collections.Concurrent;
using System.Diagnostics;
using GameLibrary.Contracts;
using GameLibrary.Domain.Tools;

namespace GameLibrary.Host.Launching;

/// <summary>
/// 启动配置（T13 扩展）：绝对 exe、argv 数组、绝对 cwd、每游戏唯一默认标记、可选工具绑定。
/// ToolId 非空表示该 Profile 经翻译工具启动；翻译 Required 的游戏默认走翻译路由（不回退直启）。
/// </summary>
public sealed record LaunchProfile
{
    public required string ProfileId { get; init; }

    public required string GameId { get; init; }

    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>绑定工具（MTool/RenpyThief/播放器/steam 等）；null 表示普通直启。</summary>
    public string? ToolId { get; init; }

    /// <summary>每游戏最多一个默认 Profile；games 层 launch 无 profileId 时使用它。</summary>
    public bool IsDefault { get; init; }

    /// <summary>更新即递增；使引用旧 Revision 的 LaunchPlan 失效（PlanStale）。</summary>
    public int Revision { get; init; } = 1;

    public string Source { get; init; } = "manual";
    public string ValidationStatus { get; init; } = "manual";
    public int SuggestionScore { get; init; }
    public IReadOnlyList<string> SuggestionReasons { get; init; } = [];
}

/// <summary>纯数据启动计划（契约 5.x launch.plan）：可预览，不产生系统副作用。</summary>
public sealed record LaunchPlan
{
    public required string PlanId { get; init; }

    public required string ProfileId { get; init; }

    public required int ProfileRevision { get; init; }

    public required string GameId { get; init; }

    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public object ToDto() => new
    {
        planId = PlanId,
        profileId = ProfileId,
        profileRevision = ProfileRevision,
        gameId = GameId,
        executablePath = ExecutablePath,
        argv = Arguments,
        cwd = WorkingDirectory,
        createdUtc = CreatedUtc.ToString("O"),
    };
}

/// <summary>尝试状态机：prepared → executing → processCreated → exited / processStartFailed。</summary>
public sealed record LaunchAttempt
{
    public required string AttemptId { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string GameId { get; init; }

    public required string ProfileId { get; init; }

    public string? PlanId { get; init; }

    public required string State { get; init; }

    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public int? ProcessId { get; init; }

    public DateTime? ProcessStartedUtc { get; init; }

    public int? ExitCode { get; init; }

    public DateTime? FinishedUtc { get; init; }

    public string? Error { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public bool IsTerminal => State is "exited" or "processStartFailed" or "unknownOutcome";

    public object ToDto() => new
    {
        attemptId = AttemptId,
        idempotencyKey = IdempotencyKey,
        gameId = GameId,
        profileId = ProfileId,
        planId = PlanId,
        state = State,
        executablePath = ExecutablePath,
        argv = Arguments,
        cwd = WorkingDirectory,
        processId = ProcessId,
        processStartedUtc = ProcessStartedUtc?.ToString("O"),
        exitCode = ExitCode,
        finishedUtc = FinishedUtc?.ToString("O"),
        error = Error,
        createdUtc = CreatedUtc.ToString("O"),
    };
}

/// <summary>
/// 宿主内启动注册表（T06 内存态；收据/数据库持久化随 T23/T27）：
/// 全入口互斥（同游戏同时只允许一个进行中的启动，与客户端无关）、
/// 幂等键重放返回原尝试、Profile Revision 使旧计划失效（PlanStale）。
/// 真实启动只允许经由 Profile 校验的 EXE/SWF；SWF 交给 Windows 文件关联打开。
/// </summary>
public sealed partial class LaunchRegistry
{
    /// <summary>翻译注入步骤默认等待上限（120s）；测试可经 Execute 参数收紧。</summary>
    public static readonly TimeSpan DefaultTranslationStepTimeout = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<string, LaunchProfile> _profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LaunchPlan> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LaunchAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _receiptByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByGame = new(StringComparer.Ordinal);

    public void Clear()
    {
        lock (_profileLock)
        {
            CancelObservations();
            _profiles.Clear();
            _plans.Clear();
            _attempts.Clear();
            _receiptByKey.Clear();
            _activeByGame.Clear();
            _processes.Clear();
        }
    }

    public Func<IDisposable?>? AcquireLibraryLease { get; set; }

    public bool HasActiveAttempts => History().Any(attempt => !attempt.IsTerminal);

    /// <summary>
    /// 尝试状态变更回调（v1 审查修复：启动历史持久化）——Profile 变更/尝试状态
    /// 每次迁移后触发，Host 接线到运行态持久层。
    /// </summary>
    public Action<LaunchAttempt>? OnAttemptChanged { get; set; }

    /// <summary>Profile 变更回调（create/update/set_default/remove 后触发，供持久化）。</summary>
    public Action<LaunchProfile?>? OnProfileChanged { get; set; }

    private void NotifyAttempt(LaunchAttempt attempt) => OnAttemptChanged?.Invoke(attempt);

    /// <summary>启动恢复：把持久层 Profile 注册回内存（不触发回调）。</summary>
    public void RestoreProfile(LaunchProfile profile) => _profiles[profile.ProfileId] = profile.ValidationStatus == "verifying"
        ? profile with { ValidationStatus = "inconclusive" } : profile;

    /// <summary>Restore only a process whose PID, start time and executable still match; uncertain effects are never rerun.</summary>
    public void RestoreAttempt(LaunchAttempt attempt, string? receiptScope = null)
    {
        Process? process = null;
        if (!attempt.IsTerminal)
        {
            try
            {
                if (attempt.ProcessId is { } pid && attempt.ProcessStartedUtc is { } started)
                {
                    process = Process.GetProcessById(pid);
                    if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - started).TotalSeconds) > 1
                        || !SamePath(process.MainModule?.FileName ?? "", attempt.ExecutablePath))
                    {
                        process.Dispose();
                        process = null;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process?.Dispose();
                process = null;
            }
            if (process is not null)
            {
                attempt = attempt with { State = "processCreated" };
                _processes[attempt.AttemptId] = process;
                _activeByGame[attempt.GameId] = attempt.AttemptId;
            }
            else
                attempt = attempt with { State = "unknownOutcome", Error = "宿主重启后无法证明上次启动结果；不会自动再次启动" };
        }

        _attempts[attempt.AttemptId] = attempt;
        _receiptByKey[receiptScope ?? attempt.IdempotencyKey] = attempt.AttemptId;
        if (process is not null) WatchProcessExit(attempt.AttemptId, process);
    }

    private LaunchProfile AddProfileCore(
        string gameId,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? toolId = null,
        bool isDefault = false)
    {
        var existing = _profiles.Values.FirstOrDefault(p => p.GameId == gameId && (p.Source == "automatic" || p.ValidationStatus == "deleted")
            && SamePath(p.ExecutablePath, executablePath));
        var profile = new LaunchProfile
        {
            ProfileId = existing?.ProfileId ?? $"profile-{Guid.NewGuid():N}",
            GameId = gameId,
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            ToolId = toolId,
            IsDefault = isDefault || existing?.IsDefault == true,
            Revision = (existing?.Revision ?? 0) + 1,
        };
        _profiles[profile.ProfileId] = profile;
        if (profile.IsDefault)
        {
            ClearOtherDefaults(gameId, profile.ProfileId);
        }

        OnProfileChanged?.Invoke(profile);
        return profile;
    }

    public LaunchProfile? GetProfile(string profileId) =>
        _profiles.TryGetValue(profileId, out var profile) && profile.ValidationStatus != "deleted" ? profile : null;

    /// <summary>计划归属的 ProfileId（launch.execute 阻断检查用）；计划不存在返回 null。</summary>
    public string? GetPlanProfileId(string planId) =>
        _plans.TryGetValue(planId, out var plan) ? plan.ProfileId : null;

    /// <summary>计划归属的 GameId；计划不存在返回 null。</summary>
    public string? GetPlanGameId(string planId) =>
        _plans.TryGetValue(planId, out var plan) ? plan.GameId : null;

    public IReadOnlyList<LaunchProfile> ListProfiles(string? gameId = null) =>
        _profiles.Values
            .Where(p => gameId is null || string.Equals(p.GameId, gameId, StringComparison.Ordinal))
            .Where(p => p.ValidationStatus != "deleted")
            .OrderBy(p => p.IsDefault ? 0 : 1)
            .ThenBy(p => p.ProfileId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>该游戏当前默认 Profile；无默认时为 null。</summary>
    public LaunchProfile? GetDefaultProfile(string gameId) =>
        ListProfiles(gameId).FirstOrDefault(p => p.IsDefault);

    private LaunchProfile UpdateProfileCore(string profileId, string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var current = _profiles[profileId];
        var updated = current with
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Revision = current.Revision + 1,
            Source = "manual",
            ValidationStatus = "manual",
        };
        _profiles[profileId] = updated;
        OnProfileChanged?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// 设为该游戏默认（profiles.set_default）：显式替代项语义——新默认生效即清除旧默认。
    /// Profile 不存在或属于其他游戏抛 <see cref="LaunchException"/>。
    /// </summary>
    private LaunchProfile SetDefaultCore(string gameId, string profileId)
    {
        var profile = _profiles.TryGetValue(profileId, out var p) ? p : null;
        if (profile is null || !string.Equals(profile.GameId, gameId, StringComparison.Ordinal))
        {
            throw new LaunchException(ErrorCodes.NotFound, $"Profile 不存在或不属于该游戏：{profileId}");
        }

        if (!profile.IsDefault || profile.Source == "automatic")
        {
            var updated = profile with { IsDefault = true, Revision = profile.Revision + 1, Source = "manual", ValidationStatus = "manual" };
            _profiles[profileId] = updated;
            ClearOtherDefaults(gameId, profileId);
            OnProfileChanged?.Invoke(updated);
            return updated;
        }

        return profile;
    }

    /// <summary>
    /// 移除非默认 Profile（profiles.remove）。默认 Profile 需先显式 set_default 替代项，
    /// 这里直接拒绝（契约 3.1：默认配置移除需明确替代项）。
    /// </summary>
    private LaunchProfile RemoveProfileCore(string profileId)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
        {
            throw new LaunchException(ErrorCodes.NotFound, $"Profile 不存在：{profileId}");
        }

        if (profile.IsDefault)
        {
            throw new LaunchException(
                ErrorCodes.InvalidArgument,
                $"Profile {profileId} 是该游戏的默认配置；先用 profiles.set_default 指定替代项后才能移除");
        }

        var deleted = profile with { ValidationStatus = "deleted", Revision = profile.Revision + 1 };
        _profiles[profileId] = deleted;
        OnProfileChanged?.Invoke(deleted);
        return profile;
    }

    private void ClearOtherDefaults(string gameId, string keepProfileId)
    {
        foreach (var other in _profiles.Values)
        {
            if (!string.Equals(other.GameId, gameId, StringComparison.Ordinal)
                || string.Equals(other.ProfileId, keepProfileId, StringComparison.Ordinal)
                || !other.IsDefault)
            {
                continue;
            }

            _profiles[other.ProfileId] = other with { IsDefault = false, Revision = other.Revision + 1 };
            OnProfileChanged?.Invoke(_profiles[other.ProfileId]);
        }
    }

    /// <summary>生成纯数据计划；Profile 不存在或 exe/cwd 不在时直接失败，不产生计划。</summary>
    public LaunchPlan CreatePlan(string gameId, string profileId)
    {
        var profile = _profiles.TryGetValue(profileId, out var p) ? p : null;
        if (profile is null || !string.Equals(profile.GameId, gameId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Profile 不存在或不属于该游戏：{profileId}");
        }

        ValidatePaths(profile);
        if (profile.ValidationStatus is "discarded" or "deleted")
            throw new LaunchException(ErrorCodes.InvalidArgument, "废弃启动方式必须先手动恢复或配置");
        var plan = new LaunchPlan
        {
            PlanId = $"plan-{Guid.NewGuid():N}",
            ProfileId = profile.ProfileId,
            ProfileRevision = profile.Revision,
            GameId = gameId,
            ExecutablePath = profile.ExecutablePath,
            Arguments = profile.Arguments,
            WorkingDirectory = profile.WorkingDirectory,
            CreatedUtc = DateTime.UtcNow,
        };
        _plans[plan.PlanId] = plan;
        return plan;
    }

    /// <summary>
    /// 执行：先检查幂等收据（重放返回原尝试，不重复启动），再检查全入口互斥，
    /// 然后经 prepared → executing → Process.Start → processCreated。不等待进程退出。
    /// </summary>
    public LaunchAttempt Execute(
        string idempotencyKey,
        string? planId,
        string? profileId,
        string? expectedRevisionProfileId,
        int? expectedRevision,
        IReadOnlyList<RecipeProcessStep>? translationSteps = null,
        TimeSpan? translationStepTimeout = null,
        string? receiptScope = null,
        Action<LaunchAttempt>? beforeStart = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = Prepare(idempotencyKey, planId, profileId, expectedRevisionProfileId, expectedRevision, receiptScope, beforeStart);
        return attempt.State == "prepared"
            ? RunPrepared(attempt.AttemptId, translationSteps, translationStepTimeout, cancellationToken) : attempt;
    }

    public LaunchAttempt Prepare(string idempotencyKey, string? planId, string? profileId,
        string? expectedRevisionProfileId, int? expectedRevision, string? receiptScope = null, Action<LaunchAttempt>? beforeStart = null)
    {
        var key = receiptScope ?? idempotencyKey;
        if (_receiptByKey.TryGetValue(key, out var existingAttemptId))
        {
            return _attempts[existingAttemptId];
        }

        LaunchPlan plan;
        if (planId is not null)
        {
            if (!_plans.TryGetValue(planId, out var storedPlan))
            {
                throw new LaunchException(ErrorCodes.NotFound, $"计划不存在：{planId}");
            }

            var current = _profiles[storedPlan.ProfileId];
            if (current.Revision != storedPlan.ProfileRevision)
            {
                throw new LaunchException(ErrorCodes.PlanStale,
                    $"Profile 已更新（Revision {storedPlan.ProfileRevision} → {current.Revision}），旧计划失效");
            }

            plan = storedPlan;
        }
        else if (profileId is not null || expectedRevisionProfileId is not null)
        {
            var id = profileId ?? expectedRevisionProfileId!;
            var profile = _profiles.TryGetValue(id, out var p) ? p : null;
            if (profile is null)
            {
                throw new LaunchException(ErrorCodes.NotFound, $"Profile 不存在：{id}");
            }

            if (expectedRevision is not null && expectedRevision.Value != profile.Revision)
            {
                throw new LaunchException(ErrorCodes.RevisionConflict, $"Profile Revision 不一致：期望 {expectedRevision}，当前 {profile.Revision}");
            }

            plan = CreatePlan(profile.GameId, id);
        }
        else
        {
            throw new LaunchException(ErrorCodes.InvalidArgument, "launch execute 需要 planId 或 profileId");
        }

        // 全入口互斥：刷新该游戏既有尝试后仍活动 → 拒绝。
        if (_activeByGame.TryGetValue(plan.GameId, out var activeAttemptId)
            && _attempts.TryGetValue(activeAttemptId, out var active)
            && !RefreshAttempt(active).IsTerminal)
        {
            throw new LaunchException(ErrorCodes.InvalidArgument,
                $"游戏已有进行中的启动（attempt {active.AttemptId}，state {active.State}）；全入口互斥生效");
        }

        ValidatePaths(_profiles[plan.ProfileId]);
        var attempt = new LaunchAttempt
        {
            AttemptId = $"attempt-{Guid.NewGuid():N}",
            IdempotencyKey = idempotencyKey,
            GameId = plan.GameId,
            ProfileId = plan.ProfileId,
            PlanId = plan.PlanId,
            State = "prepared",
            ExecutablePath = plan.ExecutablePath,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            CreatedUtc = DateTime.UtcNow,
        };
        _attempts[attempt.AttemptId] = attempt;
        if (!_receiptByKey.TryAdd(key, attempt.AttemptId))
        {
            // 并发相同键：返回已注册的收据，不启动第二个进程。
            _attempts.TryRemove(attempt.AttemptId, out _);
            return _attempts[_receiptByKey[key]];
        }

        if (!_activeByGame.TryAdd(plan.GameId, attempt.AttemptId))
        {
            _receiptByKey.TryRemove(key, out _);
            _attempts.TryRemove(attempt.AttemptId, out _);
            throw new LaunchException(ErrorCodes.InvalidArgument, "该游戏已有进行中的启动");
        }
        try
        {
            beforeStart?.Invoke(attempt);
            NotifyAttempt(attempt);
        }
        catch
        {
            _activeByGame.TryRemove(plan.GameId, out _);
            _receiptByKey.TryRemove(key, out _);
            _attempts.TryRemove(attempt.AttemptId, out _);
            throw;
        }
        return attempt;
    }

    public void FailPrepared(string attemptId, string message)
    {
        lock (_refreshLock)
        {
            if (!_attempts.TryGetValue(attemptId, out var attempt) || attempt.State != "prepared") return;
            attempt = attempt with { State = "processStartFailed", Error = message, FinishedUtc = DateTime.UtcNow };
            _attempts[attemptId] = attempt;
            _activeByGame.TryRemove(attempt.GameId, out _);
            NotifyAttempt(attempt);
        }
    }

    public LaunchAttempt RunPrepared(string attemptId, IReadOnlyList<RecipeProcessStep>? translationSteps = null,
        TimeSpan? translationStepTimeout = null, CancellationToken cancellationToken = default)
    {
        LaunchAttempt attempt;
        lock (_refreshLock)
        {
            attempt = _attempts[attemptId];
            if (attempt.State != "prepared") return attempt;
            attempt = attempt with { State = "executing" };
            _attempts[attempt.AttemptId] = attempt;
        }
        var plan = _plans[attempt.PlanId!];

        var started = new List<Process>();
        try
        {
            NotifyAttempt(attempt);
            var steps = translationSteps is { Count: > 0 }
                ? translationSteps.OrderBy(step => step.Sequence).ToArray()
                :
                [
                    new RecipeProcessStep
                    {
                        Sequence = 0,
                        ExecutablePath = plan.ExecutablePath,
                        Arguments = plan.Arguments,
                        WorkingDirectory = plan.WorkingDirectory,
                        WaitForExit = false,
                    },
                ];
            Process? process = null;
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startInfo = new ProcessStartInfo
                {
                    FileName = step.ExecutablePath,
                    WorkingDirectory = step.WorkingDirectory,
                    UseShellExecute = Path.GetExtension(step.ExecutablePath)
                        .Equals(".swf", StringComparison.OrdinalIgnoreCase),
                };
                foreach (var argument in step.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                process = StartProcessWithElevationFallback(startInfo)
                    ?? throw new LaunchException(ErrorCodes.ProcessStartFailed, "进程启动返回空");
                started.Add(process);
                if (step.WaitForExit)
                {
                    // Slow steps run in JobManager; cancellation ends only processes owned by this attempt.
                    var timeout = translationStepTimeout ?? DefaultTranslationStepTimeout;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(timeout);
                    try { process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        throw new LaunchException(
                            ErrorCodes.ProcessStartFailed,
                            $"翻译步骤超时（{timeout.TotalSeconds:0}s）已终止全部已启动进程：{step.ExecutablePath}");
                    }

                    if (process.ExitCode != 0)
                    {
                        throw new LaunchException(
                            ErrorCodes.ProcessStartFailed,
                            $"翻译步骤失败：{step.ExecutablePath}（退出码 {process.ExitCode}）");
                    }
                }
            }

            if (process is null)
            {
                throw new LaunchException(ErrorCodes.ProcessStartFailed, "没有可执行的启动步骤");
            }

            attempt = attempt with
            {
                State = "processCreated",
                ExecutablePath = steps[^1].ExecutablePath,
                ProcessId = process.Id,
                ProcessStartedUtc = process.StartTime.ToUniversalTime(),
            };
            _attempts[attempt.AttemptId] = attempt;
            _processes[attempt.AttemptId] = process;
            // feat-1：主动订阅游戏进程退出——不等人查询 launch.status/history 就完成
            // attempt 记录（经 NotifyAttempt→OnAttemptChanged 落库，与惰性路径同一出口）。
            // 只订阅最终进程；WaitForExit=true 的翻译注入步骤仍按原有同步有界等待处理。
            NotifyAttempt(attempt);
            if (_profiles[plan.ProfileId].Source == "automatic")
                ObserveSuggestion(attempt, process, plan.ProfileRevision);
            else
                WatchProcessExit(attempt.AttemptId, process);
            return attempt;
        }
        catch (Exception ex) when (ex is not LaunchException)
        {
            CompleteSuggestion(plan.ProfileId, plan.ProfileRevision, translationSteps is null && IsBadExecutable(ex) ? "discarded" : "inconclusive");
            StopStartedProcesses(started);
            attempt = attempt with
            {
                State = "processStartFailed",
                Error = ex.Message,
                FinishedUtc = DateTime.UtcNow,
            };
            _attempts[attempt.AttemptId] = attempt;
            _activeByGame.TryRemove(plan.GameId, out _);
            NotifyAttempt(attempt);
            throw new LaunchException(ErrorCodes.ProcessStartFailed, $"进程启动失败：{ex.Message}");
        }
        catch (LaunchException ex)
        {
            CompleteSuggestion(plan.ProfileId, plan.ProfileRevision, "inconclusive");
            StopStartedProcesses(started);
            attempt = attempt with
            {
                State = "processStartFailed",
                Error = ex.Message,
                FinishedUtc = DateTime.UtcNow,
            };
            _attempts[attempt.AttemptId] = attempt;
            _activeByGame.TryRemove(plan.GameId, out _);
            NotifyAttempt(attempt);
            throw;
        }
    }

    private static void StopStartedProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes.Reverse())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // 进程可能已在步骤失败时退出。
            }
        }
    }

    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);

    /// <summary>
    /// 退出观察互斥：Exited 回调（线程池线程）与查询路径的惰性 RefreshAttempt 可能并发，
    /// 没有它同一次退出会被观察两次（双份 launch.exited 事件/两次落库）。
    /// </summary>
    private readonly object _refreshLock = new();

    /// <summary>
    /// feat-1：订阅进程退出事件。EnableRaisingEvents 在 Start 之后设置存在固有窗口
    /// （订阅前已退出则事件丢失），由查询路径的惰性 RefreshAttempt 兜底——
    /// 外部管理器杀进程、外壳启动（SWF）拿不到句柄等场景同理。
    /// </summary>
    private void WatchProcessExit(string attemptId, Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                try
                {
                    using var lease = AcquireLibraryLease?.Invoke();
                    if (AcquireLibraryLease is not null && lease is null) return;
                    if (_attempts.TryGetValue(attemptId, out var current) && current.State == "processCreated")
                    {
                        RefreshAttempt(current);
                    }
                }
                catch (Exception)
                {
                    // 后台回调失败绝不能带崩宿主（如宿主停机后连接已释放）：
                    // 状态留在 processCreated，查询路径的惰性刷新兜底。
                }
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // 订阅失败（个别外壳启动形态）：保持纯惰性观察语义。
        }
    }

    /// <summary>查询时尽力观察：进程已退出则记 exited 并释放互斥（观察不等待，见契约 7.2）。</summary>
    public LaunchAttempt? GetAttempt(string attemptId)
    {
        if (!_attempts.TryGetValue(attemptId, out var attempt))
        {
            return null;
        }

        RefreshAttempt(attempt);
        return _attempts[attemptId];
    }

    public IReadOnlyList<LaunchAttempt> History(string? gameId = null) =>
        _attempts.Values
            .Where(a => gameId is null || string.Equals(a.GameId, gameId, StringComparison.Ordinal))
            .OrderBy(a => a.CreatedUtc)
            .ThenBy(a => a.AttemptId, StringComparer.Ordinal)
            .Select(a =>
            {
                RefreshAttempt(a);
                return _attempts[a.AttemptId];
            })
            .ToArray();

    private LaunchAttempt RefreshAttempt(LaunchAttempt attempt)
    {
        if (_observations.ContainsKey(attempt.AttemptId)) return _attempts.GetValueOrDefault(attempt.AttemptId) ?? attempt;
        if (attempt.State != "processCreated")
        {
            return attempt;
        }

        lock (_refreshLock)
        {
            // 锁内重读：可能已被并发的 Exited 回调/查询先行完成。
            if (!_attempts.TryGetValue(attempt.AttemptId, out var current) || current.State != "processCreated")
            {
                return _attempts.TryGetValue(attempt.AttemptId, out var settled) ? settled : attempt;
            }

            if (!_processes.TryGetValue(attempt.AttemptId, out var process))
            {
                return current;
            }

            process.Refresh();
            if (!process.HasExited)
            {
                return current;
            }

            var observed = current with
            {
                State = "exited",
                ExitCode = process.ExitCode,
                FinishedUtc = DateTime.UtcNow,
            };
            _attempts[attempt.AttemptId] = observed;
            _processes.TryRemove(attempt.AttemptId, out _);
            _activeByGame.TryRemove(observed.GameId, out _);
            NotifyAttempt(observed);
            return observed;
        }
    }

    private static void ValidatePaths(LaunchProfile profile)
    {
        if (!File.Exists(profile.ExecutablePath))
        {
            throw new LaunchException(ErrorCodes.ToolMissing, $"启动目标不存在：{profile.ExecutablePath}");
        }

        var extension = Path.GetExtension(profile.ExecutablePath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".swf", StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchException(ErrorCodes.InvalidPath, "启动目标仅支持 EXE 或 SWF 文件");
        }

        if (!Directory.Exists(profile.WorkingDirectory))
        {
            throw new LaunchException(ErrorCodes.InvalidPath, $"工作目录不存在：{profile.WorkingDirectory}");
        }
    }

    public static Process? StartProcessWithElevationFallback(ProcessStartInfo startInfo, Func<ProcessStartInfo, Process?>? start = null)
    {
        start ??= Process.Start;
        try
        {
            return start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740 && !startInfo.UseShellExecute)
        {
            var elevated = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                WorkingDirectory = startInfo.WorkingDirectory,
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (var argument in startInfo.ArgumentList) elevated.ArgumentList.Add(argument);
            try
            {
                return start(elevated);
            }
            catch (System.ComponentModel.Win32Exception retry) when (retry.NativeErrorCode == 1223)
            {
                throw new LaunchException(ErrorCodes.ProcessStartFailed, "游戏需要管理员权限，但用户取消了 UAC 提示");
            }
        }
    }
}

/// <summary>启动领域错误：携带公开错误码。</summary>
public sealed class LaunchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
