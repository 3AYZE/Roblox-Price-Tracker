# Roblox Price Tracker changelog

## v0.9.0
- Added a dedicated **Analyzer** workspace: paste any Roblox catalog URL or asset ID for a direct market deep-dive.
- Analyzer separates Roblox-observed primary price, sold/remaining supply, resale floor, RAP, seller depth, and resale volume from modeled economics.
- Added a weighted **Data Quality** grade with explicit verified/missing evidence and observation freshness instead of hiding incomplete inputs behind a score.
- Added Hunter DATA grades and evidence tooltips; the selected-drop inspector now shows freshness and source quality alongside supply.
- Added one-click **ANALYZE** from a Hunter row so scanner candidates can be promoted into deeper research without copying IDs manually.
- Added **Creator Intelligence** using a bounded sample of recent Limiteds: verified count, active/sold-out mix, average sell-through, profitable-floor rate after Roblox reseller proceeds, and median floor/cost multiple.
- Creator history is on-demand only and capped to a small sample so normal Hunter refreshes remain lightweight and avoid unnecessary Roblox requests.
- Added analyzer/catalog/data-quality/creator-track-record regressions and bumped the Lite Windows app to v0.9.0.

## v0.8.4
- Reworked UGC Hunter market data around Roblox's authoritative collectible Marketplace Items API instead of relying on incomplete catalog fields.
- Primary price, sold count, remaining stock, total stock, current resale floor, reseller availability, and collectible IDs now come from verified Roblox marketplace data when available.
- Sold-out/unpurchasable items are rejected after authoritative verification and are also blocked from catalog fallback when remaining stock is zero.
- Hunter uses the modern collectible resale endpoint directly when the collectible ID is already known, reducing stale legacy lookups and unnecessary requests.
- Reworked Hunter columns and inspector to prioritize real sold/remaining/floor/RAP/resale-volume/break-even data, with model estimates clearly separated.
- Added frozen regression references from `[ANIMATED] White Heart Aura` (asset `129868485129975`): 1,000 R$ original price, 3,000 sold, 0 remaining, 999 R$ live floor, and a 2,000 R$ reseller break-even.

## v0.8.2
- Fixed UGC Hunter showing **QUIET / OFFLINE** even while Roblox had active Limited UGC listings.
- Confirmed Roblox's current Limited search response can return placeholder `price: 0` and null purchase counts; Hunter now treats search rows as discovery IDs instead of final market data.
- Added two-stage discovery: best-selling Limited IDs are hydrated through Roblox's catalog batch-details endpoint before paid/on-sale/supply filters and resale scoring run.
- Added Roblox's anonymous CSRF challenge/retry flow to Hunter detail hydration without cookies or `.ROBLOSECURITY` credentials.
- Replaced the six-route catalog fan-out with one bounded Limited discovery request plus one detail batch, reducing HTTP 429 exposure.
- Added bounded Retry-After-aware throttling and a distinct **RATE LIMITED** state instead of mislabeling Roblox throttling as OFFLINE.
- Expanded the Hunter category filter to every UGC wearable category already supported by the scanner.
- Added four regression cases for placeholder search rows, hydrated Shop candidates, experience-only exclusion, and anonymous CSRF hydration; suite is now 33 tests.

## v0.8.1
- Expanded Hunter coverage beyond legacy accessories to layered clothing, shoes, dresses/skirts, dynamic heads, eyebrows/eyelashes, and makeup asset types.
- Stopped treating `timedOptions` metadata as an automatic experience-only exclusion.
- Added broader source diagnostics while investigating Roblox Marketplace catalog drift.

## v0.8.0
- Rebuilt **UGC Hunter** around post-sellout resale economics instead of treating fast sellout as a buy signal by itself.
- Added Roblox community-Limited reseller-share economics, including gross break-even, estimated net proceeds, net profit, and net ROI.
- Added dedicated resale-potential scoring with profitability hard gates so expensive near-sellout items with weak expected returns are capped or marked as poor entries.
- Added reseller-market enrichment for the strongest Hunter candidates, including current floor, RAP, recent resale volume, active reseller count, listing depth, liquidity, and floor-stability signals.
- Added penalties for thin or crowded resale books, unstable floors, weak resale volume, excessive purchase price, and live resale markets that do not clear break-even.
- Recalibrated scarcity and near-sellout entry timing for the paid UGC Limited market so low remaining supply helps only when demand and resale economics support it.
- Corrected Paper Portfolio market value, P/L, ROI, win rate, and profit coloring to use estimated reseller net proceeds instead of gross listing value.
- Limited resale enrichment to the strongest candidates and retained caching/bounded requests to avoid excessive Roblox API traffic.
- Added five resale-intelligence regression cases covering break-even math, expensive fast-sellout rejection, profitable/liquid ranking, unprofitable live-market rejection, and reseller-book parsing.
- Bumped the Windows application to v0.8.0 while preserving existing Hunter history, paper positions, tracker data, updater behavior, and the single-EXE release pipeline.

