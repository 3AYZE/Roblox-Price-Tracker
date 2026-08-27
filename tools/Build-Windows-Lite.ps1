$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RobloxPriceTracker.sln'
$guiProject = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\RobloxPriceTracker.Gui.csproj'
$testProject = Join-Path $repoRoot 'tests\RobloxPriceTracker.SelfTest\RobloxPriceTracker.SelfTest.csproj'
$assetsDir = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\Assets'
$iconPath = Join-Path $assetsDir 'mouse_app.ico'
$logoPath = Join-Path $assetsDir 'mouse_logo.png'
$menuPath = Join-Path $assetsDir 'mouse_menu.png'
$windowPath = Join-Path $assetsDir 'mouse_window.png'
$dist = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $dist 'publish-lite-temp'
$isolatedDir = Join-Path $dist 'lite-smoke-temp'
$finalExe = Join-Path $dist 'RobloxPriceTracker-Lite.exe'
$logDir = Join-Path $repoRoot 'logs'
$logPath = Join-Path $logDir 'build-lite.log'

New-Item -ItemType Directory -Force -Path $dist, $logDir | Out-Null
Set-Content -Path $logPath -Value "Roblox Price Tracker Lite Windows build - $(Get-Date -Format o)"

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

function Initialize-BrandAssets {
    Write-Step 'Generating validated RPT Markets icon assets...'
    Add-Type -AssemblyName System.Drawing
    if (-not ('RPTLiteIconMethods' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RPTLiteIconMethods {
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@
    }

    $bitmap = New-Object System.Drawing.Bitmap 64, 64, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 11, 18, 32))
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 47, 65, 87)), 2
    $bars = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 39, 196, 214))
    $line = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 93, 224, 230)), 3
    $arrow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 45, 216, 129)), 3
    $hIcon = [IntPtr]::Zero
    $icon = $null
    $stream = $null

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.FillEllipse($background, 3, 3, 58, 58)
        $graphics.DrawEllipse($border, 4, 4, 56, 56)
        $graphics.FillRectangle($bars, 16, 36, 5, 10)
        $graphics.FillRectangle($bars, 27, 29, 5, 12)
        $graphics.FillRectangle($bars, 38, 31, 5, 8)
        $points = [System.Drawing.Point[]]@(
            (New-Object System.Drawing.Point 14, 43),
            (New-Object System.Drawing.Point 26, 34),
            (New-Object System.Drawing.Point 36, 37),
            (New-Object System.Drawing.Point 50, 21)
        )
        $graphics.DrawLines($line, $points)
        $graphics.DrawLine($arrow, 50, 21, 49, 29)
        $graphics.DrawLine($arrow, 50, 21, 42, 22)

        $bitmap.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Save($menuPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Save($windowPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $hIcon = $bitmap.GetHicon()
        if ($hIcon -eq [IntPtr]::Zero) { throw 'Windows failed to create the application icon.' }
        $icon = [System.Drawing.Icon]::FromHandle($hIcon)
        $stream = [IO.File]::Create($iconPath)
        $icon.Save($stream)
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $icon) { $icon.Dispose() }
        if ($hIcon -ne [IntPtr]::Zero) { [RPTLiteIconMethods]::DestroyIcon($hIcon) | Out-Null }
        $arrow.Dispose(); $line.Dispose(); $bars.Dispose(); $border.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }

    $check = New-Object System.Drawing.Icon $iconPath
    try {
        if ($check.Width -lt 16 -or $check.Height -lt 16) { throw 'Generated application icon is invalid.' }
    }
    finally { $check.Dispose() }
}

function Assert-LiteStartup([string]$ExePath) {
    Write-Step 'Smoke-testing isolated Lite startup...'
    $desktopRuntime = & dotnet --list-runtimes | Select-String -Pattern '^Microsoft\.WindowsDesktop\.App 9\.'
    if (-not $desktopRuntime) { throw 'The build runner does not have the .NET 9 Windows Desktop Runtime.' }

    $process = Start-Process -FilePath $ExePath -ArgumentList '--background' -PassThru
    try {
        Start-Sleep -Seconds 8
        $process.Refresh()
        if ($process.HasExited) { throw "Lite app exited during startup smoke test with code $($process.ExitCode)." }
        Write-Step 'Isolated Lite startup smoke test passed.'
    }
    finally {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }
        } catch { }
        $process.Dispose()
    }
}

try {
    Set-Location $repoRoot
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK was not found.' }

    foreach ($dir in @($publishDir, $isolatedDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    Initialize-BrandAssets
    Invoke-DotNet 'Restoring projects...' @('restore', $solution, '--ignore-failed-sources')
    Invoke-DotNet 'Building Release...' @('build', $solution, '-c', 'Release', '--no-restore')
    Invoke-DotNet 'Running regression tests...' @('run', '--project', $testProject, '-c', 'Release', '--no-build')

    Write-Step 'Publishing framework-dependent single-file Windows x64 Lite EXE...'
    & dotnet publish $guiProject `
        -c Release `
        -r win-x64 `
        --self-contained false `
        --source https://api.nuget.org/v3/index.json `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:RPTEmbedIcon=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { throw "Lite single-file publish failed with exit code $LASTEXITCODE." }

    $publishedExe = Join-Path $publishDir 'RobloxPriceTracker.exe'
    if (-not (Test-Path $publishedExe)) { throw 'Lite publish completed but RobloxPriceTracker.exe was not produced.' }
    $publishedFiles = @(Get-ChildItem $publishDir -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'RobloxPriceTracker.exe') {
        throw 'Lite single-file invariant failed; publish output contained extra files.'
    }

    $isolatedExe = Join-Path $isolatedDir 'RobloxPriceTracker-Lite.exe'
    Copy-Item $publishedExe $isolatedExe -Force
    Assert-LiteStartup $isolatedExe

    $bytes = (Get-Item $publishedExe).Length
    Write-Step "Lite EXE size: $bytes bytes ($([Math]::Round($bytes / 1MB, 2)) MiB)."
    Copy-Item $publishedExe $finalExe -Force
    Write-Step "Lite build complete: $finalExe"
    exit 0
}
catch {
    Add-Content -Path $logPath -Value "ERROR: $($_.Exception.Message)"
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    foreach ($dir in @($publishDir, $isolatedDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
