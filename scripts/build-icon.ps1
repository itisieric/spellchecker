[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRoot 'src\LLMAutocorrect\Assets'
$taskSource = Join-Path $taskAssets 'Spellchecker.svg'
$taskSvg = [xml](Get-Content -LiteralPath $taskSource -Raw)
$taskCulture = [Globalization.CultureInfo]::InvariantCulture
$taskSizes = @(16,20,24,32,40,48,64,96,128,256)

function Get-Number([string]$Value) { [float]::Parse($Value, $taskCulture) }

function New-IconFrame([int]$Size) {
    # Render the deliberately small SVG vocabulary used by this icon directly.
    # No font dependencies, extra renderer, downloads, or external image services.
    $taskRenderSize = [math]::Max(256, $Size)
    $taskCanvas = [Drawing.Bitmap]::new($taskRenderSize, $taskRenderSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskCanvas)
    try {
        $taskGraphics.Clear([Drawing.Color]::Transparent)
        $taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskGraphics.ScaleTransform($taskRenderSize / 256.0, $taskRenderSize / 256.0)
        $taskTile = $taskSvg.svg.rect
        $taskX = Get-Number $taskTile.x; $taskY = Get-Number $taskTile.y
        $taskWidth = Get-Number $taskTile.width; $taskHeight = Get-Number $taskTile.height
        $taskDiameter = 2 * (Get-Number $taskTile.rx)
        $taskTilePath = [Drawing.Drawing2D.GraphicsPath]::new()
        $taskTilePath.AddArc($taskX, $taskY, $taskDiameter, $taskDiameter, 180, 90)
        $taskTilePath.AddArc($taskX+$taskWidth-$taskDiameter, $taskY, $taskDiameter, $taskDiameter, 270, 90)
        $taskTilePath.AddArc($taskX+$taskWidth-$taskDiameter, $taskY+$taskHeight-$taskDiameter, $taskDiameter, $taskDiameter, 0, 90)
        $taskTilePath.AddArc($taskX, $taskY+$taskHeight-$taskDiameter, $taskDiameter, $taskDiameter, 90, 90)
        $taskTilePath.CloseFigure()
        $taskStops = @($taskSvg.svg.defs.linearGradient.stop)
        $taskBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.RectangleF]::new($taskX, $taskY, $taskWidth, $taskHeight),
            [Drawing.ColorTranslator]::FromHtml($taskStops[0].'stop-color'),
            [Drawing.ColorTranslator]::FromHtml($taskStops[1].'stop-color'), 45.0)
        $taskBorder = [Drawing.Pen]::new([Drawing.Color]::FromArgb(
            [int][math]::Round(255 * (Get-Number $taskTile.'stroke-opacity')),
            [Drawing.ColorTranslator]::FromHtml($taskTile.stroke)), (Get-Number $taskTile.'stroke-width'))
        try { $taskGraphics.FillPath($taskBrush, $taskTilePath); $taskGraphics.DrawPath($taskBorder, $taskTilePath) }
        finally { $taskBrush.Dispose(); $taskBorder.Dispose(); $taskTilePath.Dispose() }

        foreach ($taskShape in $taskSvg.svg.path) {
            $taskPath = [Drawing.Drawing2D.GraphicsPath]::new()
            $taskPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($taskShape.stroke), (Get-Number $taskShape.'stroke-width'))
            $taskPen.StartCap = $taskPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            $taskPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
            try {
                $taskPrevious = $null
                foreach ($taskCommand in [regex]::Matches($taskShape.d, '([ML])\s+([\d.]+)\s+([\d.]+)')) {
                    $taskPoint = [Drawing.PointF]::new((Get-Number $taskCommand.Groups[2].Value), (Get-Number $taskCommand.Groups[3].Value))
                    if ($taskCommand.Groups[1].Value -eq 'M') { $taskPath.StartFigure() }
                    else { $taskPath.AddLine($taskPrevious, $taskPoint) }
                    $taskPrevious = $taskPoint
                }
                $taskGraphics.DrawPath($taskPen, $taskPath)
            }
            finally { $taskPath.Dispose(); $taskPen.Dispose() }
        }
    }
    finally { $taskGraphics.Dispose() }

    if ($Size -eq $taskRenderSize) { return ,$taskCanvas }
    $taskFrame = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskResize = [Drawing.Graphics]::FromImage($taskFrame)
    $taskAttributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $taskResize.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $taskResize.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskResize.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskAttributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $taskResize.DrawImage($taskCanvas, [Drawing.Rectangle]::new(0,0,$Size,$Size), 0,0,$taskRenderSize,$taskRenderSize,[Drawing.GraphicsUnit]::Pixel,$taskAttributes)
    }
    finally { $taskResize.Dispose(); $taskAttributes.Dispose(); $taskCanvas.Dispose() }
    return ,$taskFrame
}

