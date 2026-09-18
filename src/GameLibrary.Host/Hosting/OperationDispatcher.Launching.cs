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



/// <summary>OperationDispatcher 的 Launching 域 handler（阶段三按域拆分，partial）。</summary>
public sealed partial class OperationDispatcher
{
    private Envelope<object> ProfilesSetDefault(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "profiles set_default 需要 gameId、profileId 参数");
        }

        try
        {
            var updated = _state.Launches.SetDefault(gameId, profileId);
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

    private Envelope<object> ProfilesRemove(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "缺少 profileId 参数");
        }

        try
        {
            var removed = _state.Launches.RemoveProfile(profileId);
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

    private Envelope<object> ProfilesValidate(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "缺少 profileId 参数");
        }

        var profile = _state.Launches.GetProfile(profileId);
        if (profile is null)
        {
            return NotFound(request, $"Profile 不存在：{profileId}");
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

    /// <summary>内置视图 + 自定义视图（T15-C）；activeViewId 为宿主内存态。</summary>

    private Envelope<object> ProfilesCreate(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "executablePath", out var executablePath)
            || !TryGetStringParameter(request, "cwd", out var cwd))
        {
            return InvalidArgument(request, "profiles.create 需要 gameId、executablePath、cwd 参数");
        }

        if (!TryGetStringListParameter(request, "argv", out var argv))
        {
            return InvalidArgument(request, "profiles.create 需要 argv 字符串数组");
        }

        if (_state.Library.Store?.TryGetGame(gameId) is { Membership: "removed" })
        {
            return InvalidArgument(request, "游戏已从库中移除，不能新增启动方式");
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
            return InvalidArgument(request, "启动目标仅支持 EXE 或 SWF 文件");
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
        TryGetStringParameter(request, "toolId", out var toolId);
        TryGetBoolParameter(request, "isDefault", out var defaultFlag);
        var profile = _state.Launches.AddProfile(gameId, executablePath, argv, cwd, toolId.Length > 0 ? toolId : null, defaultFlag == true);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    private Envelope<object> ProfilesList(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } profileListParameters
            && profileListParameters.TryGetProperty("gameId", out var gameElement)
            && gameElement.ValueKind == JsonValueKind.String)
        {
            gameId = gameElement.GetString();
        }

        var profiles = _state.Launches.ListProfiles(gameId);
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

    private Envelope<object> ProfilesGet(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "缺少 profileId 参数");
        }

        var profile = _state.Launches.GetProfile(profileId);
        if (profile is null)
        {
            return NotFound(request, $"Profile 不存在：{profileId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(profile),
        };
    }

    private Envelope<object> ProfilesUpdate(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "profileId", out var profileId)
            || !TryGetStringParameter(request, "executablePath", out var executablePath)
            || !TryGetStringParameter(request, "cwd", out var cwd)
            || !TryGetStringListParameter(request, "argv", out var argv))
        {
            return InvalidArgument(request, "profiles.update 需要 profileId、executablePath、argv、cwd 参数");
        }

        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
        var current = _state.Launches.GetProfile(profileId);
        if (current is null)
        {
            return NotFound(request, $"Profile 不存在：{profileId}");
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
            return InvalidArgument(request, "启动目标仅支持 EXE 或 SWF 文件");
        }

        if (RejectPathOutsideRoots(request, executablePath) is { } updateOutsideRoot)
        {
            return updateOutsideRoot;
        }

        var updated = _state.Launches.UpdateProfile(profileId, executablePath, argv, cwd);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = ProfileDto(updated),
        };
    }

    private Envelope<object>? RejectInactiveGame(IpcRequest request, string gameId)
    {
        var game = _state.Library.Store?.TryGetGame(gameId);
        return game is { Membership: "removed" }
            ? InvalidArgument(request, $"游戏已从库中移除，不能启动：{gameId}")
            : null;
    }

    private Envelope<object> LaunchPlanHandler(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "gameId", out var gameId)
            || !TryGetStringParameter(request, "profileId", out var profileId))
        {
            return InvalidArgument(request, "launch.plan 需要 gameId、profileId 参数");
        }

        if (RejectInactiveGame(request, gameId) is { } inactive)
        {
            return inactive;
        }

        try
        {
            var plan = _state.Launches.CreatePlan(gameId, profileId);
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

    /// <summary>
    /// T13 Required 不回退（LA-07）：游戏翻译策略有效值为 Required 且目标 Profile 无工具绑定时，
    /// 返回阻断信封；null 表示翻译路由可直启（策略非 Required，或 Profile 已绑定工具）。
    /// </summary>
    private Envelope<object>? TranslationRouteBlock(IpcRequest request, string gameId, string resolvedProfileId)
    {
        var store = _state.Library.Store;
        if (store is null)
        {
            return null;
        }

        var game = store.TryGetGame(gameId);
        var profile = _state.Launches.GetProfile(resolvedProfileId);
        if (game is null || profile is null)
        {
            return null;
        }

        var inherited = game.TranslationInherited
            ? TranslationRequirement.Required
            : TranslationRequirement.Auto;
        var userOverride = game.TranslationOverride is null
            ? TranslationRequirement.Auto
            : Enum.Parse<TranslationRequirement>(game.TranslationOverride, ignoreCase: false);
        if ((userOverride != TranslationRequirement.Auto ? userOverride : inherited) != TranslationRequirement.Required)
        {
            return null;
        }

        if (profile.ToolId is not null)
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
                Message = $"游戏翻译策略为 Required，而 Profile {resolvedProfileId} 是普通直启（无工具绑定）；不静默回退原文直启",
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

    private Envelope<object> LaunchExecute(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "idempotencyKey", out var idempotencyKey))
        {
            return InvalidArgument(request, "launch.execute 需要 idempotencyKey 参数");
        }

        TryGetStringParameter(request, "planId", out var planId);
        TryGetStringParameter(request, "profileId", out var profileId);
        TryGetIntParameter(request, "expectedRevision", out var expectedRevision);

        // T13 Required 不回退：执行前解析目标 Profile（显式 profileId 或计划内的），
        // 游戏 Required 且该 Profile 无工具绑定 → 拒绝执行（LA-07），不产生尝试。
        var resolvedProfileId = profileId.Length > 0
            ? profileId
            : planId.Length > 0
                ? _state.Launches.GetPlanProfileId(planId)
                : null;
        if (resolvedProfileId is not null)
        {
            var resolvedGameId = profileId.Length > 0
                ? _state.Launches.GetProfile(resolvedProfileId)?.GameId
                : _state.Launches.GetPlanGameId(planId);
            if (resolvedGameId is not null)
            {
                if (RejectInactiveGame(request, resolvedGameId) is { } inactive)
                {
                    return inactive;
                }

                var block = TranslationRouteBlock(request, resolvedGameId, resolvedProfileId);
                if (block is not null)
                {
                    return block;
                }
            }
        }

        try
        {
            var attempt = _state.Launches.Execute(
                idempotencyKey,
                planId.Length > 0 ? planId : null,
                profileId.Length > 0 ? profileId : null,
                profileId.Length > 0 ? profileId : null,
                expectedRevision);
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

    private Envelope<object> LaunchStatus(IpcRequest request)
    {
        if (!TryGetStringParameter(request, "attemptId", out var attemptId))
        {
            return InvalidArgument(request, "缺少 attemptId 参数");
        }

        var attempt = _state.Launches.GetAttempt(attemptId);
        if (attempt is null)
        {
            return NotFound(request, $"启动尝试不存在：{attemptId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = attempt.ToDto(),
        };
    }

    private Envelope<object> LaunchHistory(IpcRequest request)
    {
        string? gameId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } historyParameters
            && historyParameters.TryGetProperty("gameId", out var historyGameElement)
            && historyGameElement.ValueKind == JsonValueKind.String)
        {
            gameId = historyGameElement.GetString();
        }

        var attempts = _state.Launches.History(gameId);
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

    private static bool IsSupportedLaunchTarget(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swf", StringComparison.OrdinalIgnoreCase));

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
    /// 显式建库（library.init）。自举豁免前置收据：建库成功后在新库中登记收据，
    /// 同键重放返回原结果；DB 已存在但收据缺失（建库后、收据前中断）返回 AlreadyInitialized。
    /// </summary>
}
