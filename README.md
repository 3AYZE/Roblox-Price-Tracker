# Roblox Price Tracker

<img src="src/RobloxPriceTracker.Gui/Assets/roblox_price_tracker.svg" alt="Roblox Price Tracker icon" width="88">

A Windows app for discovering Roblox Limiteds, comparing prices, and tracking items with alerts. Browse UGC accessories, emotes, animation packs, and Roblox Limiteds in one Live Drop Scanner.

**[Download RobloxPriceTracker.exe](https://github.com/3AYZE/Roblox-Price-Tracker/releases/download/latest/RobloxPriceTracker.exe)** · [Latest release](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest)

Requires **Windows 10/11 (64-bit)** and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

## Features

- **Live Drops:** One scanner for UGC Limiteds, Roblox Limiteds, emotes and Limited animation packs. Filter item types, search, or show only evidence-backed opportunities.
- **Tracker & Alerts:** Monitor resale prices, price history and targets, with optional background notifications.
- **Research:** Review item details, source evidence, trends and market analysis.
- **Paper Portfolio:** Test potential UGC entries without spending Robux.
- **Simple updates:** One Windows EXE with in-app update checks and SHA-256 verification.

**Important:** Original creator-stock prices, resale asking prices and RAP are different. Animation packs are shown for research only and do not receive investment ratings. Historical category benchmarks are not Roblox's live official minimum prices. Scores and forecasts are estimates, not guarantees.

For detailed evidence rules and limitations, see [UGC pricing research](docs/PRIMARY_PRICE_CATEGORY_RESEARCH_2026-11-17.md) and [price-integrity methodology](docs/UGC_PRICE_INTEGRITY_RESEARCH_2026-11-17.md).

## Download and updates

Download the single EXE from the [current release](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest). The app checks for new versions and verifies downloaded files with GitHub's SHA-256 digest.

## Privacy and data

This app uses public Roblox market data. No Roblox password, login, security cookie, or automatic buying is required.

Your tracked items, alerts, portfolio, settings and backups are stored locally at:

```text
%LOCALAPPDATA%\RobloxPriceTracker
```

Updating the EXE does not intentionally overwrite this data folder.

## Build from source

Install the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) on Windows and run `BUILD WINDOWS.bat`. The build runs the regression tests and creates `dist\RobloxPriceTracker.exe`.

Created by [3AYZE](https://github.com/3AYZE). [Architecture](docs/ARCHITECTURE.md) · [Changelog](docs/CHANGELOG.md)
