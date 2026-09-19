using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Infrastructure.Persistence;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 工具验证域处理器：verification.start / report / invalidate / get / list 五操作。
/// 验证记录绑定工具指纹——指纹变化即失效（返回 ToolChanged 并把记录置 Unknown）；
/// 双结论（游戏启动/翻译生效）单向 OR 累积不可逆，语义由
/// ToolVerificationRules.ApplyObservation 收口。Store 经委托每请求取当前值
/// （library.init / backups.restore 会整体替换 Library，禁止构造时缓存 store 引用）；
/// 域内不发布事件，不注入 EventStream。由 DispatchCore 调用，天然继承幂等收据
/// （verification.start/report/invalidate 在 ReceiptOperations）与串行门等中间件。
/// </summary>
internal sealed class VerificationHandler
{
    private readonly Func<SqliteLibraryStore?> _storeAccessor;

    public VerificationHandler(Func<SqliteLibraryStore?> storeAccessor)
    {
        _storeAccessor = storeAccessor;
    }

    /// <summary>开始一次工具验证（T08）：绑定当前指纹与隔离样本，状态 Unknown。</summary>
    public Envelope<object> VerificationStart(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "toolId", out var toolId)
            || !IpcRequests.TryGetStringParameter(request, "fingerprint", out var fingerprint)
            || !IpcRequests.TryGetStringParameter(request, "engine", out var engine)
            || !IpcRequests.TryGetStringParameter(request, "samplePath", out var samplePath))
        {
            return IpcRequests.InvalidArgument(request, "verification.start 需要 toolId、fingerprint、engine、samplePath 参数");
        }

        var record = new GameLibrary.Domain.Tools.ToolVerificationRecord
        {
            RecordId = $"verif-{Guid.NewGuid():N}",
            ToolId = toolId,
            ToolFingerprint = fingerprint,
            Engine = engine,
            SamplePath = samplePath,
            Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.InsertVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    /// <summary>提交验证观察（双结论分开累积；翻译生效必须先有游戏启动证据）。</summary>
    public Envelope<object> VerificationReport(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "recordId", out var recordId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return IpcRequests.NotFound(request, $"验证记录不存在：{recordId}");
        }

        IpcRequests.TryGetBoolParameter(request, "gameStarted", out var gameStarted);
        IpcRequests.TryGetBoolParameter(request, "translationConfirmed", out var translationConfirmed);

        // 指纹校验：工具更新/换目录后旧记录失效，需重新验证。
        if (IpcRequests.TryGetStringParameter(request, "fingerprint", out var fingerprint)
            && !string.Equals(fingerprint, record.ToolFingerprint, StringComparison.Ordinal))
        {
            record = record with
            {
                Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
                Note = "工具指纹变化，历史验证失效",
                UpdatedUtc = DateTime.UtcNow,
            };
            store.UpdateVerification(record);
            return new Envelope<object>
            {
                RequestId = request.RequestId,
                Ok = false,
                Status = OperationStatus.Failed,
                Error = new RequestError
                {
                    Code = ErrorCodes.ToolChanged,
                    Message = "工具指纹与验证记录不一致；记录已失效，请重新验证",
                    Retryable = false,
                },
            };
        }

        var newStatus = GameLibrary.Domain.Tools.ToolVerificationRules.ApplyObservation(
            record.Status, gameStartedConfirmed: gameStarted == true, translationConfirmed: translationConfirmed == true);
        record = record with
        {
            Status = newStatus,
            GameStartedConfirmed = record.GameStartedConfirmed || gameStarted == true,
            TranslationConfirmed = record.TranslationConfirmed || translationConfirmed == true,
            UpdatedUtc = DateTime.UtcNow,
        };
        store.UpdateVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    /// <summary>使验证记录失效（工具更新/用户撤销）。</summary>
    public Envelope<object> VerificationInvalidate(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "recordId", out var recordId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return IpcRequests.NotFound(request, $"验证记录不存在：{recordId}");
        }

        record = record with
        {
            Status = GameLibrary.Domain.Tools.ToolVerificationStatus.Unknown,
            Note = "验证已失效（invalidate）",
            UpdatedUtc = DateTime.UtcNow,
        };
        store.UpdateVerification(record);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    public Envelope<object> VerificationGet(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        if (!IpcRequests.TryGetStringParameter(request, "recordId", out var recordId))
        {
            return IpcRequests.InvalidArgument(request, "缺少 recordId 参数");
        }

        var record = store.TryGetVerification(recordId);
        if (record is null)
        {
            return IpcRequests.NotFound(request, $"验证记录不存在：{recordId}");
        }

        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = VerificationDto(record),
        };
    }

    public Envelope<object> VerificationList(IpcRequest request)
    {
        var store = _storeAccessor();
        if (store is null)
        {
            return IpcRequests.InvalidArgument(request, "库未初始化（先 library.init）");
        }

        string? toolId = null;
        if (request.Parameters is { ValueKind: JsonValueKind.Object } vlParameters
            && vlParameters.TryGetProperty("toolId", out var toolElement)
            && toolElement.ValueKind == JsonValueKind.String)
        {
            toolId = toolElement.GetString();
        }

        var records = store.ListVerifications(toolId);
        return new Envelope<object>
        {
            RequestId = request.RequestId,
            Ok = true,
            Status = OperationStatus.Completed,
            Data = new { total = records.Count, items = records.Select(VerificationDto).ToArray() },
        };
    }

    private static object VerificationDto(GameLibrary.Domain.Tools.ToolVerificationRecord record) => new
    {
        recordId = record.RecordId,
        toolId = record.ToolId,
        toolFingerprint = record.ToolFingerprint,
        engine = record.Engine,
        samplePath = record.SamplePath,
        status = record.Status,
        gameStartedConfirmed = record.GameStartedConfirmed,
        translationConfirmed = record.TranslationConfirmed,
        note = record.Note,
        createdUtc = record.CreatedUtc.ToString("O"),
        updatedUtc = record.UpdatedUtc.ToString("O"),
    };
}
