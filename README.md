# Roblox Price Tracker

<img src="src/RobloxPriceTracker.Gui/Assets/roblox_price_tracker.svg" alt="Roblox Price Tracker icon" width="88">

A Windows desktop application for tracking Roblox Limited resale prices, setting alerts, researching UGC Limited original-stock prices and evaluating market opportunities. Includes verified-versus-resale price distinctions, emissive/PBR comparisons and Limited animation-pack research. Built by [3AYZE](https://github.com/3AYZE).

**CURRENT WINDOWS DOWNLOAD:** [RobloxPriceTracker.exe](https://github.com/3AYZE/Roblox-Price-Tracker/releases/download/latest/RobloxPriceTracker.exe) · [Current app release (tag: latest)](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest)

**Important:** GitHub may display its **Latest** badge on the older v0.13.9 release for backwards-compatible automatic upgrades. **That bridge is not the current app.** For new installations, use the CURRENT WINDOWS DOWNLOAD above.

Requires **Windows 10/11 (64-bit)** and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

## Features

- **Tracker:** Follow Limited resale prices, set targets, inspect price history, and receive alerts.
- **UGC Hunter:** Scan eligible, catalog-purchasable UGC Limiteds across supported wearable and emote categories. View the **Marketplace-verified original-sale price** separately from the resale asking floor, RAP, supply, momentum, secondary-market evidence, and modeled risk. There is no universal 95 R$ original-price restriction.
- **Special-material comparisons:** Show original-sale lows by asset type and explicitly verified standard, emissive or PBR class; missing metadata stays unknown. Historical research reference prices are never presented as official live category floors.
- **Animation Packs research:** Explore Limited animation bundles separately from emotes and accessories. Display original creator-stock prices only when Marketplace-verified; no bundle auto-buy, investment rating or paper entry.
- **Price integrity:** Track verified creator-price changes over time and screen UGC investment recommendations for creator-anchor price distortion. A confirmed exclusion requires corroborating market evidence; missing creator-price or executed-resale history is labeled **INSUFFICIENT_PRICE_DATA**, not asserted to be manipulation. Excluded items remain available for explicit research but cannot appear as default Hunter investment picks or paper-investment entries.
- **Research:** Explore charts, Roblox market data where available, and historical trends.
- **Paper Portfolio:** Test ideas with simulated positions—no Robux spent.
- **Background monitoring:** Keep tracking in the system tray, optionally start with Windows, and export or back up local data.

Forecasts and opportunity scores are estimates, not guarantees. A creator's original-stock price, a reseller's asking price, RAP, and a completed resale are different kinds of evidence. The lowest **observed** original-sale price by category is not a verified official Roblox price floor. Newly purchased Limiteds can have a resale holding period, so limited early trading activity is not itself evidence of manipulation.

For methodology, sampled item findings, limitations, and evidence standards, see the [UGC price-integrity research report](docs/UGC_PRICE_INTEGRITY_RESEARCH_2026-11-17.md).

## Which release should I download?

| GitHub tag | Intended use | Download manually? |
| --- | --- | --- |
| [latest](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest) | **CURRENT Windows app** — the permanent, single-EXE release | **Yes** — download RobloxPriceTracker.exe |
| [v0.13.9](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/v0.13.9) | **Legacy updater bridge** for v0.13.8-and-older installations | **No** — automatic migration only |
| [main-latest](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/main-latest) | **Legacy Latest-channel migration bridge** | **No** — automatic migration only |

GitHub's **Latest** badge is reserved for the v0.13.9 bridge because older clients use GitHub's generic releases/latest API. The current app's release tag is literally **latest**. Keeping those distinct avoids breaking existing update installations.

## Updates

The current app checks the [permanent current-app release](https://github.com/3AYZE/Roblox-Price-Tracker/releases/tag/latest), downloads a newer build, verifies the SHA-256 digest, and offers to restart and install it. The current release contains **one** Windows EXE; no separate checksum download or manual reinstall is needed. Legacy migration files are retained for compatibility, not for new installs.

## Privacy and your data

The app uses public Roblox market data. It does **not** require your Roblox login, collect a `.ROBLOSECURITY` cookie, or automatically buy or sell items.

Saved items, alerts, history, portfolio, settings, and backups are kept separately from the EXE at:

```text
%LOCALAPPDATA%\RobloxPriceTracker
```

Updates do not intentionally replace this folder. You can also export a backup from the app.

## Build from source

Install the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) on Windows, then run `BUILD WINDOWS.bat`. The build runs the regression suite and produces `dist\RobloxPriceTracker.exe`.

Developers: [Architecture](docs/ARCHITECTURE.md) · [Changelog](docs/CHANGELOG.md)
