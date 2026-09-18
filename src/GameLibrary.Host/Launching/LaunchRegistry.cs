using System.Collections.Concurrent;
using System.Diagnostics;
using GameLibrary.Contracts;

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

    public bool IsTerminal => State is "exited" or "processStartFailed";

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
public sealed class LaunchRegistry
{
    private readonly ConcurrentDictionary<string, LaunchProfile> _profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LaunchPlan> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LaunchAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _receiptByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByGame = new(StringComparer.Ordinal);

    /// <summary>
    /// 尝试状态变更回调（v1 审查修复：启动历史持久化）——Profile 变更/尝试状态
    /// 每次迁移后触发，Host 接线到运行态持久层。
    /// </summary>
    public Action<LaunchAttempt>? OnAttemptChanged { get; set; }

    /// <summary>Profile 变更回调（create/update/set_default/remove 后触发，供持久化）。</summary>
    public Action<LaunchProfile?>? OnProfileChanged { get; set; }

    private void NotifyAttempt(LaunchAttempt attempt) => OnAttemptChanged?.Invoke(attempt);

    /// <summary>启动恢复：把持久层 Profile 注册回内存（不触发回调）。</summary>
    public void RestoreProfile(LaunchProfile profile) => _profiles[profile.ProfileId] = profile;

    /// <summary>启动恢复：把持久层尝试注册回内存；非终态尝试在重启后直接记为 exited（进程已不属于本生命周期）。</summary>
    public void RestoreAttempt(LaunchAttempt attempt)
    {
        if (!attempt.IsTerminal)
        {
            attempt = attempt with
            {
                State = "exited",
                FinishedUtc = attempt.FinishedUtc ?? DateTime.UtcNow,
                Error = attempt.Error ?? "宿主进程重启时启动尚未完成",
            };
        }

        _attempts[attempt.AttemptId] = attempt;
    }

    public LaunchProfile AddProfile(
        string gameId,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? toolId = null,
        bool isDefault = false)
    {
        var profile = new LaunchProfile
        {
            ProfileId = $"profile-{Guid.NewGuid():N}",
            GameId = gameId,
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            ToolId = toolId,
            IsDefault = isDefault,
            Revision = 1,
        };
        _profiles[profile.ProfileId] = profile;
        if (isDefault)
        {
            ClearOtherDefaults(gameId, profile.ProfileId);
        }

        OnProfileChanged?.Invoke(profile);
        return profile;
    }

    public LaunchProfile? GetProfile(string profileId) =>
        _profiles.TryGetValue(profileId, out var profile) ? profile : null;

    /// <summary>计划归属的 ProfileId（launch.execute 阻断检查用）；计划不存在返回 null。</summary>
    public string? GetPlanProfileId(string planId) =>
        _plans.TryGetValue(planId, out var plan) ? plan.ProfileId : null;

    /// <summary>计划归属的 GameId；计划不存在返回 null。</summary>
    public string? GetPlanGameId(string planId) =>
        _plans.TryGetValue(planId, out var plan) ? plan.GameId : null;

    public IReadOnlyList<LaunchProfile> ListProfiles(string? gameId = null) =>
        _profiles.Values
            .Where(p => gameId is null || string.Equals(p.GameId, gameId, StringComparison.Ordinal))
            .OrderBy(p => p.IsDefault ? 0 : 1)
            .ThenBy(p => p.ProfileId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>该游戏当前默认 Profile；无默认时为 null。</summary>
    public LaunchProfile? GetDefaultProfile(string gameId) =>
        ListProfiles(gameId).FirstOrDefault(p => p.IsDefault);

    public LaunchProfile UpdateProfile(string profileId, string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var current = _profiles[profileId];
        var updated = current with
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Revision = current.Revision + 1,
        };
        _profiles[profileId] = updated;
        OnProfileChanged?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// 设为该游戏默认（profiles.set_default）：显式替代项语义——新默认生效即清除旧默认。
    /// Profile 不存在或属于其他游戏抛 <see cref="LaunchException"/>。
    /// </summary>
    public LaunchProfile SetDefault(string gameId, string profileId)
    {
        var profile = _profiles.TryGetValue(profileId, out var p) ? p : null;
        if (profile is null || !string.Equals(profile.GameId, gameId, StringComparison.Ordinal))
        {
            throw new LaunchException(ErrorCodes.NotFound, $"Profile 不存在或不属于该游戏：{profileId}");
        }

        if (!profile.IsDefault)
        {
            var updated = profile with { IsDefault = true, Revision = profile.Revision + 1 };
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
    public LaunchProfile RemoveProfile(string profileId)
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

        _profiles.TryRemove(profileId, out _);
        OnProfileChanged?.Invoke(null);
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
        int? expectedRevision)
    {
        if (_receiptByKey.TryGetValue(idempotencyKey, out var existingAttemptId))
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
        if (!_receiptByKey.TryAdd(idempotencyKey, attempt.AttemptId))
        {
            // 并发相同键：返回已注册的收据，不启动第二个进程。
            return _attempts[_receiptByKey[idempotencyKey]];
        }

        _activeByGame[plan.GameId] = attempt.AttemptId;
        attempt = attempt with { State = "executing" };
        _attempts[attempt.AttemptId] = attempt;
        NotifyAttempt(attempt);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.ExecutablePath,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = Path.GetExtension(plan.ExecutablePath)
                    .Equals(".swf", StringComparison.OrdinalIgnoreCase),
            };
            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo)
                ?? throw new LaunchException(ErrorCodes.ProcessStartFailed, "进程启动返回空");
            attempt = attempt with
            {
                State = "processCreated",
                ProcessId = process.Id,
                ProcessStartedUtc = process.StartTime.ToUniversalTime(),
            };
            _attempts[attempt.AttemptId] = attempt;
            _processes[attempt.AttemptId] = process;
            NotifyAttempt(attempt);
            return attempt;
        }
        catch (Exception ex) when (ex is not LaunchException)
        {
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
    }

    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);

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
        if (attempt.State != "processCreated" || !_processes.TryGetValue(attempt.AttemptId, out var process))
        {
            return attempt;
        }

        process.Refresh();
        if (!process.HasExited)
        {
            return attempt;
        }

        var observed = attempt with
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
}

/// <summary>启动领域错误：携带公开错误码。</summary>
public sealed class LaunchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
