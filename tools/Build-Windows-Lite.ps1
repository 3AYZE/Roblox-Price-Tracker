$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$guiProject = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\RobloxPriceTracker.Gui.csproj'
$dist = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $dist 'publish-lite-temp'
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
    Write-Step 'Smoke-testing Lite WPF/native-tray startup...'
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
        Write-Step 'Lite WPF/native-tray startup smoke test passed.'
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

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

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
    $dlls = @(Get-ChildItem $publishDir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
    if ($dlls.Count -gt 0) { throw "Lite single-file invariant failed: output contains $($dlls.Count) DLL(s)." }

    Assert-LiteStartup $publishedExe

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
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue }
}