$taskFrames = [Collections.Generic.List[byte[]]]::new()
foreach ($taskSize in $taskSizes) {
    $taskFrame = New-IconFrame $taskSize
    $taskStream = [IO.MemoryStream]::new()
    try { $taskFrame.Save($taskStream, [Drawing.Imaging.ImageFormat]::Png); $taskFrames.Add($taskStream.ToArray()) }
    finally { $taskFrame.Dispose(); $taskStream.Dispose() }
}
$taskOutput = [IO.File]::Create((Join-Path $taskAssets 'Spellchecker.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskOutput)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
    $taskOffset = 6 + 16 * $taskSizes.Count
    for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
        $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrames[$taskIndex].Length
    }
    foreach ($taskBytes in $taskFrames) { $taskWriter.Write([byte[]]$taskBytes) }
}
finally { $taskWriter.Dispose() }

$taskMaster = New-IconFrame 512
try { $taskMaster.Save((Join-Path $taskAssets 'Spellchecker.png'), [Drawing.Imaging.ImageFormat]::Png) }
finally { $taskMaster.Dispose() }

# A design-review sheet with light/dark backgrounds and actual-size icons.
$taskSheet = [Drawing.Bitmap]::new(960, 430)
$taskPreview = [Drawing.Graphics]::FromImage($taskSheet)
$taskFont = [Drawing.Font]::new('Segoe UI', 11)
$taskLight = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(244,246,250))
$taskDark = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(26,29,38))
try {
    $taskPreview.FillRectangle($taskLight,0,0,480,430)
    $taskPreview.FillRectangle($taskDark,480,0,480,430)
    foreach ($taskPanel in @(0,480)) {
        $taskInk = if ($taskPanel -eq 0) { [Drawing.Brushes]::DarkSlateGray } else { [Drawing.Brushes]::WhiteSmoke }
        $taskPreview.DrawString('LLM Autocorrect', $taskFont, $taskInk, $taskPanel+28, 20)
        $taskLarge = New-IconFrame 256
        try { $taskPreview.DrawImageUnscaled($taskLarge, $taskPanel+112, 56) }
        finally { $taskLarge.Dispose() }
        $taskLeft = $taskPanel+28
        foreach ($taskSize in @(16,20,24,32,48,64)) {
            $taskSmall = New-IconFrame $taskSize
            try { $taskPreview.DrawImageUnscaled($taskSmall, $taskLeft, 335) }
            finally { $taskSmall.Dispose() }
            $taskPreview.DrawString([string]$taskSize, $taskFont, $taskInk, $taskLeft, 405)
            $taskLeft += 72
        }
    }
    $taskPreviewPath = Join-Path $taskRoot 'artifacts\icon-preview-20261027.png'
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskPreviewPath) -Force | Out-Null
    $taskSheet.Save($taskPreviewPath, [Drawing.Imaging.ImageFormat]::Png)
    Write-Output $taskPreviewPath
}
finally { $taskPreview.Dispose(); $taskSheet.Dispose(); $taskFont.Dispose(); $taskLight.Dispose(); $taskDark.Dispose() }
Write-Output "Generated Spellchecker.ico with $($taskSizes.Count) 32-bit alpha sizes: $($taskSizes -join ', ')."
