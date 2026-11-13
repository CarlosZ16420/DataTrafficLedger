param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'assets\app.ico' }
Add-Type -AssemblyName System.Drawing

# Rasterize the existing logo's rounded rectangle and straight, rounded-cap path.
# Keep the SVG as the source of its geometry and colors; no image tools are needed.
[xml]$document = [IO.File]::ReadAllText((Join-Path $projectRoot 'assets\readme\logo.svg'))
$logo = $document.DocumentElement
$rectangle = $logo.rect
$line = $logo.path
$culture = [Globalization.CultureInfo]::InvariantCulture
$canvasSize = [single]::Parse($logo.width, $culture)
$radius = [single]::Parse($rectangle.rx, $culture)
if ($logo.width -ne $logo.height -or $line.d -notmatch '^M[\d\s.,-]+$') { throw 'Unsupported logo geometry.' }
$coordinates = [regex]::Matches($line.d, '-?\d+(?:\.\d+)?')
if ($coordinates.Count -lt 4 -or $coordinates.Count % 2) { throw 'Invalid logo path.' }
$points = for ($index = 0; $index -lt $coordinates.Count; $index += 2) {
    [Drawing.PointF]::new([single]::Parse($coordinates[$index].Value, $culture), [single]::Parse($coordinates[$index + 1].Value, $culture))
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $large = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($large)
    $scaledGraphics = [Drawing.Graphics]::FromImage($bitmap)
    $background = [Drawing.Drawing2D.GraphicsPath]::new()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($rectangle.fill))
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($line.stroke), [single]::Parse($line.'stroke-width', $culture))
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size * 4 / $canvasSize, $size * 4 / $canvasSize)
        $diameter = 2 * $radius
        $edge = $canvasSize - $diameter
        $background.AddArc(0, 0, $diameter, $diameter, 180, 90)
        $background.AddArc($edge, 0, $diameter, $diameter, 270, 90)
        $background.AddArc($edge, $edge, $diameter, $diameter, 0, 90)
        $background.AddArc(0, $edge, $diameter, $diameter, 90, 90)
        $background.CloseFigure()
        $graphics.FillPath($brush, $background)
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLines($pen, [Drawing.PointF[]]$points)
        $scaledGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaledGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaledGraphics.DrawImage($large, [Drawing.Rectangle]::new(0, 0, $size, $size))
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
    } finally {
        $stream.Dispose(); $pen.Dispose(); $brush.Dispose(); $background.Dispose()
        $scaledGraphics.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $large.Dispose()
    }
}
$output = [IO.File]::Create([IO.Path]::GetFullPath($OutputPath))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
Write-Output "Created application icon: $OutputPath"
