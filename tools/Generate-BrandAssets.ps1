$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assetDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RobloxPriceTracker.Gui\Assets'
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null

function Render-TrackerIcon([int]$size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    try {
        $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 512.0, $size / 512.0))
        $bg = [System.Windows.Media.LinearGradientBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString('#2E333D'),
            [System.Windows.Media.ColorConverter]::ConvertFromString('#10151E'),
            130.0)
        $border = [System.Windows.Media.Pen]::new(
            [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#3D4653')), 3)
        $dc.DrawRoundedRectangle($bg, $border, [System.Windows.Rect]::new(14, 14, 484, 484), 108, 108)

        $dc.PushTransform([System.Windows.Media.RotateTransform]::new(17, 245, 208))
        $white = [System.Windows.Media.LinearGradientBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString('#FFFFFF'),
            [System.Windows.Media.ColorConverter]::ConvertFromString('#D4E1F3'), 125.0)
        $dc.DrawRoundedRectangle($white, $null, [System.Windows.Rect]::new(136, 93, 231, 231), 11, 11)
        $cutout = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#161A24'))
        $dc.DrawRoundedRectangle($cutout, $null, [System.Windows.Rect]::new(222, 180, 61, 61), 2, 2)
        $dc.Pop()

        $chart = [System.Windows.Media.Geometry]::Parse('M154,389 L234,318 284,361 375,269')
        $outline = [System.Windows.Media.Pen]::new(
            [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#162A42')), 57)
        $outline.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $outline.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $outline.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $dc.DrawGeometry($null, $outline, $chart)
        $blue = [System.Windows.Media.LinearGradientBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString('#75DFFF'),
            [System.Windows.Media.ColorConverter]::ConvertFromString('#1769DB'), 135.0)
        $line = [System.Windows.Media.Pen]::new($blue, 36)
        $line.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $line.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $line.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $dc.DrawGeometry($null, $line, $chart)
        $arrowOutline = [System.Windows.Media.Geometry]::Parse('M346,248 L426,228 Q438,225 437,239 L429,316 Q427,327 417,320 L345,264 Q335,256 346,248 Z')
        $arrowInner = [System.Windows.Media.Geometry]::Parse('M353,243 L418,225 Q429,222 428,233 L420,298 Q419,311 409,303 L346,259 Q336,251 353,243 Z')
        $dc.DrawGeometry([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#172E4E')), $null, $arrowOutline)
        $dc.DrawGeometry($blue, $null, $arrowInner)
        $dc.Pop()
    }
    finally { $dc.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return $stream.ToArray()
    }
    finally { $stream.Dispose() }
}

function Write-IconBundle([string]$path) {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) { $images.Add([byte[]](Render-TrackerIcon $size)) }
    $out = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($out)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$images[$i].Length)
            $writer.Write([UInt32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    }
    finally { $writer.Dispose() }
}

[System.IO.File]::WriteAllBytes((Join-Path $assetDir 'mouse_logo.png'), [byte[]](Render-TrackerIcon 256))
[System.IO.File]::WriteAllBytes((Join-Path $assetDir 'mouse_menu.png'), [byte[]](Render-TrackerIcon 96))
[System.IO.File]::WriteAllBytes((Join-Path $assetDir 'mouse_window.png'), [byte[]](Render-TrackerIcon 128))
Write-IconBundle (Join-Path $assetDir 'mouse_app.ico')
Write-Host 'Roblox Price Tracker multi-resolution icon and UI branding generated.'
