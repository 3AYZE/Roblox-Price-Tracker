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
