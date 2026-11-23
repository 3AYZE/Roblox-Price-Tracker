# UGC category buying references (October 2026)

These are **conservative buying ceilings**, not Roblox's official category floors or automatically discovered market prices. They apply exclusively to current verified Marketplace creator-owned original stock. Regional offers, resale prices, RAP, and catalog-only quotes never determine the ceiling or verified historical price.

| Roblox asset type | Category | Standard material ceiling (R$) |
| --- | --- | ---: |
| 8 | Hat | 95 |
| 41 | Hair | 65 |
| 42 | Face accessory | 55 |
| 43 | Neck | 55 |
| 44 | Shoulder | 55 |
| 46 | Back | 140 |
| 61 | Emote | 50 |

Unverified category references remain **insufficient evidence**, including front (45), waist (47), layered clothing (64–72), eyebrows (76), eyelashes (77), dynamic heads (79), and makeup (88–90). Dynamic heads are not automatically grouped with hat accessories. Material is `STANDARD` only when both material flags are explicitly false. A missing flag, conflicting `isPbr`/`isAdvancedMaterials` values, or simultaneous emissive and PBR flags produces `UNKNOWN`; these items remain researchable but cannot pass a buying-price gate. Special material defaults (emissive/PBR) are also unverified, even if the Marketplace explicitly identifies one material; creator prices and scan minima are not proof of a trustworthy premium.

The built-in values can be reviewed by creating `ugc-buying-price-ceilings.json` in the app's existing data directory, with a JSON object containing `ReferencePeriod: "2026-10"`, and optional numeric `Hat`, `Hair`, `Face`, `Neck`, `Shoulder`, `Back`, and `Emote` properties. An absent file uses the defaults. Invalid configuration reverts to all defaults and logs a warning. The file is not rewritten during scans.

Independent material benchmarks can be explicitly added under `SpecialMaterialBenchmarks`, keyed by `"8:EMISSIVE"` or `"8:PBR"` (or the appropriately reviewed asset type). Each entry requires positive `CeilingRobux`, a meaningful `EvidenceSource`, and a `VerifiedAtUtc` timestamp; no premium is configured by default. Only add category-wide, independently substantiated benchmarks, never a creator's self-reported current price. Human review of evidence remains necessary.

An item that costs above its applicable ceiling is excluded from default buying suggestions, model scores, and paper entries. Research mode still shows it with its real creator price, original stock, PNG, price-integrity result, and category buying explanation. A lower earlier Marketplace-verified creator price for the same asset, type, and creator adds the **ABOVE ENTRY PRICE — CREATOR REPRICED** label. This label describes observed pricing, not motive or market manipulation. A missing verified historical record does not assert a past increase; the current verified price can still pass its category ceiling, subject to all other opportunity requirements.

Resale enrichment uses a fixed per-scan request budget. Verified original-stock candidates inside their category ceiling get first use of the full budget; category turns preserve breadth, and both the starting category and candidate order within each category rotate between scans. Research-only candidates use only capacity left after the affordable pool has been selected. When affordable candidates fill every slot, research-only candidates wait for a later scan. Existing cache, cancellation, and HTTP 429 behavior remains in the resale data services.
