$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$guiProject = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\RobloxPriceTracker.Gui.csproj'
$dist = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $dist 'publish-lite-temp'
$isolatedDir = Join-Path $dist 'lite-smoke-temp'
$finalExe = Join-Path $dist 'RobloxPriceTracker-Lite.exe'
$logDir = Join-Path $repoRoot 'logs'
$logPath = Join-Path $logDir 'build-lite.log'

New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Content -Path $logPath -Value "Roblox Price Tracker Lite Windows build - $(Get-Date -Format o)"

function Write-Step([string]$Text) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Text"
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

function Assert-LiteStartup([string]$ExePath) {
    Write-Step 'Smoke-testing isolated Lite WPF/native-tray startup...'
    $desktopRuntime = & dotnet --list-runtimes | Select-String -Pattern '^Microsoft\.WindowsDesktop\.App 9\.'
    if (-not $desktopRuntime) {
        throw 'The build runner does not have a .NET 9 Windows Desktop Runtime for the Lite startup test.'
    }

    $process = Start-Process -FilePath $ExePath -ArgumentList '--background' -PassThru
    try {
        Start-Sleep -Seconds 8
        $process.Refresh()
        if ($process.HasExited) {
            throw "Lite app exited during startup smoke test with code $($process.ExitCode)."
        }
        Write-Step 'Isolated Lite WPF/native-tray startup smoke test passed.'
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
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK was not found.'
    }

    foreach ($dir in @($publishDir, $isolatedDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    Write-Step 'Publishing framework-dependent single-file Windows x64 Lite EXE...'
    & dotnet publish $guiProject `
        -c Release `
        -r win-x64 `
        --self-contained false `
        --source https://api.nuget.org/v3/index.json `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { throw "Lite single-file publish failed with exit code $LASTEXITCODE." }

    $publishedExe = Join-Path $publishDir 'RobloxPriceTracker.exe'
    if (-not (Test-Path $publishedExe)) { throw 'Lite publish completed but RobloxPriceTracker.exe was not produced.' }

    $publishedFiles = @(Get-ChildItem $publishDir -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'RobloxPriceTracker.exe') {
        $names = ($publishedFiles | ForEach-Object Name) -join ', '
        throw "Lite single-file invariant failed: expected only RobloxPriceTracker.exe, found: $names"
    }

    $isolatedExe = Join-Path $isolatedDir 'RobloxPriceTracker-Lite.exe'
    Copy-Item $publishedExe $isolatedExe -Force
    Assert-LiteStartup $isolatedExe

    $bytes = (Get-Item $publishedExe).Length
    $mib = [Math]::Round($bytes / 1MB, 1)
    Write-Step "Lite EXE size: $bytes bytes ($mib MiB)."
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
