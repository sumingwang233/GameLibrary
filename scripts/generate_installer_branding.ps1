$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$workspace = Split-Path -Parent $PSScriptRoot
$sourceImage = Join-Path $workspace 'src/GameLibrary.Tauri/app-icon.png'
$destination = Join-Path $workspace 'artifacts/installer'

# Keep 4x artwork for high-DPI dialogs; NSIS downsamples to the actual control size.
$scale = 4
$image = [System.Drawing.Image]::FromFile($sourceImage)
try {
    foreach ($asset in @(
        @{ Name = 'header.bmp'; Width = 150; Height = 57; Size = 48; X = 98; Y = 4 },
        @{ Name = 'wizard.bmp'; Width = 164; Height = 314; Size = 80; X = 42; Y = 28 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Width * $scale, $asset.Height * $scale, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($image, $asset.X * $scale, $asset.Y * $scale, $asset.Size * $scale, $asset.Size * $scale)
            $bitmap.Save((Join-Path $destination $asset.Name), [System.Drawing.Imaging.ImageFormat]::Bmp)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $image.Dispose() }
Write-Output 'High-DPI NSIS branding generated from the approved PNG.'
