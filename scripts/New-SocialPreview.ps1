$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$repoRoot = Split-Path -Parent $PSScriptRoot
$screenshotPath = Join-Path $repoRoot 'docs\screenshots\manager-dark.png'
$outputPath = Join-Path $repoRoot 'docs\assets\social-preview.png'
if (-not (Test-Path -LiteralPath $screenshotPath)) {
    throw "No se encontró la captura de demostración: $screenshotPath"
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null

function New-SolidBrush([string]$hex) {
    $brush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $brush.Freeze()
    return $brush
}

function Draw-Label(
    [System.Windows.Media.DrawingContext]$context,
    [string]$text,
    [double]$x,
    [double]$y,
    [double]$size,
    [string]$color,
    [bool]$bold = $false
) {
    $weight = if ($bold) { [System.Windows.FontWeights]::SemiBold } else { [System.Windows.FontWeights]::Normal }
    $typeface = [System.Windows.Media.Typeface]::new('Segoe UI', [System.Windows.FontStyles]::Normal, $weight, [System.Windows.FontStretches]::Normal)
    $formatted = [System.Windows.Media.FormattedText]::new(
        $text,
        [System.Globalization.CultureInfo]::GetCultureInfo('es-AR'),
        [System.Windows.FlowDirection]::LeftToRight,
        $typeface,
        $size,
        (New-SolidBrush $color),
        1.0
    )
    $context.DrawText($formatted, [System.Windows.Point]::new($x, $y))
}

$width = 1280
$height = 640
$visual = [System.Windows.Media.DrawingVisual]::new()
$context = $visual.RenderOpen()

$context.DrawRectangle((New-SolidBrush '#111820'), $null, [System.Windows.Rect]::new(0, 0, $width, $height))
$accent = [System.Windows.Media.LinearGradientBrush]::new(
    [System.Windows.Media.ColorConverter]::ConvertFromString('#18395A'),
    [System.Windows.Media.ColorConverter]::ConvertFromString('#111820'),
    [System.Windows.Point]::new(0, 0),
    [System.Windows.Point]::new(1, 1)
)
$accent.Freeze()
$context.DrawRectangle($accent, $null, [System.Windows.Rect]::new(0, 0, 520, $height))

$shield = [System.Windows.Media.Geometry]::Parse('M 0,18 C 15,15 29,8 40,0 C 51,8 65,15 80,18 L 80,50 C 80,72 66,91 40,106 C 14,91 0,72 0,50 Z')
$context.PushTransform([System.Windows.Media.TranslateTransform]::new(62, 66))
$context.DrawGeometry((New-SolidBrush '#1684FF'), $null, $shield)
$context.Pop()

Draw-Label $context 'NextDNS' 160 75 47 '#F4F7FB' $true
Draw-Label $context 'MANAGER PARA WINDOWS' 163 135 18 '#54A6FF' $true
Draw-Label $context 'Controlá tu protección DNS' 62 234 28 '#F4F7FB' $true
Draw-Label $context 'y tus perfiles desde una app nativa.' 62 274 25 '#D0D9E4'

$pill = [System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new(62, 350, 405, 56), 14, 14)
$context.DrawGeometry((New-SolidBrush '#1B2A39'), (New-Object System.Windows.Media.Pen((New-SolidBrush '#35506A'), 1)), $pill)
Draw-Label $context 'PERFILES  ·  LISTAS  ·  ACTIVIDAD' 82 367 17 '#D8E7F6' $true
Draw-Label $context 'Modo claro, oscuro y automático' 64 436 18 '#AFC1D2'
Draw-Label $context 'Proyecto independiente · v2.2.0' 64 564 16 '#90A5BA'

$screenshot = [System.Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new($screenshotPath, [UriKind]::Absolute))
$screenRect = [System.Windows.Rect]::new(538, 57, 690, 484)
$frame = [System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new(532, 51, 702, 496), 22, 22)
$context.DrawGeometry((New-SolidBrush '#0A0F15'), (New-Object System.Windows.Media.Pen((New-SolidBrush '#47627E'), 1)), $frame)
$context.PushClip([System.Windows.Media.RectangleGeometry]::new($screenRect, 14, 14))
$context.DrawImage($screenshot, $screenRect)
$context.Pop()
$context.Close()

$bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [System.IO.File]::Create($outputPath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }

$image = [System.Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new($outputPath, [UriKind]::Absolute))
if ($image.PixelWidth -ne $width -or $image.PixelHeight -ne $height) {
    throw "La vista previa debe medir ${width}x${height}; se generó $($image.PixelWidth)x$($image.PixelHeight)."
}
Write-Output "Vista previa generada: $outputPath ($width x $height)."