## v0.7.1
- Fixed the UGC Hunter startup/live-scan HTTP 400 by removing the incompatible Updated + SortAggregation query combination.
- Replaced the failing legacy Collectibles route with Accessories-first Marketplace discovery plus broader fallbacks, bounded 429 retry/backoff, and paced pagination.
- Added response-detail logging for failed Hunter catalog routes so future Roblox API changes are diagnosable without exposing credentials.
- Preserved the last successful Hunter rows and selected asset across refreshes instead of degrading the workspace when a later live request fails.
- Background Hunter refresh failures are now non-blocking when cached market data exists; first-load failures show an explicit OFFLINE state.
- Bumped the verified Windows release to v0.7.1 so the existing in-app updater can deliver the fix.

## v0.7.0
- Reorganized the desktop application into a market-terminal workspace: **Market / UGC Hunter / Tracker / Portfolio / Research / Alerts / Settings**.
- Added a live **UGC Hunter** scanner for qualifying catalog-buyable Limited UGC drops using anonymous Roblox catalog data.
- Hunter scans multiple recent collectible result pages and excludes Roblox-authored classics, free/off-sale items, timed-ownership items, and experience-only/developer-API-only sale locations.
- Added Opportunity, Entry, Risk, and Confidence scores with separate meanings instead of collapsing every signal into one opaque rating.
- Added repeated 1m/5m/15m sales-velocity tracking, acceleration/cooling classification, remaining-supply analysis, supply absorption, launch lifecycle phases, and sellout ETA.
- Hunter prefers Roblox `totalQuantity` and `unitsAvailableForConsumption` when available and can infer sold quantity from decreasing remaining supply when aggregate `purchaseCount` lags.
- Added bear/base/bull resale scenario ranges and deterministic explanation/risk text; scenario values are explicitly presented as estimates rather than guaranteed resale prices.
- Added a persistent right-side selected-drop inspector and stock-screener-style Hunter table with price, remaining supply, velocity, ETA, Opportunity, Entry, Risk, Confidence, and lifecycle phase.
- Added local Hunter history in `%LOCALAPPDATA%\RobloxPriceTracker\ugc-hunter-history.json`.
- Added a functional **Paper Portfolio**. `PAPER ENTRY` records a simulated 1-unit Hunter position with the exact model snapshot at entry and never spends Robux.
- Added paper-position persistence in `%LOCALAPPDATA%\RobloxPriceTracker\paper-portfolio.json`.
- Portfolio refreshes actual lowest reseller floors after a paper-traded item becomes resellable and calculates simulated market value, P/L, ROI, and priced-position win rate.
- Preserved the existing resale Tracker, statistical forecast/backtesting, alert engine, background monitoring, tray workflow, updater, local data formats, and single-EXE publish pipeline.

## v0.6.2
- Fixed the v0.6.1 startup failure caused by the WPF runtime failing to decode the application ICO while loading window XAML.
- Reworked icon generation to create a native Windows/WPF-compatible ICO from the canonical PNG artwork during the Windows build.
- Added build-time validation through both the Win32 icon decoder and WPF `BitmapFrame` decoder before compiling the release.
- Added a packaged EXE startup smoke test using `--background`; releases now fail if the finished WPF application cannot initialize and remain running.
- Enabled .NET single-file compression while keeping the app self-contained and requiring no separate .NET installation.
- Reduced the standalone EXE from 170,496,514 bytes in v0.6.1 to approximately 74,404,812 bytes, a reduction of about 56%.
- Preserved the embedded EXE/taskbar icon, background monitoring, updater, forecasts, watchlist data, and all existing local app-data formats.

## v0.6.0
- Added a deterministic **Market Forecast** engine for the next observed lowest reseller quote, with expected range, fair value, market direction, and confidence instead of presenting a single price as certain.
- Forecasting requires at least **8 valid local price observations**; before that the UI explicitly reports **Insufficient data**.
- Added anonymous Roblox resale-aggregate retrieval for **Recent Average Price (RAP)** and daily sales-volume history. Legacy Limiteds use the Economy resale-data endpoint; migrated collectible items can fall back through `collectibleItemId` to the Marketplace Sales resale-data endpoint.
- Added a 15-minute success cache / 5-minute failure cache for resale aggregates so normal UI refreshes do not repeatedly hammer Roblox analytics endpoints.
- Added **sales/day** and a 0–100 **liquidity score** derived from recent sale-volume aggregates and data recency.
- Forecast inputs now combine recent quote momentum, short/medium EMA behavior, robust median, log-linear trend, normalized volatility, RAP, sales liquidity, and data age.
- Added **1H / 6H / 24H target-hit probability** estimates plus an estimated time-to-target when the fitted direction supports a meaningful ETA.
- Added a persistent `forecast-history.json` backtest store outside core tracker state. Every forecast can be evaluated against the next observed reseller quote.
- Added rolling forecast quality metrics: evaluated sample count, mean absolute percentage error (MAPE), derived accuracy, and expected-range coverage.
- Expanded the stock quote board with **Forecast / Confidence / Sales per Day / Target 24H** fields plus forecast-confidence and forecast-price sorting.
- Added a dedicated Market Forecast panel to Item Details showing next forecast, fair value, expected range, direction, confidence, RAP, sales velocity, liquidity, target probabilities, ETA, and backtest performance.
- Forecast direction uses conventional market colors (green bullish / red bearish) and all forecast UI clearly labels the output as a statistical estimate rather than a guaranteed future price.
- Roblox sales inputs are aggregate RAP and daily volume series; the application does not claim to receive private or individual transaction records.
- Added five forecasting regression tests covering resale parsing, minimum-data gating, clean downtrend behavior, liquidity/confidence behavior, and persistent next-quote backtesting.

