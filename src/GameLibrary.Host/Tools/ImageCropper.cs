namespace GameLibrary.Host.Tools;

/// <summary>
/// 封面裁切（T14，assets.crop）：System.Drawing 真实像素裁切，输出 PNG 到应用自有资产目录；
/// 不触碰游戏目录文件。仅 Windows（Host TFM net10.0-windows）。
/// </summary>
public static class ImageCropper
{
    public static string Crop(string sourcePath, string destDirectory, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "裁切尺寸必须为正数");
        }

        Directory.CreateDirectory(destDirectory);
        var targetPath = Path.Combine(destDirectory, $"crop-{Guid.NewGuid():N}.png");

        using var source = System.Drawing.Image.FromFile(sourcePath);
        if (x < 0 || y < 0 || x + width > source.Width || y + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"裁切区域越界：源图 {source.Width}x{source.Height}，请求 ({x},{y},{width}x{height})");
        }

        using var target = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                new System.Drawing.Rectangle(0, 0, width, height),
                new System.Drawing.Rectangle(x, y, width, height),
                System.Drawing.GraphicsUnit.Pixel);
        }

        target.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
        return targetPath;
    }
}
