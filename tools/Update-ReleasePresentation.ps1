$ErrorActionPreference = 'Stop'

# Edits GitHub release metadata only, never tags, assets or app binaries.
# The permanent "latest" tag is the current download; v0.13.9 and main-latest
# are retained migration endpoints for older installations.
if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    throw 'GITHUB_REPOSITORY is required.'
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN is required for release metadata updates.'
}

$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content (Join-Path $root 'src/RobloxPriceTracker.Gui/RobloxPriceTracker.Gui.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }

function Edit-ReleaseMetadata([string]$Tag, [string]$Title, [string]$Notes, [string[]]$ExtraArgs) {
    $arguments = @('release', 'edit', $Tag, '--repo', $env:GITHUB_REPOSITORY, '--title', $Title, '--notes', $Notes)
    if ($ExtraArgs) { $arguments += $ExtraArgs }
    & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "Failed to edit release metadata for $Tag." }
}

$currentTitle = "CURRENT DOWNLOAD - Roblox Price Tracker v$version (Windows)"
$currentNotes = @'
# CURRENT WINDOWS DOWNLOAD

**For new installations and current app updates, download RobloxPriceTracker.exe from this release.**

## What this app includes

- Roblox Official Limited resale-price tracking, alerts and price history.
- UGC Limited Hunter, with creator original-stock prices separate from resale asking prices and RAP.
- Observed original-sale price comparisons by asset category and verified material class (standard, PBR or emissive); unknown material stays unknown.
- Price-integrity screening that distinguishes corroborated price distortion from incomplete resale evidence.
- Separate Limited Animation Packs research (bundle IDs are not emote or asset IDs). Bundle research does not produce investment recommendations.
- Paper portfolio, local backups, Windows tray monitoring and in-app updates.

**Price disclosure:** Historical examples such as 95 R$ hats, higher emissive prices, and 300 R$ animation packs are research references, NOT live official Roblox dynamic price floors. The app uses a verified original creator-stock offer as its entry cost.

## Install or update

1. Download RobloxPriceTracker.exe below, the only file needed for new installations.
2. Requires Windows 10/11 (64-bit) and the .NET 9 Desktop Runtime: https://dotnet.microsoft.com/en-us/download/dotnet/9.0
3. The in-app updater verifies GitHub's SHA-256 asset digest.

## Which release is current?

**This release is current.** Its stable tag is latest for the existing in-app updater.

GitHub's separate Latest badge can appear on v0.13.9 because older updaters use the /releases/latest API route. That badge DOES NOT mean the old compatibility bridge is the current application. Do not manually install v0.13.9 or main-latest.

The older migration releases remain available exclusively to protect existing installations.
'@

$legacyTitle = 'LEGACY UPDATER ONLY - Roblox Price Tracker v0.13.9 (not for new installs)'
$legacyNotes = @'
# LEGACY UPDATER COMPATIBILITY - NOT THE CURRENT APPLICATION

**Do not download or install files from this release for a new installation.**

This v0.13.9 release exists only as an automatic migration bridge for old Roblox Price Tracker installations (v0.13.8 and earlier). Old updaters require GitHub's /releases/latest endpoint, so GitHub may show the Latest badge on this bridge.

**CURRENT DOWNLOAD:** https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest

Use RobloxPriceTracker.exe from the current release instead. Do not rename this legacy tag or remove its migration files, as old updater clients depend on them.
'@

$legacyChannelTitle = 'LEGACY CHANNEL BRIDGE - main-latest (not for new installs)'
$legacyChannelNotes = @'
# OLD UPDATE-CHANNEL MIGRATION - NOT THE CURRENT APPLICATION

**Not a new-installation download.** This release exists solely for the old Latest-channel update path.

Legacy updater compatibility marker: ONE_TIME_BRIDGE_v0.13.9

**CURRENT DOWNLOAD:** https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest

Install the single RobloxPriceTracker.exe from the current release instead. The main-latest tag and its existing legacy files must remain available for older updater compatibility.
'@

Edit-ReleaseMetadata 'latest' $currentTitle $currentNotes @('--latest=false')
Edit-ReleaseMetadata 'v0.13.9' $legacyTitle $legacyNotes @()
Edit-ReleaseMetadata 'main-latest' $legacyChannelTitle $legacyChannelNotes @('--prerelease')

$current = gh api "repos/$env:GITHUB_REPOSITORY/releases/tags/latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $current.tag_name -ne 'latest' -or
    $current.name -ne $currentTitle -or
    @($current.assets).Count -ne 1 -or
    @($current.assets)[0].name -ne 'RobloxPriceTracker.exe' -or
    @($current.assets)[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$') {
    throw 'Current release validation failed: expected original single EXE and SHA-256 digest.'
}

$legacyStable = gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $legacyStable.tag_name -ne 'v0.13.9') {
    throw 'The older /releases/latest updater route must continue to reach v0.13.9.'
}
$legacyChannel = gh api "repos/$env:GITHUB_REPOSITORY/releases/tags/main-latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $legacyChannel.prerelease -or
    -not ([string]$legacyChannel.body).Contains('ONE_TIME_BRIDGE_v0.13.9')) {
    throw 'The old Latest-channel bridge must stay marked as a prerelease with its compatibility marker.'
}
Write-Host "Release presentation verified: current v$version; legacy migration tags retained."
