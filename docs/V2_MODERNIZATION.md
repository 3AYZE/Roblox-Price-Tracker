# V2 modernization plan (incremental)

Current safe production baseline: v0.14.1. Prior v0.14.0 adaptive request scheduling and progressive Hunter UI changes were rolled back after reports of crashes and increased scan time. A successful CI build is not a substitute for representative Windows/runtime measurements.

## Phase 1 — Stability observability (v0.14.2)

- Record managed fatal/UI/task failures locally in logs/crash.log without collecting credentials or uploading reports.
- Detect an unclean previous exit via a session marker (this is an indicator, not proof of a software crash).
- Measure UGC discovery, UGC total, thumbnails, resale enrichment, and Official Limited total in logs/scan-metrics.jsonl. Both log files rotate at 1 MiB.
- Test successful, failed and cancelled measurement paths, and session marker cleanup.
- Preserve all existing scan order, throttling and ranking semantics.

## Phase 2 — Scanner boundary extraction (future, **not yet implemented**)

- Extract discovery, verification and analysis behind interfaces, without altering behavior.
- Add deterministic fixtures for 429/Retry-After, stale data, malformed payloads, network timeouts, cancellation and shutdown.
- Establish baseline medians and p95 for first verified result, full scans, request count, UI responsiveness, crash rate and peak memory.
- Require a canary run and measurable regression gates before changing concurrency or fixed delays. Do not ship a shared global mouse or network hook.

## Phase 3 — Shared market data and freshness (future, **not yet implemented**)

- Define canonical item snapshot and separate primary price, resale floor, RAP, inventory and verification timestamp.
- Add per-field TTLs and observable "verified/cached/partial/unavailable" states.
- Deduplicate repeated requests only after concurrency and cancellation race tests; never replace an actionable verified value with a missing placeholder.
- Keep protected user-data backups and support rollback to the pre-migration state.

## Phase 4 — WPF view-model decomposition (future, **not yet implemented**)

- Move page state from MainWindow partial classes into independently tested view models.
- Preserve keyboard shortcuts, saved filters, focus, selection, taskbar/tray and auto-updater behavior.
- Test repeated page transitions, minimizing/closing to tray, shutdown while scanning, offline start and long-running use.
- Refine risk scoring only with regression examples and provenance; forecasts remain estimates, not guarantees.

## Release gates (all phases)

Each phase is independently reviewable. Pass existing regression tests, Windows packaging, isolated EXE startup, icon integrity and saved-data recovery. Diagnose actual user crash logs and measure performance before labeling a scanner optimization faster. Keep the permanent GitHub single EXE and older updater migration bridges.
