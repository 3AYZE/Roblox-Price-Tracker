$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'logs'
$logPath = Join-Path $logDir 'build.log'
$solution = Join-Path $repoRoot 'RobloxPriceTracker.sln'
$guiProject = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\RobloxPriceTracker.Gui.csproj'
$testProject = Join-Path $repoRoot 'tests\RobloxPriceTracker.SelfTest\RobloxPriceTracker.SelfTest.csproj'
$iconPath = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\Assets\mouse_app.ico'
$dist = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $dist 'publish-temp'
$finalExe = Join-Path $dist 'RobloxPriceTracker.exe'

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Set-Content -Path $logPath -Value "Roblox Price Tracker Windows build - $(Get-Date -Format o)"

function Write-Step([string]$Text) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Text"
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

function Invoke-DotNet([string]$Description, [string[]]$ArgsList) {
    Write-Step $Description
    & dotnet @ArgsList 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($ArgsList -join ' ') failed with exit code $LASTEXITCODE." }
}

function Assert-LightweightGui {
    Write-Step 'Checking GUI dependency invariants...'
    $projectText = Get-Content -LiteralPath $guiProject -Raw
    if ($projectText -match '<UseWindowsForms>\s*true\s*</UseWindowsForms>') {
        throw 'Size invariant failed: GUI still enables Windows Forms.'
    }

    $formsRefs = @(Get-ChildItem -Path (Split-Path $guiProject -Parent) -Filter '*.cs' -File |
        Select-String -Pattern 'System\.Windows\.Forms')
    if ($formsRefs.Count -ne 0) {
        throw "Size invariant failed: found $($formsRefs.Count) System.Windows.Forms source reference(s)."
    }
}

function Restore-AppIcon {
    Write-Step 'Generating original RPT Markets application icon...'

    Add-Type -AssemblyName System.Drawing
    if (-not ('RPTNativeIconMethods' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RPTNativeIconMethods {
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@
    }

    $bitmap = $null
    $graphics = $null
    $backgroundBrush = $null
    $borderPen = $null
    $gridPen = $null
    $cyanPen = $null
    $greenPen = $null
    $barBrush = $null
    $icon = $null
    $stream = $null
    $hIcon = [IntPtr]::Zero

    try {
        $bitmap = New-Object System.Drawing.Bitmap 64, 64, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        # RPT Markets brand mark: dark market-terminal tile with a compact price chart.
        $backgroundBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 11, 18, 32))
        $borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 47, 65, 87)), 2
        $graphics.FillEllipse($backgroundBrush, 3, 3, 58, 58)
        $graphics.DrawEllipse($borderPen, 4, 4, 56, 56)

        $gridPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 79, 98, 121)), 1
        $graphics.DrawLine($gridPen, 13, 46, 52, 46)
        $graphics.DrawLine($gridPen, 13, 35, 52, 35)

        $barBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 39, 196, 214))
        $graphics.FillRectangle($barBrush, 16, 36, 5, 10)
        $graphics.FillRectangle($barBrush, 27, 29, 5, 12)
        $graphics.FillRectangle($barBrush, 38, 31, 5, 8)

        $cyanPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 93, 224, 230)), 3
        $cyanPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cyanPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $points = [System.Drawing.Point[]]@(
            (New-Object System.Drawing.Point 14, 43),
            (New-Object System.Drawing.Point 26, 34),
            (New-Object System.Drawing.Point 36, 37),
            (New-Object System.Drawing.Point 50, 21)
        )
        $graphics.DrawLines($cyanPen, $points)

        $greenPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 45, 216, 129)), 3
        $greenPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $greenPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($greenPen, 50, 21, 49, 29)
        $graphics.DrawLine($greenPen, 50, 21, 42, 22)

        $hIcon = $bitmap.GetHicon()
        if ($hIcon -eq [IntPtr]::Zero) { throw 'Windows failed to create an HICON from the RPT artwork.' }
        $icon = [System.Drawing.Icon]::FromHandle($hIcon)
        $stream = [IO.File]::Create($iconPath)
        $icon.Save($stream)
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $icon) { $icon.Dispose() }
        if ($hIcon -ne [IntPtr]::Zero) { [RPTNativeIconMethods]::DestroyIcon($hIcon) | Out-Null }
        if ($null -ne $greenPen) { $greenPen.Dispose() }
        if ($null -ne $cyanPen) { $cyanPen.Dispose() }
        if ($null -ne $barBrush) { $barBrush.Dispose() }
        if ($null -ne $gridPen) { $gridPen.Dispose() }
        if ($null -ne $borderPen) { $borderPen.Dispose() }
        if ($null -ne $backgroundBrush) { $backgroundBrush.Dispose() }
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }

    $iconBytes = (Get-Item $iconPath).Length
    if ($iconBytes -lt 512) { throw "Generated icon is unexpectedly small ($iconBytes bytes)." }

    # Verify both Win32 and WPF decoders so the XAML icon remains startup-safe.
    $verifyIcon = New-Object System.Drawing.Icon $iconPath
    try {
        if ($verifyIcon.Width -lt 16 -or $verifyIcon.Height -lt 16) {
            throw "Generated Win32 icon has invalid dimensions: $($verifyIcon.Width)x$($verifyIcon.Height)."
        }
    }
    finally { $verifyIcon.Dispose() }

    Add-Type -AssemblyName PresentationCore
    $iconStream = [IO.File]::OpenRead($iconPath)
    try {
        $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create(
            $iconStream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        if ($frame.PixelWidth -lt 16 -or $frame.PixelHeight -lt 16) {
            throw "Generated WPF icon has invalid dimensions: $($frame.PixelWidth)x$($frame.PixelHeight)."
        }
    }
    finally { $iconStream.Dispose() }

    Write-Step "RPT Markets icon generated and decoded successfully: $iconBytes bytes."
}

