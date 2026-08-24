# Roblox Price Tracker changelog

## v0.3.3
- Added per-user **Start with Windows** registration through HKCU with automatic moved-EXE path repair.
- Added `--startup` and optional `--background` launch modes with configurable 0-120 second startup delay.
- Added background startup with no main-window requirement, close-to-tray control, tray startup toggle, and monitoring-state tray text.
- Added second-instance activation so opening the EXE while it is already running brings the existing window forward.
- Added creator attribution for [3AYZE](https://github.com/3AYZE) in the UI, README, and executable metadata.


## v0.3.0
- Reworked the WPF application shell and navigation into a denser business dashboard.
- Added thumbnail presentation through Roblox's public asset thumbnail endpoint.
- Added watchlist search, status filtering, sorting, target-distance text, near-target status, issue state, and empty states.
- Reworked Add Item into a verify-first workflow with item identity and current-market preview.
- Reworked Item Details with range controls, current/target/tracked-low metrics, range low/high/change, and chart target line.
- Reworked Reports with 24h/7d/30d/all ranges, metrics, and CSV export.
- Added settings diagnostics and ZIP backup creation.
- Added a dependency-free Windows tray workflow with alert balloons, open/check/pause-resume/exit actions, and optional close-to-tray behavior.
- Serialized GUI-triggered/automatic check entry to reduce overlapping poll races.
- Preserved v0.2.7 self-contained single-file publish flow and the existing persistent state format.
