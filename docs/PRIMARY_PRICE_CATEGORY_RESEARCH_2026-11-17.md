# Original-sale category research — November 17, 2026

**Differentiate six data types:** Official dynamically calculated minimum; historical category benchmark; actual creator original-stock offer; reseller asking floor; RAP; and executed secondary sales. The six are not interchangeable.

## Original creator-stock reference prices

These figures are historical common/observed original-sale listings, **not authenticated current official dynamic floors**. They must never be substituted for the asset's verified actual purchase price or used as automatic investment/distortion thresholds.

| Category | Standard original (R$) | Emissive original (R$) | Evidence |
|---|---:|---:|---|
| Hat (asset 8) | 95 | 500 | October–November 2026 observed/common listings |
| Face accessory (42) | 95 | 500 | Observed listings |
| Hair accessory (41) | 65 | 350 | Observed listings |
| Neck accessory (43) | 55 | Not verified | Standard only |
| Shoulder accessory (44) | 55 | 300 | Observed listings |
| Front accessory (45) | 55 | Not verified | Standard only |
| Back accessory (46) | 140 | 500 | Historical common standard and observed emissive; neither is guaranteed minimum |
| Waist accessory (47) | 65 | Not verified | Standard only |
| Emote asset (61) | 50 | Not established | Emotes are NOT animation-pack bundles |
| Animation pack bundle (BundleType.Animations = 2) | **300** | No distinct emissive minimum confirmed | Documented original-sale floor at September 2026 launch |

The lowest verified original listing within one scan may differ from the most common historical value. This app displays its own *scan observed low* separately from the *historical comparison*.

## Roblox documents

- [Publish to Marketplace](https://create.roblox.com/docs/marketplace/publish-to-marketplace): Dynamic price constraints, original offers, regional pricing.
- [Animation packs](https://create.roblox.com/docs/avatar/animation-packs) and [September 2026 announcement](https://devforum.roblox.com/t/full-release-create-and-sell-animation-packs-on-marketplace/4861803): 300 R$ animation pack floor at launch.
- [September 2026 emissive release](https://devforum.roblox.com/t/full-release-create-and-sell-emissive-items-on-marketplace/4846756): Approximately 5× category floor for emissive at launch. Do not confuse publication advances with sale prices.
- [Marketplace search parameters](https://create.roblox.com/docs/projects/assets/api): Category 12, Subcategory 38 targets AnimationBundles, with itemType distinguishing bundles from assets.
- [BundleType enum](https://create.roblox.com/docs/reference/engine/enums/BundleType): Animations = 2.

The documented item-configuration official price-floor API requires authentication. This anonymous desktop app does **not** claim live official price-floor access and must never request user cookies or account credentials.

## Application verification contracts

1. Search rows are discovery only, even when their price field is positive.
2. Verified original offers require authoritative Marketplace Items data confirming positive creator-stock price and units, plus Shop purchase location.
3. Material classification uses *explicit* source flags only: true emissive wins over PBR; true PBR is a separate class; false/false means standard; any missing field leaves class UNKNOWN unless a true flag is present. Titles containing glow, neon, or animated are *hints*, not proof.
4. Separate observed-original minima by **asset type AND material class**. Unknowns remain in their own bucket.
5. Neither resale floors, creator asking prices for other items, RAP, nor historical references can establish a live official floor or a completed resale.
6. Bundle IDs must never be passed to asset resale, tracker, analyzer, or paper-investment paths. Limited animation packs are research-only until independently validated resale economics exist.
7. Creator repricing is not automatically manipulation. Changes in Roblox dynamic floors or regional pricing, limited resale holding periods and normal demand remain plausible explanations. Preserve INSUFFICIENT_PRICE_DATA when trade evidence is incomplete.
8. A missing, stale or rate-limited observation stays UNKNOWN. It must not be coerced to zero or a false floor.

## Operational scope

Hunter retains its stable sequential asset scanner and a single current EXE updater. The Animation Packs research window issues one on-demand catalog search and at most 12 sequential paced bundle detail requests, then verifies available original stock via the existing Marketplace Items service. When collectible IDs or primary verification are unavailable, offers remain browse-only and no original price is asserted.

Emissive/PBR flags may be absent in current Roblox catalog responses: the correct result is MATERIAL UNKNOWN, **not** a guessed standard or emissive class. The historical table is a small evidence-labeled research reference, not a representative statistical sample. Refresh these estimates only with dated, region-aware and independently validated sources.