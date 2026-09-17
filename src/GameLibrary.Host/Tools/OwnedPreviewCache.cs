using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.Host.Tools;

/// <summary>当前库的可再生封面预览缓存；自定义位置永不接管用户目录本身。</summary>
internal static class OwnedPreviewCache
{
    private const string ContainerName = "GameLibraryCache";
    private const string MarkerName = ".gamelibrary-owner";
    private const int MaxDimension = 720;
    private const int MaxPreviewBytes = 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal sealed record Preview(byte[] Bytes, string MimeType);

    public static bool TryValidateParent(string input, string dataDirectory, out string canonical, out string error)
    {
        canonical = "";
        error = "";
        var resolved = DataDirectory.Resolve(input);
        if (!resolved.IsValid)
        {
            error = $"缓存位置必须是本地绝对路径（{resolved.Error}）";
            return false;
        }

        canonical = resolved.CanonicalPath!;
        try
        {
            EnsureDirectoryAndAncestorsAreNotLinks(canonical);
            var container = Path.Combine(canonical, ContainerName);
            if (File.Exists(container))
            {
                throw new InvalidOperationException($"缓存专属目录名称已被文件占用：{container}");
            }

            if (Directory.Exists(container))
            {
                EnsureDirectoryAndAncestorsAreNotLinks(container);
            }

            var root = GetRoot(dataDirectory, canonical);
            if (File.Exists(root))
            {
                throw new InvalidOperationException($"缓存专属子目录名称已被文件占用：{root}");
            }

            if (Directory.Exists(root))
            {
                EnsureDirectoryAndAncestorsAreNotLinks(root);
                VerifyOwner(root, dataDirectory);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string GetRoot(string dataDirectory, string? cacheParentDirectory) =>
        cacheParentDirectory is null
            ? Path.Combine(dataDirectory, "cache")
            : Path.Combine(cacheParentDirectory, ContainerName, LibraryKey(dataDirectory));

    public static string? GetValidatedContentDirectoryForCleanup(
        string dataDirectory,
        string? cacheParentDirectory)
    {
        var root = GetRoot(dataDirectory, cacheParentDirectory);
        if (File.Exists(root))
        {
            throw new InvalidOperationException($"缓存专属目录名称已被文件占用：{root}");
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        EnsureDirectoryAndAncestorsAreNotLinks(root);
        if (cacheParentDirectory is not null)
        {
            VerifyOwner(root, dataDirectory);
            var previews = Path.Combine(root, "previews");
            if (!Directory.Exists(previews))
            {
                return null;
            }

            EnsureDirectoryAndAncestorsAreNotLinks(previews);
            return previews;
        }

        return root;
    }

    public static Preview GetOrCreate(string dataDirectory, string? cacheParentDirectory,
        string assetId, string sourcePath, byte[] sourceBytes, string originalMimeType)
    {
        try
        {
            var previewDirectory = EnsurePreviewDirectory(dataDirectory, cacheParentDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{assetId}|{Convert.ToHexString(SHA256.HashData(sourceBytes))}"))).ToLowerInvariant();
            var previewPath = Path.Combine(previewDirectory, key + ".png");
            if (File.Exists(previewPath))
            {
                if ((File.GetAttributes(previewPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return new Preview(sourceBytes, originalMimeType);
                }

                var cachedBytes = File.ReadAllBytes(previewPath);
                if (cachedBytes.Length <= MaxPreviewBytes && cachedBytes.AsSpan().StartsWith(PngSignature))
                {
                    return new Preview(cachedBytes, "image/png");
                }
            }

            using var input = new MemoryStream(sourceBytes, writable: false);
            using var source = Image.FromStream(input);
            var scale = Math.Min(1.0, (double)MaxDimension / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            using var output = new MemoryStream();
            target.Save(output, ImageFormat.Png);
            var bytes = output.ToArray();
            if (bytes.Length > MaxPreviewBytes)
            {
                return new Preview(sourceBytes, originalMimeType);
            }

            var temporaryPath = Path.Combine(previewDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, previewPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new Preview(bytes, "image/png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            // 缓存是可选加速层；损坏/离线/无权限不影响原图展示。
            return new Preview(sourceBytes, originalMimeType);
        }
    }

    private static string EnsurePreviewDirectory(string dataDirectory, string? cacheParentDirectory)
    {
        var root = GetRoot(dataDirectory, cacheParentDirectory);
        if (cacheParentDirectory is not null)
        {
            EnsureDirectoryAndAncestorsAreNotLinks(cacheParentDirectory);
            var container = Path.GetDirectoryName(root)!;
            if (!Directory.Exists(container))
            {
                Directory.CreateDirectory(container);
            }

            EnsureDirectoryAndAncestorsAreNotLinks(container);
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }

            EnsureDirectoryAndAncestorsAreNotLinks(root);
            var marker = Path.Combine(root, MarkerName);
            if (!File.Exists(marker))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any())
                {
                    throw new InvalidOperationException("自定义缓存子目录已有未知文件，拒绝接管");
                }

                File.WriteAllText(marker, MarkerContents(dataDirectory), Encoding.UTF8);
            }

            VerifyOwner(root, dataDirectory);
        }
        else
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }

            EnsureDirectoryAndAncestorsAreNotLinks(root);
        }

        var previews = Path.Combine(root, "previews");
        if (!Directory.Exists(previews))
        {
            Directory.CreateDirectory(previews);
        }

        EnsureDirectoryAndAncestorsAreNotLinks(previews);
        return previews;
    }

    private static void VerifyOwner(string root, string dataDirectory)
    {
        var marker = Path.Combine(root, MarkerName);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(marker, Encoding.UTF8) != MarkerContents(dataDirectory))
        {
            throw new InvalidOperationException("自定义缓存子目录缺少本库所有权标记，拒绝接管或清理");
        }
    }

    private static string MarkerContents(string dataDirectory) => $"GameLibraryCache|v1|{LibraryKey(dataDirectory)}";

    private static string LibraryKey(string dataDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16].ToLowerInvariant();

    private static void EnsureDirectoryAndAncestorsAreNotLinks(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"目录不存在：{directory}");
        }

        var current = Path.GetFullPath(directory);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"缓存路径包含链接或 junction：{current}");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }
}