## v0.5.1
- Reworked the watchlist into a more recognizable stock-app quote board with **Last / 24H / Trend / Vs Target / Target / Low / Signal / Updated** market data.
- Added 24-hour price movement in both percent and Robux, using conventional green/red market colors.
- Added compact per-item sparklines built from locally recorded resale-price observations.
- Added a **24H change** sort mode for quickly finding the strongest movers.
- Added a **1 Hour** range alongside 24H / 7D / 30D / All History in market reports and item details.
- Upgraded price charts with range-direction coloring, hover crosshair, exact price/time quote tooltip, area fill, and target reference line.
- Updated item details to behave more like a quote page, including range-colored movement metrics and dark market-status badges.
- Added desktop-terminal keyboard controls: **F5** refresh, **Ctrl+F** search, **Ctrl+N** track asset, **Ctrl+,** settings, **Enter** details, **Delete** remove, and **Esc** close details.
- Renamed the presentation to **RPT MARKETS / ROBLOX MARKET WATCH** while preserving the original mouse artwork and the existing background/startup/update systems.
- Switched displayed version/backup metadata to the executable's runtime version so future releases stay synchronized automatically.

## v0.4.2
- Restored the exact original mouse artwork supplied for Roblox Price Tracker branding instead of the later cropped/tiny logo treatment.
- Regenerated the Windows multi-size `.ico` from the original mouse image for the EXE, taskbar, and WPF window icon.
- Unified the sidebar/menu/dialog PNG resources around the original mouse artwork so the branding stays consistent throughout the application.
- Bumped the desktop application and diagnostics metadata to v0.4.2 so the corrected icon build can be published as a distinct verified update.

## v0.4.1
- Fixed the Price History item/range selectors using a fully custom dark WPF ComboBox template so Windows light-theme chrome no longer produces white unreadable dropdowns.
- Increased Price History title, metric, table, and header contrast for better readability against the market-terminal background.
- Corrected the sidebar brand icon by using the dedicated logo asset with a larger native-proportion container instead of the clipped menu artwork.
- Added an optional in-app GitHub Release updater with automatic six-hour checks and a manual **Check for Update** action in Settings.
- Added staged update downloads under the local app-data directory, Windows PE validation, SHA-256 verification, writable-install-location validation, restart-to-install replacement, and safe preservation of watchlist/history/settings.
- Added a versioned GitHub Release workflow that only publishes when the application version is new and attaches `RobloxPriceTracker.exe` plus `RobloxPriceTracker.exe.sha256` after the Windows build and regression suite succeed.
- Added private-release support through optional `RPT_GITHUB_TOKEN` and configurable release source through `RPT_UPDATE_REPOSITORY`, without embedding GitHub credentials into the EXE.
- Published v0.4.1 after a clean Windows build with 0 warnings / 0 errors and 19/19 regression tests passing.

## v0.4.0
- Redesigned the application around a professional dark **market-terminal / stock-dashboard** visual system.
- Added a compact quote tape for tracked items, near-target signals, target hits, and Roblox API health.
- Reworked the watchlist into a denser quote board with **Last / Vs Target / Target / Session Low / Signal / Updated** information.
- Added finance-style signal colors: green for target hits, amber for near-target items, and red/orange for market/data issues.
- Reworked price-history rendering with a dark trading chart, subtle area fill, grid, compact price labels, and target reference line.
- Restyled Add Item as an asset verification/target-entry workflow and Item Details as a market quote/history view.
- Preserved search, filters, sorting, empty states, report metrics/export, diagnostics, startup/background controls, tray monitoring, and persistent alerts.
- Updated executable metadata and diagnostics to v0.4.0.
- Validated the redesign through the repository's Windows GitHub Actions build/test/single-EXE pipeline.

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
- Reworked Reports with 24h/7d/30d/all ranges, metrics, CSV export.
- Added settings diagnostics and ZIP backup creation.
- Added a dependency-free Windows tray workflow with alert balloons, open/check/pause-resume/exit actions, and optional close-to-tray behavior.
- Serialized GUI-triggered/automatic check entry to reduce overlapping poll races.
- Preserved v0.2.7 self-contained single-file publish flow and the existing persistent state format.