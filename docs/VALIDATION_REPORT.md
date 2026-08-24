# Roblox Price Tracker v0.4.0 validation report

The market-terminal redesign is validated on a real Windows GitHub Actions runner using .NET 9.

## Windows pipeline

The repository workflow performs the following on `windows-latest`:

- Restores the complete solution.
- Builds the WPF application in Release configuration.
- Runs the dependency-free regression/self-test project.
- Publishes a self-contained `win-x64` single-file executable.
- Verifies that `RobloxPriceTracker.exe` is produced without companion DLL files.
- Uploads the finished EXE as the `RobloxPriceTracker-win-x64` workflow artifact.

The v0.4.0 market-terminal build completed successfully through build, regression tests, single-file publish, and artifact upload.

## Release invariants

- 0 third-party `PackageReference` entries.
- Roblox primary catalog price is never substituted for lowest reseller price.
- Missing/no-reseller/off-sale/API-failure data is never interpreted as `0` Robux.
- Anonymous Roblox 403 CSRF challenge retry behavior remains covered by regression tests.
- Persistent alert state, history, notification outbox, rate-limit state, and polling sequence protections remain intact.
- Windows startup/background behavior remains available through HKCU Run registration, `--startup`, optional `--background`, startup delay, second-instance activation, and moved-EXE path repair.
- Creator attribution remains [3AYZE](https://github.com/3AYZE).
- Persistent tracker data schema remains compatible with the v0.3.x release line.

## v0.4.0 UI validation

- Main dashboard uses the market-terminal visual system and preserves all existing code-behind contracts.
- Watchlist search, filtering, sorting, item actions, selection state, and empty states compile against the production WPF view.
- Reports retain asset selection, date ranges, price metrics, chart rendering, history table, and CSV export.
- Add Item and Item Details use the same dark quote-terminal design language.
- Notification banner, diagnostics, settings, system tray, and background monitoring controls remain wired.
