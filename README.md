# Roblox Price Tracker

A Windows desktop tracker for Roblox Limited/resellable item prices. It monitors the current **lowest reseller price**, records local price history, and alerts when an item reaches a configured target.

**Creator:** [3AYZE](https://github.com/3AYZE)

## Features

- Business-style WPF dashboard and watchlist
- Roblox item thumbnails and verify-before-save Add Item workflow
- Current lowest resale price, target price, tracked low, and target-distance display
- Search, status filters, sorting, stale/error/no-seller states
- Persistent alert history and price history
- Item detail charts and 24H / 7D / 30D / All reports
- CSV report export and ZIP data backup
- Windows tray monitoring and notifications
- Optional **Start with Windows** support
- Optional silent `--background` startup
- Configurable startup delay so Windows/networking can settle before automatic polling
- Automatic startup-path repair if the single EXE is moved and launched again
- Second-instance activation: opening the EXE while it is already running brings the existing instance forward
- Self-contained Windows x64 single-file publish flow
- No third-party NuGet dependencies

## Privacy and safety

The application is read-only marketplace monitoring software. It does **not** require a Roblox account login, does not use `.ROBLOSECURITY`, and does not perform automatic purchases.

## Data

Runtime data is stored outside the executable at:

```text
%LOCALAPPDATA%\RobloxPriceTracker
```

This includes the watchlist, settings, history, alerts, and runtime logs.

## Windows startup/background behavior

Settings separates three concepts:

- **Start with Windows** — registers the current user under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Start quietly in the system tray** — adds `--background` to the Windows startup command.
- **Keep running in the background when I close the window** — closing the main window keeps the process and monitoring alive in the tray.

Windows startup also includes `--startup`. The configured startup delay (0–120 seconds) applies to the first automatic market check from a Windows startup launch.

No administrator permission is required for the startup registration.

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

See `docs/CHANGELOG.md` and `docs/VALIDATION_REPORT.md` for current release notes and validation details.
