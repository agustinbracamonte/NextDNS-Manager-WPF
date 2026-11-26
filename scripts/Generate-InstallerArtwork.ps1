$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $projectRoot 'NextDNSModernWPF'
$brandingDirectory = Join-Path $projectRoot 'installer\branding'
$iconPath = Join-Path $appDirectory 'escudo_dns.ico'
$iconPngPath = Join-Path $appDirectory 'brand.png'
$wizardPngPath = Join-Path $brandingDirectory 'wizard.png'

New-Item -ItemType Directory -Path $brandingDirectory -Force | Out-Null

function Draw-BrandMark([System.Drawing.Graphics]$graphics, [float]$x, [float]$y, [float]$size) {
    $state = $graphics.Save()
    $scale = $size / 512.0
    $graphics.TranslateTransform($x, $y)
    $graphics.ScaleTransform($scale, $scale)
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddBezier(
        [System.Drawing.Point]::new(256, 16), [System.Drawing.Point]::new(326, 48),
        [System.Drawing.Point]::new(405, 75), [System.Drawing.Point]::new(476, 96))
    $shield.AddBezier(
        [System.Drawing.Point]::new(476, 96), [System.Drawing.Point]::new(484, 238),
        [System.Drawing.Point]::new(424, 405), [System.Drawing.Point]::new(256, 496))
    $shield.AddBezier(
        [System.Drawing.Point]::new(256, 496), [System.Drawing.Point]::new(88, 405),
        [System.Drawing.Point]::new(28, 238), [System.Drawing.Point]::new(36, 96))
    $shield.AddBezier(
        [System.Drawing.Point]::new(36, 96), [System.Drawing.Point]::new(107, 75),
        [System.Drawing.Point]::new(186, 48), [System.Drawing.Point]::new(256, 16))
    $shield.CloseFigure()

    $blueBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.Rectangle]::new(36, 16, 440, 480),
        [System.Drawing.Color]::FromArgb(255, 76, 157, 255),
        [System.Drawing.Color]::FromArgb(255, 31, 99, 213), 90.0)
    $graphics.FillPath($blueBrush, $shield)

    $shield.Dispose()
    $blueBrush.Dispose()
    $graphics.Restore($state)
}

function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$path) {
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

$iconBitmap = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$iconGraphics.Clear([System.Drawing.Color]::Transparent)
Draw-BrandMark $iconGraphics 0 0 512
Save-Png $iconBitmap $iconPngPath

$iconSizes = @(256, 128, 64, 48, 32, 24, 16)
$iconImages = @()
foreach ($iconSize in $iconSizes) {
    $smallBitmap = New-Object System.Drawing.Bitmap($iconSize, $iconSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $smallGraphics = [System.Drawing.Graphics]::FromImage($smallBitmap)
    $smallGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $smallGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $smallGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $smallGraphics.DrawImage($iconBitmap, 0, 0, $iconSize, $iconSize)
    $imageStream = New-Object System.IO.MemoryStream
    $smallBitmap.Save($imageStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $iconImages += ,$imageStream.ToArray()
    $imageStream.Dispose()
    $smallGraphics.Dispose()
    $smallBitmap.Dispose()
}

$iconStream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$iconSizes.Count)
$imageOffset = 6 + (16 * $iconSizes.Count)
for ($index = 0; $index -lt $iconSizes.Count; $index++) {
    $iconSize = $iconSizes[$index]
    $dimension = if ($iconSize -eq 256) { [byte]0 } else { [byte]$iconSize }
    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$iconImages[$index].Length)
    $writer.Write([UInt32]$imageOffset)
    $imageOffset += $iconImages[$index].Length
}
foreach ($iconImage in $iconImages) { $writer.Write($iconImage) }
$writer.Dispose()
$iconStream.Dispose()
$iconBitmap.Dispose()
$iconGraphics.Dispose()

$wizardBitmap = New-Object System.Drawing.Bitmap(328, 628, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wizardGraphics = [System.Drawing.Graphics]::FromImage($wizardBitmap)
$wizardGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$wizardGraphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$wizardRect = [System.Drawing.Rectangle]::new(0, 0, 328, 628)
$wizardGraphics.Clear([System.Drawing.Color]::FromArgb(255, 18, 18, 18))
Draw-BrandMark $wizardGraphics 111 58 106

$titleFont = New-Object System.Drawing.Font('Segoe UI', 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subtitleFont = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 247, 250))
$mutedBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 164, 170, 180))
$accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 76, 157, 255))
$wizardGraphics.DrawString('NextDNS', $titleFont, $whiteBrush, 32, 230)
$wizardGraphics.DrawString('Manager', $titleFont, $accentBrush, 32, 263)
$dividerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 52, 59, 69), 1)
$wizardGraphics.DrawLine($dividerPen, 32, 315, 296, 315)
$wizardGraphics.DrawString('Protección DNS simple.', $subtitleFont, $mutedBrush, 32, 340)
Save-Png $wizardBitmap $wizardPngPath

$accentBrush.Dispose()
$dividerPen.Dispose()
$mutedBrush.Dispose()
$whiteBrush.Dispose()
$subtitleFont.Dispose()
$titleFont.Dispose()
$wizardGraphics.Dispose()
$wizardBitmap.Dispose()

Write-Output "Generated app icon: $iconPath"
Write-Output "Generated installer artwork: $wizardPngPath"
