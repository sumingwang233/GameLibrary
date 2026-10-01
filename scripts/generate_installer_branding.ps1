$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$workspace = Split-Path -Parent $PSScriptRoot
$sourceIcon = Join-Path $workspace 'src/GameLibrary.Desktop/App.ico'
$destination = Join-Path $workspace 'artifacts/installer'

# Native NSIS bitmaps; regenerate from the approved app icon after a logo change.
foreach ($asset in @(
    @{ Name = 'header.bmp'; Width = 150; Height = 57; Size = 48; X = 98; Y = 4 },
    @{ Name = 'wizard.bmp'; Width = 164; Height = 314; Size = 80; X = 42; Y = 28 }
)) {
    $icon = [System.Drawing.Icon]::new($sourceIcon, [System.Drawing.Size]::new($asset.Size, $asset.Size))
    $image = $icon.ToBitmap()
    $bitmap = [System.Drawing.Bitmap]::new($asset.Width, $asset.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image, $asset.X, $asset.Y, $asset.Size, $asset.Size)
        $bitmap.Save((Join-Path $destination $asset.Name), [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
        $image.Dispose()
        $icon.Dispose()
    }
}
Write-Output 'NSIS header and finish-page branding generated from App.ico.'
