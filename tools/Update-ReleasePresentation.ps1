$ErrorActionPreference = 'Stop'

# Change only the current GitHub release's title, notes, and Latest status.
# Preserve the permanent "latest" tag and the existing EXE asset for in-app updates.
if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    throw 'GITHUB_REPOSITORY is required.'
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN is required.'
}

$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $root 'src/RobloxPriceTracker.Gui/RobloxPriceTracker.Gui.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }

$title = "Roblox Price Tracker v$version"
$notes = @'
## Download

**RobloxPriceTracker.exe** — Windows 10/11 (64-bit).

Requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

## Features

- Track Roblox Limited resale prices, price history, and alerts.
- Discover UGC Limiteds and compare verified original-sale prices with resale prices.
- Research emissive/PBR accessories and Limited animation packs.
- View market analysis, test a paper portfolio, and get in-app updates.

**Note:** Animation packs are research-only. Category price references are not live official Roblox minimums.

Updates retain your locally saved items and settings.
'@

& gh release edit latest --repo $env:GITHUB_REPOSITORY --title $title --notes $notes --latest=true
if ($LASTEXITCODE -ne 0) { throw 'Failed to update the current release.' }

$current = gh api "repos/$env:GITHUB_REPOSITORY/releases/tags/latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $current.tag_name -ne 'latest' -or
    $current.name -ne $title -or
    @($current.assets).Count -ne 1 -or
    @($current.assets)[0].name -ne 'RobloxPriceTracker.exe' -or
    @($current.assets)[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$') {
    throw 'Current release validation failed: expected one EXE and a SHA-256 digest.'
}

$githubLatest = gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $githubLatest.tag_name -ne 'latest') {
    throw 'GitHub Latest badge must point to the current release.'
}

Write-Host "Release verified: $title (tag latest, one EXE)."
