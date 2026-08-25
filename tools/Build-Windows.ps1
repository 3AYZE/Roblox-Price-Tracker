$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'logs'
$logPath = Join-Path $logDir 'build.log'
$solution = Join-Path $repoRoot 'RobloxPriceTracker.sln'
$guiProject = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\RobloxPriceTracker.Gui.csproj'
$testProject = Join-Path $repoRoot 'tests\RobloxPriceTracker.SelfTest\RobloxPriceTracker.SelfTest.csproj'
$iconPath = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\Assets\mouse_app.ico'
$iconBase64Path = Join-Path $repoRoot 'src\RobloxPriceTracker.Gui\Assets\mouse_app.ico.b64'
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

function Restore-AppIcon {
    Write-Step 'Restoring canonical Windows application icon...'
    if (-not (Test-Path $iconBase64Path)) { throw "Canonical icon source was not found: $iconBase64Path" }

    $encoded = (Get-Content -Path $iconBase64Path -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($encoded)) { throw 'Canonical icon source is empty.' }

    try {
        $bytes = [Convert]::FromBase64String($encoded)
    }
    catch {
        throw "Canonical icon source is not valid base64: $($_.Exception.Message)"
    }

    if ($bytes.Length -lt 4096) { throw "Decoded icon is unexpectedly small ($($bytes.Length) bytes)." }
    if ($bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
        throw 'Decoded icon does not contain a valid Windows ICO header.'
    }

    [IO.File]::WriteAllBytes($iconPath, $bytes)
    Write-Step "Windows icon restored: $($bytes.Length) bytes."
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
    finally {
        $icon.Dispose()
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

    Restore-AppIcon

    Invoke-DotNet 'Restoring projects...' @('restore', $solution, '--ignore-failed-sources')
    Invoke-DotNet 'Building Release...' @('build', $solution, '-c', 'Release', '--no-restore')
    Invoke-DotNet 'Running regression tests...' @('run', '--project', $testProject, '-c', 'Release', '--no-build')

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Write-Step 'Publishing self-contained single-file Windows x64 EXE...'
    & dotnet publish $guiProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --source https://api.nuget.org/v3/index.json `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
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
