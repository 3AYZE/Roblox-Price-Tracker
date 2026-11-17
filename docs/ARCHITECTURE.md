# Architecture

Roblox Price Tracker is a .NET 9 Windows WPF app. Runtime data is stored under `%LOCALAPPDATA%\RobloxPriceTracker`, separately from the EXE.

| Project | Responsibility |
| --- | --- |
| `src/RobloxPriceTracker.Core` | Domain models, price validation, alert logic, polling and rate limits. |
| `src/RobloxPriceTracker.Infrastructure` | Roblox catalog/resale access, UGC discovery, storage, data recovery, analytics and provider coordination. |
| `src/RobloxPriceTracker.Gui` | WPF workspaces, background tray monitoring, research, paper portfolio, settings and verified self-updates. |
| `src/RobloxPriceTracker.Cli` | Command-line utilities. |
| `tests/RobloxPriceTracker.SelfTest` | Dependency-free regression tests. |
| `tools` | Windows build, standalone EXE publishing and icon generation. |

## Data protection

Tracker records, alerts and price history are persisted locally. The repository retains rolling backups and protected checkpoints so startup and subsequent saves can recover missing tracked records without overriding newer item settings. The app should be closed before manually restoring files.

## Release process

GitHub Actions builds, tests and smoke-tests the Windows EXE before updating the fixed `latest` release. That release has **one** downloadable `RobloxPriceTracker.exe`; the client validates GitHub's asset SHA-256 digest before installation. Older `v0.13.9` and `main-latest` release files remain available only to migrate existing updaters.

## Diagnostic files

The app keeps local crash details in `logs/crash.log` and scan-stage measurements in `logs/scan-metrics.jsonl` within the same local data directory. These rotate at 1 MiB, keep one prior file, and are never transmitted automatically. An `logs/session.active` marker records whether the prior process reached normal shutdown; an unexpected exit is not necessarily an app crash. Review local logs before sharing them. See [V2 modernization plan](V2_MODERNIZATION.md) for the guarded rollout.

## Primary UGC Limited pricing (v0.14.3)

For each hydrated UGC Limited, Hunter distinguishes the actual **original-stock listing price** from lowestResalePrice. A category-aware comparison computes the **lowest observed Marketplace-verified original price per exact asset type** within the current scan. It is not a verified Roblox official category floor: the documented official floor endpoint is authenticated and may depend on asset type, emissive/PBR flags, creation method and region. The app does not request or store Roblox session cookies or API keys to retrieve it.

Filters: **Observed low (by type)**, **All verified originals**, **All original candidates**. Catalog-only rows never count toward a verified observed low. Emissive/dance name filters are explicitly labeled hints, not verified visual/animation metadata. Sold-out original stock and experience-only products stay excluded. The existing 120-ID discovery cap and sequential rate-limit behavior remain intact; a single-page emote feed reserves a bounded portion of candidates. Additional Roblox types require confirmed support before enabling them.
