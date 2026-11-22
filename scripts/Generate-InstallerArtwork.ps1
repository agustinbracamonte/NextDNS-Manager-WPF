$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $projectRoot 'NextDNSModernWPF'
$brandingDirectory = Join-Path $projectRoot 'installer\branding'
$iconPath = Join-Path $appDirectory 'escudo_dns.ico'
$iconPngPath = Join-Path $appDirectory 'brand.png'
$wizardPngPath = Join-Path $brandingDirectory 'wizard.png'

New-Item -ItemType Directory -Path $brandingDirectory -Force | Out-Null

function New-RoundedRectanglePath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-BrandMark([System.Drawing.Graphics]$graphics, [float]$x, [float]$y, [float]$size) {
    $state = $graphics.Save()
    $scale = $size / 512.0
    $graphics.TranslateTransform($x, $y)
    $graphics.ScaleTransform($scale, $scale)
    $outerPoints = [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(256, 45), [System.Drawing.Point]::new(414, 103),
        [System.Drawing.Point]::new(398, 279), [System.Drawing.Point]::new(357, 371),
        [System.Drawing.Point]::new(256, 455), [System.Drawing.Point]::new(155, 371),
        [System.Drawing.Point]::new(114, 279), [System.Drawing.Point]::new(98, 103)
    )
    $innerPoints = [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(256, 81), [System.Drawing.Point]::new(377, 125),
        [System.Drawing.Point]::new(364, 271), [System.Drawing.Point]::new(330, 344),
        [System.Drawing.Point]::new(256, 406), [System.Drawing.Point]::new(182, 344),
        [System.Drawing.Point]::new(148, 271), [System.Drawing.Point]::new(135, 125)
    )

    $outerBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.Rectangle]::new(96, 45, 320, 410),
        [System.Drawing.Color]::FromArgb(255, 47, 128, 237),
        [System.Drawing.Color]::FromArgb(255, 66, 211, 211), 45.0)
    $innerBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.Rectangle]::new(130, 80, 250, 330),
        [System.Drawing.Color]::FromArgb(255, 14, 34, 55),
        [System.Drawing.Color]::FromArgb(255, 22, 57, 78), 90.0)
    $graphics.FillPolygon($outerBrush, $outerPoints)
    $graphics.FillPolygon($innerBrush, $innerPoints)

    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 124, 238, 232), 12)
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($linePen, 174, 224, 338, 224)
    $nodeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 235, 252, 255))
    foreach ($nodeX in @(160, 244, 328)) {
        $graphics.FillEllipse($nodeBrush, $nodeX, 211, 26, 26)
    }

    $font = New-Object System.Drawing.Font('Segoe UI', 39, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 242, 249, 255))
    $graphics.DrawString('DNS', $font, $textBrush, [System.Drawing.RectangleF]::new(130, 249, 252, 70), $format)

    $textBrush.Dispose()
    $format.Dispose()
    $font.Dispose()
    $nodeBrush.Dispose()
    $linePen.Dispose()
    $innerBrush.Dispose()
    $outerBrush.Dispose()
    $graphics.Restore($state)
}

function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$path) {
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

$iconBitmap = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$iconGraphics.Clear([System.Drawing.Color]::Transparent)
$backgroundPath = New-RoundedRectanglePath 16 16 480 480 108
$backgroundBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    [System.Drawing.Rectangle]::new(16, 16, 480, 480),
    [System.Drawing.Color]::FromArgb(255, 8, 18, 33),
    [System.Drawing.Color]::FromArgb(255, 20, 50, 75), 45.0)
$iconGraphics.FillPath($backgroundBrush, $backgroundPath)
Draw-BrandMark $iconGraphics 0 0 512
Save-Png $iconBitmap $iconPngPath

$pngStream = New-Object System.IO.MemoryStream
$iconBitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$iconStream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Dispose()
$iconStream.Dispose()
$pngStream.Dispose()
$backgroundBrush.Dispose()
$backgroundPath.Dispose()
$iconGraphics.Dispose()
$iconBitmap.Dispose()

$wizardBitmap = New-Object System.Drawing.Bitmap(328, 628, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wizardGraphics = [System.Drawing.Graphics]::FromImage($wizardBitmap)
$wizardGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$wizardGraphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$wizardRect = [System.Drawing.Rectangle]::new(0, 0, 328, 628)
$wizardBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $wizardRect,
    [System.Drawing.Color]::FromArgb(255, 8, 18, 33),
    [System.Drawing.Color]::FromArgb(255, 18, 45, 70), 90.0)
$wizardGraphics.FillRectangle($wizardBrush, $wizardRect)
$glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(35, 66, 211, 211))
$wizardGraphics.FillEllipse($glowBrush, 178, -96, 272, 272)
$wizardGraphics.FillEllipse($glowBrush, -166, 438, 345, 345)
Draw-BrandMark $wizardGraphics 92 43 144

$titleFont = New-Object System.Drawing.Font('Segoe UI', 25, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subtitleFont = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$labelFont = New-Object System.Drawing.Font('Segoe UI Semibold', 11, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 242, 249, 255))
$cyanBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 101, 226, 224))
$mutedBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 165, 193, 213))
$wizardGraphics.DrawString('NEXTDNS', $titleFont, $whiteBrush, 30, 214)
$wizardGraphics.DrawString('MANAGER', $titleFont, $cyanBrush, 30, 249)
$dividerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(125, 101, 226, 224), 2)
$wizardGraphics.DrawLine($dividerPen, 30, 302, 298, 302)
$wizardGraphics.DrawString('Protección DNS', $subtitleFont, $whiteBrush, 30, 330)
$wizardGraphics.DrawString('con control sencillo.', $subtitleFont, $whiteBrush, 30, 356)
$wizardGraphics.DrawString('PRIVACIDAD  ·  CONTROL', $labelFont, $mutedBrush, 30, 422)

$cardPath = New-RoundedRectanglePath 28 528 272 58 14
$cardBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(75, 66, 211, 211))
$cardPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 101, 226, 224), 1.5)
$wizardGraphics.FillPath($cardBrush, $cardPath)
$wizardGraphics.DrawPath($cardPen, $cardPath)
$wizardGraphics.DrawString('NUCLEO CLI  ·  v1.50.2', $labelFont, $whiteBrush, 47, 547)
Save-Png $wizardBitmap $wizardPngPath

$dividerPen.Dispose()
$cardPen.Dispose()
$cardBrush.Dispose()
$cardPath.Dispose()
$mutedBrush.Dispose()
$cyanBrush.Dispose()
$whiteBrush.Dispose()
$labelFont.Dispose()
$subtitleFont.Dispose()
$titleFont.Dispose()
$glowBrush.Dispose()
$wizardBrush.Dispose()
$wizardGraphics.Dispose()
$wizardBitmap.Dispose()

Write-Output "Generated app icon: $iconPath"
Write-Output "Generated installer artwork: $wizardPngPath"
