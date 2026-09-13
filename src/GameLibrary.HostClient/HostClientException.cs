namespace GameLibrary.HostClient;

/// <summary>HostClient 协议/连接异常，携带公开错误码供三入口映射退出码。</summary>
public sealed class HostClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class HostClientErrorCodes
{
    public const string HostUnavailable = "HostUnavailable";
    public const string HostVersionMismatch = "HostVersionMismatch";
    public const string ProtocolError = "ProtocolError";
}
