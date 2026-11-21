# Official Limited RAP and 7-day-sales coverage — November 21, 2026

## User-reported symptom
The Official Limiteds table (e.g., Chill Cap and Recycled Cardboard Scarf) displays resale ask prices but shows dashes for most RAP and 7-day sales values. This is not a sign that a dash equals zero sales or that the resale floor is a RAP.

## Root causes and bounded repairs

1. The scanner discovers as many as 180 official Roblox-issued Limiteds, but fetched deep resale analytics for only the first 36, **every time**. Now the existing **36-ID per-scan** API budget rotates to untried / least-recently-enriched items, with a 40-minute in-memory cache of previously obtained evidence. Repeated refreshes can improve coverage without making 180 parallel API calls or increasing HTTP fan-out.
2. Some Roblox-issued Limiteds have migrated to collectible IDs and no longer return usable/current data from `economy.roblox.com/v1/assets/{id}/resale-data`. The existing modern `marketplace-sales/v1/item/{collectibleItemId}/resale-data` fallback now also runs when the legacy response contains a **demonstrably stale historical series**, even if legacy RAP was nonzero. If catalog item-details cannot resolve an ID, a second anonymous ID lookup tries `economy.roblox.com/v2/assets/{id}/details` before giving up. It never substitutes an asset ID for a collectible ID.
3. Weekly sales calculations no longer anchor the window to the historical **last available** volume point. An item has a verified 7D figure only if its source contains a nonnegative dated volume point from the past 48 hours; sums are relative to the actual UTC scan time. If source data is absent or stale, 7D and 30D display **—**, not 0. A zero-volume recent point legitimately displays **0**.
4. RAP from a demonstrably stale legacy series is not used in opportunity scoring or displayed as current. The scanner distinguishes items with usable resale evidence from items merely **attempted**. In the Official list header, a compact coverage summary reports how many items currently have RAP and fresh 7D volume.

## Limits

- A newly scanned set can still show many dashes until the next data-bearing refreshes; Roblox can return missing/inaccessible resale metrics even when a real reseller ask exists.
- The 36-per-scan budget, history persistence and official/UGC market separation are preserved; no third-party price service, Roblox login, cookie access, automatic purchasing, new dependencies or invented values.
- Cached in-memory coverage is intentionally not persisted between app restarts. Resale price and volume source data can expire; score availability is never treated as permanent.
- Data that is demonstrably stale is not silently reused. Date windows and missing-source reasons should remain visible through inspector evidence.

## Verification

Run the existing .NET 9 Windows CI with new tests for scan rotation, old volume rejection, legitimate zero sales, and collectible-ID fallback. Manual UI checks: original screenshot items in Official Limiteds; click Refresh more than once and confirm source counts can rise, while unsupported RAP/sales remain dashes; verify no UGC leakage, asset PNGs, sort/filter stability and rate limiting.

References:
- https://devforum.roblox.com/t/economyv1assetsidresale-data-returns-recentaverageprice-null-for-collectibleitem-system-limiteds/4651514
- https://devforum.roblox.com/t/gold-clockwork-headphones-dont-function-as-a-normal-limited/3412084
- https://create.roblox.com/docs/cloud/reference/features/assets
