using System.Buffers.Binary;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Scanning;

namespace GameLibrary.Host.Scanning;

/// <summary>自动补齐不覆盖；显式切换先保存历史，再原子更新 PNG（WebP 保留原格式）。</summary>
public static class GameCoverService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    public const int MaxBytes = 5 * 1024 * 1024;

    public static (byte[] Bytes, string Extension) PrepareImage(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new ArgumentException("图片必须在 1 字节至 5 MiB 内");
        if (bytes.AsSpan().StartsWith("RIFF"u8))
        {
            if (bytes.Length < 12 || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != bytes.Length - 8
                || !ValidateWebPChunks(bytes.AsSpan(12)))
                throw new ArgumentException("WebP 容器或图片头无效");
            // ponytail: 仅校验容器和尺寸，保留原压缩流；有原生 WebP decoder 后再做像素解码。
            return (bytes, ".webp");
        }
        using var input = new MemoryStream(bytes, writable: false);
        using var image = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
        CheckDimensions(image.Width, image.Height);
        using var output = new MemoryStream();
        // 直接转码保留解码像素；DrawImage 的预乘 alpha 会舍入半透明像素的 RGB。
        image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        if (output.Length > MaxBytes) throw new ArgumentException("解码后的 PNG 超过 5 MiB 上限");
        return (output.ToArray(), ".png");
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 32_000_000)
            throw new ArgumentException("图片尺寸超过 8192 边长或 3200 万像素上限");
    }

    private static int UInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static bool ValidateWebPChunks(ReadOnlySpan<byte> chunks, bool frame = false)
    {
        var hasImage = false;
        var extended = false;
        while (!chunks.IsEmpty)
        {
            if (chunks.Length < 8) throw new ArgumentException("WebP chunk 头被截断");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunks.Slice(4, 4));
            var paddedSize = (long)size + (size & 1);
            if (paddedSize > chunks.Length - 8) throw new ArgumentException("WebP chunk 长度越界");
            var data = chunks.Slice(8, (int)size);
            if ((size & 1) != 0 && chunks[8 + (int)size] != 0) throw new ArgumentException("WebP padding 无效");
            if (chunks.StartsWith("VP8X"u8))
            {
                if (frame || extended || data.Length != 10 || (data[0] & 0xc1) != 0 || UInt24(data.Slice(1, 3)) != 0)
                    throw new ArgumentException("WebP VP8X 头无效");
                CheckDimensions(UInt24(data.Slice(4, 3)) + 1, UInt24(data.Slice(7, 3)) + 1);
                extended = true;
            }
            else if (chunks.StartsWith("VP8L"u8))
            {
                if (data.Length < 6 || data[0] != 0x2f) throw new ArgumentException("WebP VP8L 头无效");
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
                if ((bits >> 29) != 0) throw new ArgumentException("WebP VP8L 版本无效");
                CheckDimensions((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
                hasImage = true;
            }
            else if (chunks.StartsWith("VP8 "u8))
            {
                if (data.Length < 11 || (data[0] & 1) != 0 || data[3] != 0x9d || data[4] != 1 || data[5] != 0x2a)
                    throw new ArgumentException("WebP VP8 关键帧头无效");
                CheckDimensions(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)) & 0x3fff);
                hasImage = true;
            }
            else if (chunks.StartsWith("ANMF"u8))
            {
                if (frame || !extended || data.Length < 24) throw new ArgumentException("WebP 动画帧头无效");
                CheckDimensions(UInt24(data.Slice(6, 3)) + 1, UInt24(data.Slice(9, 3)) + 1);
                hasImage |= ValidateWebPChunks(data[16..], frame: true);
            }
            chunks = chunks[(8 + (int)paddedSize)..];
        }
        return hasImage;
    }

    public static string? Synchronize(SqliteLibraryStore store, GameCard game, bool replaceExisting = false)
        => store.WithWriteLock(() => SynchronizeCore(store, game, replaceExisting));

    private static string? SynchronizeCore(SqliteLibraryStore store, GameCard game, bool replaceExisting)
    {
        if (game.Membership != "active" || DirectoryWalker.IsSystemDirectory(game.RootPath)) return null;
        var directory = game.Kind switch
        {
            "manualShortcut" => Path.GetDirectoryName(game.EntryPath),
            "manualFile" or "fileGame" => Path.GetDirectoryName(game.RootPath),
            _ => game.RootPath,
        };
        if (directory is null || !Directory.Exists(directory)) return null;
        var stem = game.Kind switch
        {
            "manualShortcut" => Path.GetFileNameWithoutExtension(game.EntryPath) + ".cover",
            "manualFile" or "fileGame" => Path.GetFileNameWithoutExtension(game.RootPath) + ".cover",
            _ => "cover",
        };
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var files = Directory.EnumerateFiles(directory).ToArray();
            var covers = files.Where(file => Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    && Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .OrderBy(file => !Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
            var existing = covers.FirstOrDefault();
            var asset = store.ListAssets(game.GameId).FirstOrDefault(item => item.IsCurrent);
            if (existing is not null && !replaceExisting)
            {
                // 已有用户封面优先；只有尚无库内封面的游戏才读取磁盘 cover。
                if (asset is not null || !Extensions.Contains(Path.GetExtension(existing).ToLowerInvariant())
                    || new FileInfo(existing).Length > MaxBytes
                    || (File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0) return null;
                // 解码校验在复制前完成；原图仍作为独立资产保存。
                _ = PrepareImage(File.ReadAllBytes(existing));
                Preserve(store, game.GameId, existing, null);
            }
            else if (asset is not null && File.Exists(asset.FilePath))
            {
                if (new FileInfo(asset.FilePath).Length <= MaxBytes)
                {
                    var prepared = PrepareImage(File.ReadAllBytes(asset.FilePath));
                    if (replaceExisting)
                        foreach (var previous in covers) Preserve(store, game.GameId, previous, asset.AssetId);
                    var target = covers.FirstOrDefault(file => Path.GetExtension(file).Equals(prepared.Extension, StringComparison.OrdinalIgnoreCase))
                        ?? Path.Combine(directory, stem + prepared.Extension);
                    var temporary = Path.Combine(directory, $".gamelibrary-cover-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllBytes(temporary, prepared.Bytes);
                        if (replaceExisting && File.Exists(target)) File.Replace(temporary, target, null);
                        else File.Move(temporary, target, overwrite: false);
                        // 旧格式只有在新封面完整落盘且历史保存成功后才移除。
                        if (replaceExisting)
                            foreach (var previous in covers.Where(file => !file.Equals(target, StringComparison.OrdinalIgnoreCase))) File.Delete(previous);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
            }
            return null;
        }
        catch (IOException ex) { return $"封面已保存在游戏库，但游戏目录封面同步失败：{ex.Message}"; }
        catch (UnauthorizedAccessException ex) { return $"无法写入或读取游戏目录封面：{ex.Message}"; }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { return $"封面无法安全解码，游戏目录原封面已保留：{ex.Message}"; }
    }

    private static void Preserve(SqliteLibraryStore store, string gameId, string source, string? currentId)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || new FileInfo(source).Length > MaxBytes)
            throw new IOException("原封面为链接或超过 5 MiB，无法安全保存历史");
        var bytes = File.ReadAllBytes(source);
        var matching = store.ListAssets(gameId).FirstOrDefault(item => File.Exists(item.FilePath)
            && new FileInfo(item.FilePath).Length == bytes.Length && File.ReadAllBytes(item.FilePath).AsSpan().SequenceEqual(bytes));
        if (matching is not null)
        {
            if (currentId is null) store.ChooseAsset(gameId, matching.AssetId);
            return;
        }
        var owned = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "assets", gameId);
        Directory.CreateDirectory(owned);
        var target = Path.Combine(owned, $"{Guid.NewGuid():N}{Path.GetExtension(source)}");
        File.WriteAllBytes(target, bytes);
        store.InTransaction(() =>
        {
            var preserved = store.ImportAsset(gameId, target, DateTime.UtcNow);
            if (currentId is not null) store.ChooseAsset(gameId, currentId);
            return preserved;
        });
    }
}