function Assert-PublishedIcon([string]$ExePath) {
    Write-Step 'Validating embedded EXE/taskbar icon...'
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($ExePath)
    if ($null -eq $icon) { throw 'Published EXE does not expose an associated Windows icon.' }

    try {
        if ($icon.Width -lt 16 -or $icon.Height -lt 16) {
            throw "Published EXE icon dimensions are invalid: $($icon.Width)x$($icon.Height)."
        }
        Write-Step "Embedded Windows icon validated: $($icon.Width)x$($icon.Height)."
    }
    finally { $icon.Dispose() }
}

function Assert-PublishedStartup([string]$ExePath) {
    Write-Step 'Smoke-testing packaged WPF startup and native tray host...'
    $process = Start-Process -FilePath $ExePath -ArgumentList '--background' -PassThru
    try {
        Start-Sleep -Seconds 10
        $process.Refresh()
        if ($process.HasExited) {
            throw "Packaged app exited during startup smoke test with code $($process.ExitCode)."
        }
        Write-Step 'Packaged WPF/native-tray startup smoke test passed.'
    }
    finally {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }
        }
        catch { }
        $process.Dispose()
    }
}

try {
    Set-Location $repoRoot
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw '.NET SDK was not found. Install .NET 9 SDK first.' }

    $version = (& dotnet --version).Trim()
    Write-Step "dotnet SDK selected: $version"
    $major = 0
    [void][int]::TryParse(($version -split '\.')[0], [ref]$major)
    if ($major -lt 9) { throw "Found .NET SDK $version; .NET 9 or newer is required." }

    $packageRefs = @(Get-ChildItem -Path $repoRoot -Recurse -Filter *.csproj | Select-String -Pattern '<PackageReference')
    if ($packageRefs.Count -ne 0) { throw "Dependency invariant failed: found $($packageRefs.Count) PackageReference entries." }

    Assert-LightweightGui
    Restore-AppIcon

    Invoke-DotNet 'Restoring projects...' @('restore', $solution, '--ignore-failed-sources')
    Invoke-DotNet 'Building Release...' @('build', $solution, '-c', 'Release', '--no-restore')
    Invoke-DotNet 'Running regression tests...' @('run', '--project', $testProject, '-c', 'Release', '--no-build')

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Write-Step 'Publishing compressed self-contained single-file Windows x64 EXE...'
    & dotnet publish $guiProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --source https://api.nuget.org/v3/index.json `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { throw "Single-file publish failed with exit code $LASTEXITCODE." }

    $publishedExe = Join-Path $publishDir 'RobloxPriceTracker.exe'
    if (-not (Test-Path $publishedExe)) { throw 'Publish completed but RobloxPriceTracker.exe was not produced.' }
    $dlls = @(Get-ChildItem $publishDir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
    if ($dlls.Count -gt 0) { throw "Single-file invariant failed: publish output contains $($dlls.Count) DLL(s)." }

    Assert-PublishedIcon $publishedExe
    Assert-PublishedStartup $publishedExe

    $publishedBytes = (Get-Item $publishedExe).Length
    $publishedMiB = [Math]::Round($publishedBytes / 1MB, 1)
    Write-Step "Published EXE size: $publishedBytes bytes ($publishedMiB MiB)."

    Copy-Item $publishedExe $finalExe -Force
    Write-Step "Build complete: $finalExe"
    exit 0
}
catch {
    Add-Content -Path $logPath -Value "ERROR: $($_.Exception.Message)"
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue }
}
