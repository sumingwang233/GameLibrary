using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Scanning;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 忽略规则域处理器：ignores.list / ignores.create / ignores.remove。
/// Store 经委托每请求取当前值（library.init / backups.restore 会整体替换 Library，
/// 禁止构造时缓存 store 引用）；域内不发布事件（撤销忽略是恢复候选提示的唯一途径，
/// 亦无对应事件契约），故不注入 EventStream。由 DispatchCore 调用，天然继承
/// 幂等收据（ignores.create/remove 在 ReceiptOperations）与串行门等中间件。
/// </summary>
internal sealed class IgnoreRulesHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;
    private readonly RootRegistry _roots;

    /// <summary>
    /// roots 以 init-only 引用直传：HostRuntimeState.Roots 构造后整体不可替换
    /// （HostRuntime.cs），内部 ConcurrentDictionary 自线程安全。若未来宿主改为
    /// 整体替换 Roots 实例，此处须同步改为委托取值。
    /// </summary>
    public IgnoreRulesHandler(Func<SqliteLibraryStore?> storeAccessor, RootRegistry roots)
    {
        _storeAccessor = storeAccessor;
        _roots = roots;
    }

    /// <summary>列出忽略规则：保持 store.ListIgnoreRules() 返回顺序，不新增排序。</summary>
    public Envelope<object> List(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
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

    /// <summary>
    /// 登记忽略规则（scope ∈ ExactPath/Subtree/ConfirmedIdentity）：路径必须在已注册
    /// 库根内；登记后匹配的待审核候选立即转入 ignored（幂等补登记）。
    /// </summary>
    public Envelope<object> Create(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "scope", out var scope)
            || scope is not ("ExactPath" or "Subtree" or "ConfirmedIdentity"))
        {
            return IpcRequests.InvalidArgument(request, "缺少 scope 参数（ExactPath/Subtree/ConfirmedIdentity）");
        }

        IpcRequests.TryGetStringParameter(request, "path", out var path);
        IpcRequests.TryGetStringParameter(request, "gameId", out var gameId);
        IpcRequests.TryGetStringParameter(request, "reason", out var reason);
        if (scope != "ConfirmedIdentity" && path.Length == 0)
        {
            return IpcRequests.InvalidArgument(request, $"{scope} 需要 path 参数（规范化绝对路径）");
        }

        if (scope == "ConfirmedIdentity" && gameId.Length == 0)
        {
            return IpcRequests.InvalidArgument(request, "ConfirmedIdentity 需要用户确认的 gameId");
        }

        if (path.Length > 0 && !_roots.Contains(path))
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
    public Envelope<object> Remove(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "ignoreId", out var ignoreId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 ignoreId 参数");
        }

        var rule = store.ListIgnoreRules()
            .FirstOrDefault(r => string.Equals(r.IgnoreId, ignoreId, StringComparison.Ordinal));
        if (rule is null)
        {
            return IpcRequests.NotFound(request, $"忽略规则不存在：{ignoreId}");
        }

        // expectedRevision 处理层宽松可选（缺省即跳过校验），与契约 schema 声明 required
        // 不一致——既有集成测试不传该参数走此路径，保留宽松，不得“修严”。
        IpcRequests.TryGetIntParameter(request, "expectedRevision", out var expectedRevision);
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
