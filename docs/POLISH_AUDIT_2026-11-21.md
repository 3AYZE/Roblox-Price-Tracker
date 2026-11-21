# Roblox Price Tracker v0.15.7 — system polish audit

Date: 2026-11-21. Target: existing .NET 9 Windows WPF application, based on `main` at `3608318`.

## Confirmed defects and corrections

| Priority | Component | Cause / user impact | Correction |
| --- | --- | --- | --- |
| High | Resale analytics | Rolling 24-hour, 7-day and 30-day sums were anchored to the newest *historical* daily bucket. Old observations could be presented as current trades and inflate liquidity or forecast confidence. | Require a dated, nonnegative bucket within 48 hours of the actual UTC evaluation time. Sum observations against the current clock, reject future/negative buckets, retain legitimate zero-volume coverage and show absent coverage as unknown. |
| High | Resale HTTP scheduling | HTTP 429 responses did not consistently throttle subsequent asset requests, and modern collectible 429 could be replaced by an unrelated legacy fallback result. | Apply a shared bounded Retry-After cooldown to legacy, modern and cross-asset enrichment; retain 429 through fallback and cache backoff responses. |
| Medium | Official Limited UI | Thumbnail URLs were awaited before scan rows appeared. The rows had immutable thumbnail properties, so simply fetching images later would not refresh WPF bindings. | Show market rows as soon as scan data is ready; hydrate PNGs asynchronously with property-change notifications, cancellation and stale-row checks. Preserve the selected inspector. |
| Medium | Official Limited table layout | Market rows used more vertical space than necessary, increasing scrolling for long results. | Compact Hunt / Market row spacing and enable row/column virtualization for the Official grid. |
| Medium | UGC resale evidence | Some 30-day secondary-volume evidence could include a future-dated bucket or a historical period lacking any fresh bucket. | Limit the 30-day evidence to dated, finite past observations and require recent series coverage before grading it as verified completed resale volume. Exclude future daily price points. |

## Preserved behavior and scope

Official Limited and UGC discovery stay distinct. Creator original-stock verification, category benchmarks, adjustable UGC price filter, existing 36-item Official enrichment allocation, Tracker, Alerts, Research, Paper Portfolio, local data recovery, GitHub updater and PNG image sources remain in place. No paid APIs, account cookies, new third-party packages, hardcoded item IDs or added runtime diagnostics.

The updater was inspected during the isolated smoke. It staged the published v0.15.7 EXE because this **unpublished test binary** carries the same version but has a different SHA-256 from the verified GitHub release. `GitHubReleaseIntegrity.NeedsDownload` explicitly supports same-version hash repair; this observation alone is not evidence of a production updater defect. Installation was not triggered.

## UI behavior: before / after

- **Before:** Official results waited for the thumbnail batch; long rows were less compact and changing thumbnail URLs on existing rows could not notify the image binding.
- **After:** Official market data and rankings render first. Asset PNGs arrive progressively when the thumbnail service responds; row images update through `INotifyPropertyChanged`. Official grid virtualization and compact Hunt / Market rows reduce unnecessary layout work.
- **Unverified visually:** No captured WPF screenshots or hands-on checks of resizing, DPI scaling, keyboard navigation, thumbnail appearance or physical UI alignment. Code-level changes are not substitutes for those checks.

## Verification evidence

| Check | Result | Evidence / limitation |
| --- | --- | --- |
| Release solution build | **PASS** | `dotnet build RobloxPriceTracker.sln -c Release --nologo`: 0 warnings, 0 errors after final code edits. |
| Regression suite | **PASS** | 111/111 tests after final edits (baseline: 107); includes stale/future/zero resale buckets, modern/legacy 429, 150-item cooldown fan-out, malformed JSON, request timeout and existing persistence/market-separation checks. |
| Single-file Windows EXE publish | **PASS** | Self-contained `win-x64` build with `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true`; 135,411,828 bytes; SHA-256 `88291A011058EDC90804D0C8816B11DB86D081D7D5EA23AF4ABC2CF3C55ED332`. |
| Isolated application startup | **PASS (smoke only)** | Launched built EXE with `--background` and a fresh `RPT_DATA_DIR` in `%TEMP%`; alive after 9 seconds; created tracker and log files in isolated profile; no crash file; current-user Windows startup value unchanged. Process deliberately terminated after smoke, leaving an expected temporary `session.active` marker. |
| Source diff check | **PASS** | `git diff --check`, after generated binary assets are restored. |
| Real Roblox live data availability | **UNVERIFIED** | Mocks validate parsing, missing values and rate limits. Real-time public endpoint coverage is dependent on Roblox. |
| UI manual acceptance, DPI, PNG fidelity | **UNVERIFIED** | Requires an interactive WPF review at different window sizes. |
| End-to-end updater installation and real data restore | **UNVERIFIED** | The actual installation and restart were deliberately not executed; existing JSON recovery has regression coverage. |
| Measured latency, memory and GPU comparison | **UNVERIFIED** | No before/after profiler or repeatable workload measurement; do not infer numerical speedups from virtualized rows or fewer blocked requests. |

The repository intentionally clears NuGet feeds for normal offline builds. The first self-contained publish could not resolve .NET 9.0.20 runtime/ILLink packages from the local cache. The successful retry supplied `-p:RestoreSources=https://api.nuget.org/v3/index.json` **only on the publish command**; it did not change repository NuGet configuration.

## Remaining risk and manual acceptance

1. Open both market tabs and verify PNGs, inspector selection, Hunt/Market sort/filter, scroll position and loading/empty states through repeated refreshes. Check 100%, 125% and 150% DPI and narrow windows.
2. Check real Official items with known recent transactions. Confirm that RAP and 7-day sales show values only when supported; an API dash remains valid when unavailable. Test UGC creator stock separately from resale stock.
3. Recheck network behavior under genuine Roblox throttling. The shared 429 cooldown intentionally defers later requests; missing data during backoff is expected.
4. Verify restart/persistence using a disposable profile, and review end-to-end user-prompted updater installation before promoting a new release.

**Disposition: PARTIAL.** The validated code corrections, build, regression suite and startup smoke succeeded. Full UI acceptance, live market-source coverage and measured performance remain unverified; keep this work in a draft PR until those checks are complete.
