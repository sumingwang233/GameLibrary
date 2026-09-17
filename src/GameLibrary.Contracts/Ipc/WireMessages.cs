using System.Text.Json;

namespace GameLibrary.Contracts.Ipc;

/// <summary>IPC 请求（内部协议，非公开边界）。公开边界是 CLI/MCP/schema。</summary>
public sealed class IpcRequest
{
    public string RequestId { get; init; } = "";

    public string ApiVersion { get; init; } = ApiConstants.ApiVersion;

    public string OperationId { get; init; } = "";

    public JsonElement? Parameters { get; init; }

    /// <summary>调用方声明的标签，仅作审计，不是身份认证。宿主以握手声明回填。</summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// 调用方持有的库实例 ID（握手时下发、变更请求随发回传）。
    /// 与宿主当前实例不一致时请求被拒绝（LibraryInstanceMismatch），防止旧客户端写错库。
    /// </summary>
    public string? LibraryInstanceId { get; set; }

    /// <summary>
    /// 调用方期望的数据纪元。备份恢复会续期纪元：携带旧纪元的变更请求一律
    /// DataEpochMismatch 拒绝，客户端必须重连获取新纪元后重试。
    /// </summary>
    public string? ExpectedDataEpoch { get; set; }

    /// <summary>握手声明的权限集合回填（null=不限权，保留给第一方 CLI/Desktop）。</summary>
    public IReadOnlyList<string>? GrantedPermissions { get; set; }
}

/// <summary>连接握手请求：每连接第一条消息。</summary>
public sealed class HandshakeRequest
{
    public string ApiVersion { get; init; } = ApiConstants.ApiVersion;

    public string? ClientName { get; init; }

    /// <summary>
    /// 客户端声明的权限集合（契约 access.*：同用户下不同 agent/MCP 客户端可被限制读写范围）。
    /// null/缺省 = 不限权（第一方 CLI/Desktop）；声明后宿主按操作 catalog 的 permission 逐请求校验。
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>连接握手响应；携带库实例与数据纪元，客户端后续变更必须校验。</summary>
public sealed class HandshakeResponse
{
    public string ApiVersion { get; init; } = ApiConstants.ApiVersion;

    public string HostInstanceId { get; init; } = "";

    /// <summary>尚未执行 library.init 时为 null。</summary>
    public string? LibraryInstanceId { get; init; }

    public string? DataEpoch { get; init; }

    public bool LibraryInitialized { get; init; }

    public string AppVersion { get; init; } = "";
}
