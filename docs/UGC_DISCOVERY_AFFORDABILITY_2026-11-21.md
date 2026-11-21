# UGC discovery coverage and affordability update — November 21, 2026

This proposal is scoped to **Roblox Price Tracker (Windows / .NET 9)**, not a Roblox Studio experience. It is intended to be reviewed and tested on its feature branch before releasing a new EXE.

## Changes

- Set the UGC scanner and Overview pick-board's default original-price cap to **500 R$**, down from 1,000 R$. The user can still choose 1,000 / 2,500 / 5,000 / Any in the existing compact price filter.
- Supplement existing collectible and community sales feeds with **one page of recently listed community collectibles**. This is cached with existing discovery and uses the same rate-limit/backoff and hydration verification.
- Preserve the scanner's **120 total candidate ID cap**. Reserve at most 10 emote IDs, up to 28 distinct IDs spread across discovery feeds (at most four per feed), and approximately 20 recent IDs. Backfill unused reservations with scored candidates.
- Do not reserve ordinary accessories as emotes if an emote-search request falls back to broad catalog results. The asset type must be **explicitly 61** in search metadata to qualify for an emote reserve.
- Preserve authoritative Marketplace Items validation before treating an asset as an active creator-stock opportunity, and retain safe suppression of unknown/sold-out/stale listings, item image handling, and research-only animation bundles.

## Category and material research

See [original-sale category research](PRIMARY_PRICE_CATEGORY_RESEARCH_2026-11-17.md). In particular, observed standard hair around 65 R$, hats/faces around 95 R$, shoulders/necks/front around 55 R$, waist around 65 R$, and back around 140 R$ are **historical comparisons**, not universal current Roblox price floors. Verified emissive offers use a *different* reference bucket, and PBR has no separately established universal price uplift in the current dataset. Never invent a live floor, assume “glowing” in a title proves emissive, or add an unsupported PBR surcharge.

Roblox documents **dynamic pricing floors** that may change. Price caps are buyer budget screens, not proof of manipulation or a profit forecast. An item above 500 R$ is hidden by the default screen, but the user can explicitly inspect it with a higher cap or Any. Evidence-based repricing exclusions still use the pre-existing verified historical policy, not these category references.

Official source references:
- https://create.roblox.com/docs/marketplace/publish-to-marketplace
- https://create.roblox.com/docs/marketplace/frequently-asked-questions
- https://devforum.roblox.com/t/full-release-create-and-sell-emissive-items-on-marketplace/4846756

## Regression and rollout

The source includes regression cases for selection caps, deduplication, per-feed representation, recent/emote reserves, a fallback containing mixed asset types, and the new default budget. Run the existing Windows workflow / .NET 9 self-tests. Compare candidate counts and verified active-stock counts against the previous release using the **same observation window**; do not claim more profitable items merely because raw discovery volume increased.

No authentication cookies, scraping bypasses, automatic purchases, new dependencies, or additional runtime logs have been added. The legacy default branch and published EXE must not change until tests and a manual Windows review pass.
