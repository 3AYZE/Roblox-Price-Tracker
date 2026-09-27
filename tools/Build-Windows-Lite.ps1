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
$finalExe = Join-Path $dist 'RobloxPriceTracker.exe'
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
    Write-Step 'Generating the Roblox Price Tracker multi-resolution icon...'
    & (Join-Path $PSScriptRoot 'Generate-BrandAssets.ps1') 2>&1 | Tee-Object -FilePath $logPath -Append
    if (-not (Test-Path $iconPath)) { throw 'The application icon was not generated.' }
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::new($iconPath)
    try {
        if ($icon.Width -lt 16 -or $icon.Height -lt 16) { throw 'Generated application icon is invalid.' }
    }
    finally { $icon.Dispose() }
    foreach ($asset in @($logoPath, $menuPath, $windowPath)) {
        if (-not (Test-Path $asset)) { throw "Missing generated brand asset: $asset" }
    }
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

    $isolatedExe = Join-Path $isolatedDir 'RobloxPriceTracker.exe'
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
