$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assetDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RobloxPriceTracker.Gui\Assets'
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null

function Solid([string]$hex) {
    return [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($hex))
}

function Render-TrackerIcon([int]$size) {
    # Build the small resources from simplified geometry, not downscaled 256px artwork.
    # Render at a higher internal resolution before high-quality downsampling.
    $small = $size -le 24
    $medium = $size -ge 32 -and $size -le 64
    $supersample = if ($size -le 48) { 4 } elseif ($size -le 128) { 2 } else { 1 }
    $renderSize = $size * $supersample
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    try {
        $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($renderSize / 512.0, $renderSize / 512.0))
        if ($small) {
            # Flat contrast survives 16px and Windows' notification-area resampling.
            $dc.DrawRoundedRectangle((Solid '#222936'), $null,
                [System.Windows.Rect]::new(10, 10, 492, 492), 116, 116)
        }
        else {
            $bg = [System.Windows.Media.LinearGradientBrush]::new(
                [System.Windows.Media.ColorConverter]::ConvertFromString('#343C49'),
                [System.Windows.Media.ColorConverter]::ConvertFromString('#10151E'), 130.0)
            $edge = [System.Windows.Media.Pen]::new((Solid '#424B5A'), $(if ($medium) { 5 } else { 3 }))
            $dc.DrawRoundedRectangle($bg, $edge,
                [System.Windows.Rect]::new(14, 14, 484, 484), 108, 108)
        }

        $dc.PushTransform([System.Windows.Media.RotateTransform]::new(17, 245, 208))
        $white = if ($small) { Solid '#FFFFFF' } else {
            [System.Windows.Media.LinearGradientBrush]::new(
                [System.Windows.Media.ColorConverter]::ConvertFromString('#FFFFFF'),
                [System.Windows.Media.ColorConverter]::ConvertFromString('#E0E9F7'), 125.0)
        }
        if ($small) {
            $dc.DrawRoundedRectangle($white, $null, [System.Windows.Rect]::new(117, 82, 266, 266), 9, 9)
            $dc.DrawRoundedRectangle((Solid '#222936'), $null,
                [System.Windows.Rect]::new(214, 178, 74, 74), 1, 1)
        }
        elseif ($medium) {
            $dc.DrawRoundedRectangle($white, $null, [System.Windows.Rect]::new(129, 86, 244, 244), 9, 9)
            $dc.DrawRoundedRectangle((Solid '#1A2230'), $null,
                [System.Windows.Rect]::new(220, 178, 68, 68), 2, 2)
        }
        else {
            $dc.DrawRoundedRectangle($white, $null, [System.Windows.Rect]::new(136, 93, 231, 231), 11, 11)
            $dc.DrawRoundedRectangle((Solid '#161A24'), $null,
                [System.Windows.Rect]::new(222, 180, 61, 61), 2, 2)
        }
        $dc.Pop()

        if ($small) {
            # Three-segment arrow with no outer halo; visible even at 16/20px.
            $path = [System.Windows.Media.Geometry]::Parse('M149,391 L238,324 L287,357 L376,268')
            $pen = [System.Windows.Media.Pen]::new((Solid '#43B2FF'), 49)
            $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
            $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $dc.DrawGeometry($null, $pen, $path)
            $dc.DrawGeometry((Solid '#43B2FF'), $null,
                [System.Windows.Media.Geometry]::Parse('M345,239 L426,223 Q438,220 436,235 L424,312 Q422,324 412,316 L337,259 Q328,250 345,239 Z'))
        }
        else {
            $path = [System.Windows.Media.Geometry]::Parse('M154,389 L234,318 L284,361 L375,269')
            if (-not $medium) {
                $outline = [System.Windows.Media.Pen]::new((Solid '#162A42'), 57)
                $outline.LineJoin = [System.Windows.Media.PenLineJoin]::Round
                $outline.StartLineCap = [System.Windows.Media.PenLineCap]::Round
                $outline.EndLineCap = [System.Windows.Media.PenLineCap]::Round
                $dc.DrawGeometry($null, $outline, $path)
            }
            $blue = [System.Windows.Media.LinearGradientBrush]::new(
                [System.Windows.Media.ColorConverter]::ConvertFromString('#75DFFF'),
                [System.Windows.Media.ColorConverter]::ConvertFromString('#1769DB'), 135.0)
            $pen = [System.Windows.Media.Pen]::new($blue, $(if ($medium) { 42 } else { 36 }))
            $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
            $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $dc.DrawGeometry($null, $pen, $path)
            if (-not $medium) {
                $dc.DrawGeometry((Solid '#172E4E'), $null,
                    [System.Windows.Media.Geometry]::Parse('M346,248 L426,228 Q438,225 437,239 L429,316 Q427,327 417,320 L345,264 Q335,256 346,248 Z'))
            }
            $dc.DrawGeometry($blue, $null,
                [System.Windows.Media.Geometry]::Parse('M353,243 L418,225 Q429,222 428,233 L420,298 Q419,311 409,303 L346,259 Q336,251 353,243 Z'))
        }
        $dc.Pop()
    }
    finally { $dc.Close() }

    $hiRes = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($renderSize, $renderSize, 96, 96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $hiRes.Render($visual)
    $bitmap = $hiRes
    if ($supersample -gt 1) {
        $down = [System.Windows.Media.DrawingVisual]::new()
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
            $down, [System.Windows.Media.BitmapScalingMode]::HighQuality)
        $drawing = $down.RenderOpen()
        try { $drawing.DrawImage($hiRes, [System.Windows.Rect]::new(0, 0, $size, $size)) }
        finally { $drawing.Close() }
        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96,
            [System.Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($down)
    }
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,([byte[]]$stream.ToArray())
    }
    finally { $stream.Dispose() }
}

function Write-IconBundle([string]$path) {
    # Add 20, 40 and 96px DPI steps; retain Windows' canonical 16–256px entries.
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) { $images.Add([byte[]](Render-TrackerIcon $size)) }
    $output = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($output)
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
Write-Host 'Generated 10 independently optimized icon frames: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px.'
