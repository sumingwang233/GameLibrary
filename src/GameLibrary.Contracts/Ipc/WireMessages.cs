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
}

/// <summary>连接握手请求：每连接第一条消息。</summary>
public sealed class HandshakeRequest
{
    public string ApiVersion { get; init; } = ApiConstants.ApiVersion;

    public string? ClientName { get; init; }
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
