# Roblox Price Tracker

A professional Windows desktop market monitor for Roblox Limited/resellable items. It tracks the current **lowest reseller price**, records local price history, and alerts when an item reaches a configured target.

**Creator:** [3AYZE](https://github.com/3AYZE)

## v0.5.1 stock-market UI

The interface is designed like a compact stock/market app rather than a generic dashboard:

- Dark, low-distraction **RPT MARKETS** workspace
- Quote tape for tracked assets, near-target signals, target hits, and API health
- Stock-style watchlist columns for **Last / 24H / Trend / Vs Target / Target / Low / Signal / Updated**
- 24-hour percent and Robux movement with conventional green/red market coloring
- Compact per-asset sparklines built from locally recorded resale observations
- **24H change** sorting for finding the strongest movers quickly
- Fully dark selector/dropdown chrome across Price History, Watchlist, and Settings
- 1H / 24H / 7D / 30D / All History chart ranges
- Interactive trading charts with hover crosshair, exact price/time quote tooltip, range-direction coloring, area fill, and target reference line
- Asset detail view styled as a quote page with Last, Target, Low, range performance, and recent observations
- Keyboard workflow: **F5** refresh, **Ctrl+F** search, **Ctrl+N** track asset, **Ctrl+,** settings, **Enter** details, **Delete** remove, **Esc** close details
- Search, status filters, sorting, empty states, selection actions, and report export

## Features

- Roblox item thumbnails and verify-before-save asset workflow
- Current lowest resale price, target price, tracked low, target-distance display, 24H movement, and trend sparkline
- Search, status filters, movement/price/target sorting, stale/error/no-seller states
- Persistent alert history and price history
- Interactive item detail and report charts with 1H / 24H / 7D / 30D / All ranges
- CSV report export and ZIP data backup
- Windows tray monitoring and notifications
- Optional **Start with Windows** support
- Optional silent `--background` startup
- Configurable startup delay so Windows/networking can settle before automatic polling
- Automatic startup-path repair if the single EXE is moved and launched again
- Second-instance activation: opening the EXE while it is already running brings the existing instance forward
- Self-contained Windows x64 single-file publish flow
- Windows GitHub Actions build/test/publish workflow
- Versioned GitHub Release publishing with `RobloxPriceTracker.exe` and SHA-256 checksum assets
- Optional in-app GitHub Release updater with SHA-256 verification and restart-to-install replacement
- No third-party NuGet dependencies

## Updates

The desktop app can check GitHub Releases automatically every six hours and can also check manually from **Settings → Updates**. A newer release is downloaded into the local data directory, validated as a Windows PE file, verified against the published SHA-256 checksum, and only then offered for restart/install.

The updater never replaces local tracker data. Watchlist, history, alert state, settings, and logs remain under `%LOCALAPPDATA%\RobloxPriceTracker`.

The source repository is currently private. GitHub does not provide anonymous access to private-repository Releases, so a normal standalone EXE cannot silently authenticate to this private feed. The updater supports two deployment-safe options without embedding credentials in the executable:

- `RPT_GITHUB_TOKEN` — optional GitHub token supplied only on the local machine for private-release access.
- `RPT_UPDATE_REPOSITORY=owner/repository` — points the updater at a separate public release-only repository while the source repository stays private.

For public distribution, a public release feed is the recommended configuration. Do not embed a private GitHub token into the executable.

## Privacy and safety

The application is read-only marketplace monitoring software. It does **not** require a Roblox account login, does not use `.ROBLOSECURITY`, and does not perform automatic purchases.

## Data

Runtime data is stored outside the executable at:

```text
%LOCALAPPDATA%\RobloxPriceTracker
```

This includes the watchlist, settings, history, alerts, staged verified updates, and runtime logs.

## Windows startup/background behavior

Settings separates three concepts:

- **Start with Windows** — registers the current user under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Start quietly in the system tray** — adds `--background` to the Windows startup command.
- **Keep running in the background when I close the window** — closing the main window keeps the process and monitoring alive in the tray.

Windows startup also includes `--startup`. The configured startup delay (0–120 seconds) applies to the first automatic market check from a Windows startup launch.

No administrator permission is required for startup registration.

## Build

Requirements:

- Windows 10/11 x64
- .NET 9 SDK
- Internet access for the one-time self-contained publish runtime-pack download

Run:

```text
BUILD WINDOWS.bat
```

The script performs restore, Release build, regression tests, then creates:

```text
dist\RobloxPriceTracker.exe
```

The normal source restore/build has no third-party package dependencies. The final self-contained publish may download official Microsoft .NET runtime/linker packs from NuGet.

GitHub Actions performs the same Windows build, regression-test, and single-EXE publish process on `main`. The release workflow publishes a new GitHub Release only when the application version is new, and includes both the EXE and its SHA-256 checksum.

## Roblox API behavior

The tracker treats missing/no-reseller/off-sale/API-failure states explicitly and never converts a missing resale price into `0`. Primary catalog price is not substituted for the lowest reseller price.

## Project structure

```text
src/
  RobloxPriceTracker.Core/
  RobloxPriceTracker.Infrastructure/
  RobloxPriceTracker.Gui/
  RobloxPriceTracker.Cli/
tests/
  RobloxPriceTracker.SelfTest/
docs/
tools/
```

See `docs/CHANGELOG.md` and `docs/VALIDATION_REPORT.md` for release notes and validation details.
