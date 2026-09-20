using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Domain.Classification;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 启动域处理器：launch.plan / launch.execute / launch.status / launch.history 四操作 +
/// profiles.create / list / get / update / set_default / remove / validate 七操作 +
/// translation.get / translation.set 两操作，共十三臂（翻译策略与翻译路由助手整域随迁）。
/// Store 经委托每请求取当前值（library.init / backups.restore 会整体替换 Library，
/// 禁止构造时缓存 store 引用）；Launches/Roots/Events 为 init-only 引用
/// （HostRuntimeState 构造后整体不可替换）；域内仅 translation.set 发布 game.updated
/// 事件，launch.*/profiles.* 零事件。由 DispatchCore 调用，天然继承幂等收据
/// （launch.execute / profiles.create / update / set_default / remove / translation.set
/// 在 ReceiptOperations）与串行门、权限、维护模式等中间件；launch.execute 的
/// prepared 收据崩溃恢复特判留在 OperationDispatcher（依赖收据存储与尝试注册表）。
/// </summary>
internal sealed class LaunchingHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly GameLibrary.Host.Launching.LaunchRegistry _launches;
    private readonly RootRegistry _roots;
    private readonly EventStream _events;

    /// <summary>
    /// launches/roots/events 以 init-only 引用直传：HostRuntimeState 构造后整体不可替换
    /// （HostRuntime.cs），内部集合自线程安全；Store 经委托每请求取当前值。
    /// </summary>
    public LaunchingHandler(
        Func<SqliteLibraryStore?> storeAccessor,
        GameLibrary.Host.Launching.LaunchRegistry launches,
        RootRegistry roots,
        EventStream events)
    {
        _storeAccessor = storeAccessor;
        _launches = launches;
        _roots = roots;
        _events = events;
    }

    /// <summary>profiles.set_default：切换默认启动方式（首个 Profile 创建时自动默认，替换走本操作）。</summary>
    public Envelope<object> ProfilesSetDefault(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "profileId", out var profileId))
        {
            return IpcRequests.InvalidArgument(request, "profiles set_default 需要 gameId、profileId 参数");
        }

        try
        {
            var updated = _launches.SetDefault(gameId, profileId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = ProfileDto(updated),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    /// <summary>profiles.remove：删除启动方式；移除默认配置需显式替代项（RecoveryOperation 指向 set_default）。</summary>
    public Envelope<object> ProfilesRemove(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "profileId", out var profileId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 profileId 参数");
        }

        try
        {
            var removed = _launches.RemoveProfile(profileId);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = new { profileId = removed.ProfileId, gameId = removed.GameId, removed = true },
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex) when (ex.Code == ErrorCodes.InvalidArgument)
        {
            // 默认配置移除需明确替代项（契约 3.1）：给出可执行修复入口。
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
                    RecoveryOperation = "profiles.set_default",
                },
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    /// <summary>profiles.validate：本地静态检查（入口/工作目录存在性）；不执行工具能力验证。</summary>
    public Envelope<object> ProfilesValidate(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "profileId", out var profileId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 profileId 参数");
        }

        var profile = _launches.GetProfile(profileId);
        if (profile is null)
        {
            return IpcRequests.NotFound(request, $"Profile 不存在：{profileId}");
        }

        var issues = new List<object>();
        if (!File.Exists(profile.ExecutablePath))
        {
            issues.Add(new { code = "EntryMissing", detail = $"入口不存在：{profile.ExecutablePath}" });
        }

        if (!Directory.Exists(profile.WorkingDirectory))
        {
            issues.Add(new { code = "WorkingDirectoryMissing", detail = $"工作目录不存在：{profile.WorkingDirectory}" });
        }

        if (profile.ToolId is not null)
        {
            issues.Add(new
            {
                code = "ToolUnverified",
                detail = $"绑定工具 {profile.ToolId} 的能力验证状态用 verification.list 查询；validate 不执行工具",
            });
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                profileId = profile.ProfileId,
                gameId = profile.GameId,
                available = issues.Count == 0,
                toolId = profile.ToolId,
                isDefault = profile.IsDefault,
                issues,
            },
        };
    }

    /// <summary>
    /// profiles.create：新增启动方式（入口仅 EXE/SWF，路径必须在已注册库根内）；
    /// 可选工具绑定与 isDefault 标记（首个即默认，替代项走 profiles.set_default）。
    /// </summary>
    public Envelope<object> ProfilesCreate(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "executablePath", out var executablePath)
            || !IpcRequests.TryGetStringParameter(request, "cwd", out var cwd))
        {
            return IpcRequests.InvalidArgument(request, "profiles.create 需要 gameId、executablePath、cwd 参数");
        }

        if (!IpcRequests.TryGetStringListParameter(request, "argv", out var argv))
        {
            return IpcRequests.InvalidArgument(request, "profiles.create 需要 argv 字符串数组");
        }

        if (_storeAccessor()?.TryGetGame(gameId) is { Membership: "removed" })
        {
            return IpcRequests.InvalidArgument(request, "游戏已从库中移除，不能新增启动方式");
        }

        if (!File.Exists(executablePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolMissing,
                    Message = $"启动目标不存在：{executablePath}",
                    Retryable = false,
                },
            };
        }

        if (!IsSupportedLaunchTarget(executablePath))
        {
            return IpcRequests.InvalidArgument(request, "启动目标仅支持 EXE 或 SWF 文件");
        }

        if (RejectPathOutsideRoots(request, executablePath) is { } createOutsideRoot)
        {
            return createOutsideRoot;
        }

        if (!Directory.Exists(cwd))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.InvalidPath,
                    Message = $"工作目录不存在：{cwd}",
                    Retryable = false,
                },
            };
        }

        // T13：可选工具绑定与默认标记（isDefault 首个即默认，替代项走 profiles.set_default）。
        IpcRequests.TryGetStringParameter(request, "toolId", out var toolId);
        IpcRequests.TryGetBoolParameter(request, "isDefault", out var defaultFlag);
        var profile = _launches.AddProfile(gameId, executablePath, argv, cwd, toolId.Length > 0 ? toolId : null, defaultFlag == true);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    /// <summary>profiles.list：按 gameId 过滤或全量列出，保持注册表返回顺序。</summary>
    public Envelope<object> ProfilesList(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } profileListParameters
            && profileListParameters.TryGetProperty("gameId", out var gameElement)
            && gameElement.ValueKind == JsonValueKind.String)
        {
            gameId = gameElement.GetString();
        }

        var profiles = _launches.ListProfiles(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = profiles.Count,
                items = profiles.Select(ProfileDto).ToArray(),
            },
        };
    }

    /// <summary>profiles.get：查单个启动方式。</summary>
    public Envelope<object> ProfilesGet(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "profileId", out var profileId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 profileId 参数");
        }

        var profile = _launches.GetProfile(profileId);
        if (profile is null)
        {
            return IpcRequests.NotFound(request, $"Profile 不存在：{profileId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    /// <summary>profiles.update：整体更新（expectedRevision 可选乐观校验；入口限制与边界校验同 create）。</summary>
    public Envelope<object> ProfilesUpdate(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "profileId", out var profileId)
            || !IpcRequests.TryGetStringParameter(request, "executablePath", out var executablePath)
            || !IpcRequests.TryGetStringParameter(request, "cwd", out var cwd)
            || !IpcRequests.TryGetStringListParameter(request, "argv", out var argv))
        {
            return IpcRequests.InvalidArgument(request, "profiles.update 需要 profileId、executablePath、argv、cwd 参数");
        }

        IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
        var current = _launches.GetProfile(profileId);
        if (current is null)
        {
            return IpcRequests.NotFound(request, $"Profile 不存在：{profileId}");
        }

        if (expectedRevision is not null && expectedRevision.Value != current.Revision)
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.RevisionConflict,
                    Message = $"Profile Revision 不一致：期望 {expectedRevision}，当前 {current.Revision}",
                    Retryable = false,
                },
            };
        }

        if (!File.Exists(executablePath))
        {
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolMissing,
                    Message = $"启动目标不存在：{executablePath}",
                    Retryable = false,
                },
            };
        }

        if (!IsSupportedLaunchTarget(executablePath))
        {
            return IpcRequests.InvalidArgument(request, "启动目标仅支持 EXE 或 SWF 文件");
        }

        if (RejectPathOutsideRoots(request, executablePath) is { } updateOutsideRoot)
        {
            return updateOutsideRoot;
        }

        var updated = _launches.UpdateProfile(profileId, executablePath, argv, cwd);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(updated),
        };
    }

    /// <summary>已移除库籍的游戏拒绝启动类操作（create 前置 / plan 与 execute 前置共用）。</summary>
    private Envelope<object>? RejectInactiveGame(IpcRequest request, string gameId)
    {
        var game = _storeAccessor()?.TryGetGame(gameId);
        return game is { Membership: "removed" }
            ? IpcRequests.InvalidArgument(request, $"游戏已从库中移除，不能启动：{gameId}")
            : null;
    }

    /// <summary>
    /// launch.plan：预演启动（无副作用）；Required 且无可自动执行的翻译路由时不执行、
    /// 返回 NeedsUserAction 并给出修复入口（tools.discover / translation.set）。
    /// </summary>
    public Envelope<object> LaunchPlanHandler(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
            || !IpcRequests.TryGetStringParameter(request, "profileId", out var profileId))
        {
            return IpcRequests.InvalidArgument(request, "launch.plan 需要 gameId、profileId 参数");
        }

        if (RejectInactiveGame(request, gameId) is { } inactive)
        {
            return inactive;
        }

        try
        {
            var plan = _launches.CreatePlan(gameId, profileId);
            var block = TranslationRouteBlock(request, gameId, plan.ProfileId);
            if (block is not null)
            {
                // 预览无副作用：计划照常返回，但明确 needsUserAction 与后续步骤，不让 agent 误以为可直接执行。
                return new Envelope<object>
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Status = OperationStatus.NeedsUserAction,
                    Data = plan.ToDto(),
                    NextActions =
                    [
                        new NextAction
                        {
                            OperationId = "tools.discover",
                            Reason = "游戏翻译策略为 Required；目标 Profile 未绑定翻译工具，直启会被拒绝",
                        },
                        new NextAction
                        {
                            OperationId = "translation.set",
                            Reason = "如需原文直启，请显式将策略覆盖为 NotRequired（用户主动选择，不静默回退）",
                        },
                    ],
                };
            }

            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = plan.ToDto(),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    /// <summary>解析翻译路由：游戏或 Profile 缺失、库未初始化均返回 null（交由调用方决定语义）。</summary>
    private GameLibrary.Host.Launching.TranslationRouteResolution? ResolveTranslationRoute(
        string gameId,
        string resolvedProfileId)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return null;
        }

        var game = store.TryGetGame(gameId);
        var profile = _launches.GetProfile(resolvedProfileId);
        if (game is null || profile is null)
        {
            return null;
        }

        return GameLibrary.Host.Launching.TranslationLaunchRouteResolver.Resolve(game, profile);
    }

    /// <summary>
    /// Required 不回退：没有显式工具绑定时，优先使用游戏目录中可验证的自动翻译配方；
    /// 没有配方才阻断，不把原始 EXE 当作翻译启动。
    /// </summary>
    private Envelope<object>? TranslationRouteBlock(IpcRequest request, string gameId, string resolvedProfileId)
    {
        var resolution = ResolveTranslationRoute(gameId, resolvedProfileId);
        if (resolution is null
            || !resolution.IsRequired
            || resolution.SatisfiedByProfileBinding
            || resolution.Route is not null)
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
                Code = ErrorCodes.TranslationRouteUnavailable,
                Message = $"游戏需要翻译，但没有可自动执行的翻译路由：{resolution.UnavailableReason}。已配置的 Profile {resolvedProfileId} 仍是原始游戏入口；不静默回退原文直启",
                Retryable = false,
            },
            NextActions =
            [
                new NextAction
                {
                    OperationId = "tools.discover",
                    Reason = "发现并绑定翻译工具（MTool/RenpyThief/播放器/steam）后创建翻译 Profile",
                },
                new NextAction
                {
                    OperationId = "translation.set",
                    Reason = "用户主动选择原文直启时，显式将策略覆盖为 NotRequired",
                },
            ],
        };
    }

    /// <summary>
    /// launch.execute：执行启动（幂等语义由 DispatchWithReceipt 收据中间件保证）。
    /// T13 Required 不回退：执行前解析目标 Profile（显式 profileId 或计划内的），
    /// 游戏 Required 且该 Profile 无工具绑定 → 拒绝执行（LA-07），不产生尝试。
    /// </summary>
    public Envelope<object> LaunchExecute(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "idempotencyKey", out var idempotencyKey))
        {
            return IpcRequests.InvalidArgument(request, "launch.execute 需要 idempotencyKey 参数");
        }

        IpcRequests.TryGetStringParameter(request, "planId", out var planId);
        IpcRequests.TryGetStringParameter(request, "profileId", out var profileId);
        IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision);

        // T13 Required 不回退：执行前解析目标 Profile（显式 profileId 或计划内的），
        // 游戏 Required 且该 Profile 无工具绑定 → 拒绝执行（LA-07），不产生尝试。
        var resolvedProfileId = profileId.Length > 0
            ? profileId
            : planId.Length > 0
                ? _launches.GetPlanProfileId(planId)
                : null;
        GameLibrary.Host.Launching.TranslationLaunchRoute? translationRoute = null;
        if (resolvedProfileId is not null)
        {
            var resolvedGameId = profileId.Length > 0
                ? _launches.GetProfile(resolvedProfileId)?.GameId
                : _launches.GetPlanGameId(planId);
            if (resolvedGameId is not null)
            {
                if (RejectInactiveGame(request, resolvedGameId) is { } inactive)
                {
                    return inactive;
                }

                var resolution = ResolveTranslationRoute(resolvedGameId, resolvedProfileId);
                var block = TranslationRouteBlock(request, resolvedGameId, resolvedProfileId);
                if (block is not null)
                {
                    return block;
                }

                translationRoute = resolution?.Route;
            }
        }

        try
        {
            var attempt = _launches.Execute(
                idempotencyKey,
                planId.Length > 0 ? planId : null,
                profileId.Length > 0 ? profileId : null,
                profileId.Length > 0 ? profileId : null,
                expectedRevision,
                translationRoute?.Steps);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = true,
                Status = OperationStatus.Completed,
                Data = attempt.ToDto(),
            };
        }
        catch (GameLibrary.Host.Launching.LaunchException ex)
        {
            return LaunchError(request, ex);
        }
    }

    /// <summary>launch.status：查启动尝试现状（进程存活由注册表后台跟踪）。</summary>
    public Envelope<object> LaunchStatus(IpcRequest request)
    {
        if (!IpcRequests.TryGetStringParameter(request, "attemptId", out var attemptId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 attemptId 参数");
        }

        var attempt = _launches.GetAttempt(attemptId);
        if (attempt is null)
        {
            return IpcRequests.NotFound(request, $"启动尝试不存在：{attemptId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = attempt.ToDto(),
        };
    }

    /// <summary>launch.history：按 gameId 过滤或全量启动历史。</summary>
    public Envelope<object> LaunchHistory(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } historyParameters
            && historyParameters.TryGetProperty("gameId", out var historyGameElement)
            && historyGameElement.ValueKind == JsonValueKind.String)
        {
            gameId = historyGameElement.GetString();
        }

        var attempts = _launches.History(gameId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new
            {
                total = attempts.Count,
                items = attempts.Select(a => a.ToDto()).ToArray(),
            },
        };
    }

    /// <summary>translation.get：继承值与用户覆盖分离返回；有效值 = 覆盖优先（策划案 7.4）。</summary>
    public Envelope<object> TranslationGet(IpcRequest request)
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

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TranslationDto(game),
        };
    }

    /// <summary>
    /// translation.set：只写用户覆盖层（Auto/Required/NotRequired），继承值不动；
    /// Required 不因覆盖缺失而回退为直启（回退需显式 NotRequired）。
    /// </summary>
    public Envelope<object> TranslationSet(IpcRequest request)
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

        if (!IpcRequests.TryGetStringParameter(request, "override", out var overrideText)
            || !Enum.TryParse<TranslationRequirement>(overrideText, ignoreCase: false, out var overrideValue))
        {
            return IpcRequests.InvalidArgument(request, "override 必须是 Auto/Required/NotRequired（区分大小写）");
        }

        if (!IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision) || expectedRevision is null)
        {
            return IpcRequests.InvalidArgument(request, "缺少 expectedRevision 参数");
        }

        var game = store.TryGetGame(gameId);
        if (game is null)
        {
            return IpcRequests.NotFound(request, $"游戏不存在：{gameId}");
        }

        var storedOverride = overrideValue == TranslationRequirement.Auto ? null : overrideValue.ToString();
        var newRevision = store.SetTranslationOverride(gameId, storedOverride, expectedRevision.Value, DateTime.UtcNow);
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

        var updatedGame = store.TryGetGame(gameId)!;
        _events.Publish("game.updated", $"game:{gameId}", new { gameId, revision = newRevision, translation = TranslationDto(updatedGame) }, DateTime.UtcNow);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = TranslationDto(updatedGame),
        };
    }

    /// <summary>启动方式的对外 DTO（形状不可变更：profileId/gameId/executablePath/argv/cwd/toolId/isDefault/revision）。</summary>
    private static object ProfileDto(GameLibrary.Host.Launching.LaunchProfile profile) => new
    {
        profileId = profile.ProfileId,
        gameId = profile.GameId,
        executablePath = profile.ExecutablePath,
        argv = profile.Arguments,
        cwd = profile.WorkingDirectory,
        toolId = profile.ToolId,
        isDefault = profile.IsDefault,
        revision = profile.Revision,
    };

    /// <summary>翻译策略的对外 DTO（形状不可变更：覆盖/继承/有效值分离）。</summary>
    private static object TranslationDto(GameCard game)
    {
        var inherited = game.TranslationInherited
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = game.TranslationOverride is null
            ? TranslationRequirement.Auto
            : Enum.Parse<TranslationRequirement>(game.TranslationOverride, ignoreCase: false);
        var policy = TranslationPolicy.FromInheritance(
            new FolderClassification([], inherited == TranslationRequirement.Required, game.TranslationInherited ? "[toolNeed]" : null, ClassificationRules.CurrentVersion))
            with
        { UserOverride = userOverride };
        return new
        {
            gameId = game.GameId,
            userOverride = userOverride.ToString(),
            inherited = inherited.ToString(),
            inheritedFrom = game.TranslationInherited ? "[toolNeed]" : null,
            effective = policy.Effective.ToString(),
            isRequired = policy.IsRequired,
            revision = game.Revision,
        };
    }

    private static bool IsSupportedLaunchTarget(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swf", StringComparison.OrdinalIgnoreCase));

    /// <summary>启动域异常统一转失败信封（Retryable=false，码与文案透传）。</summary>
    private static Envelope<object> LaunchError(IpcRequest request, GameLibrary.Host.Launching.LaunchException ex) =>
        new()
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

    /// <summary>
    /// 路径包含校验（CWE-22 边界）：调用方路径必须在已注册库根内。与
    /// OperationDispatcher.RejectPathOutsideRoots 双份同构（先例 IgnoreRulesHandler），
    /// 错误码/文案保持一致；待剩余域拆完在收尾片收敛到共享处。
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
