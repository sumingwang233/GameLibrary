using System.Buffers.Binary;
using System.Text;

namespace GameLibrary.Contracts.Ipc;

/// <summary>
/// IPC 消息帧：4 字节大端长度前缀 + UTF-8 JSON 载荷，上限 8 MiB，容纳 5 MiB 图片的 Base64。
/// </summary>
public static class IpcFrame
{
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC 消息超过上限 {MaxMessageBytes} 字节");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(stream, prefix, ct))
        {
            throw new EndOfStreamException("IPC 对端在帧头前关闭");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC 帧长度非法：{length}");
        }

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct))
        {
            throw new EndOfStreamException("IPC 对端在载荷中途关闭");
        }

        return payload;
    }

    public static Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct) =>
        WriteAsync(stream, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options), ct);

    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        var payload = await ReadAsync(stream, ct);
        return System.Text.Json.JsonSerializer.Deserialize<T>(payload, ContractJson.Options)
            ?? throw new InvalidDataException("IPC JSON 载荷反序列化为 null");
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
