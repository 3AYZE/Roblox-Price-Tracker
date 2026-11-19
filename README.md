# Roblox Price Tracker

<img src="src/RobloxPriceTracker.Gui/Assets/roblox_price_tracker.svg" alt="Roblox Price Tracker icon" width="88">

A Windows app for finding Roblox Limiteds, tracking prices, and setting alerts. Browse Official and UGC Limiteds separately, with item images and simple market details.

**[Download RobloxPriceTracker.exe](https://github.com/3AYZE/Roblox-Price-Tracker/releases/download/latest/RobloxPriceTracker.exe)** · [Latest release](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest)

Requires **Windows 10/11 (64-bit)** and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

## Features

- **Discover:** Separate Official and UGC Limiteds, item images, and price filters. UGC shows verified original prices or clearly marked, unverified catalog quotes; animation packs are research-only.
- **Tracker & Alerts:** Monitor resale prices, price history and targets, with optional background notifications.
- **Research:** Review item details, source evidence, trends and market analysis.
- **Paper Portfolio:** Test potential UGC entries without spending Robux; items include thumbnails.
- **Simple updates:** One Windows EXE with in-app update checks and SHA-256 verification.

**Important:** Verified original-sale prices, unverified catalog quotes, resale asks and RAP are different. UGC defaults to an adjustable 1,000 R$ maximum-price screen, not a Roblox official floor or fair-value estimate. Missing resale asks are never guessed. Animation packs remain research-only. Scores and forecasts are estimates, not guarantees.

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
