# UGC Resale Intelligence v0.8.0

UGC Hunter v0.8.0 ranks live paid UGC Limiteds by projected post-sellout resale economics rather than sellout speed alone.

Core model changes:
- Applies the 50% community-Limited reseller share to gross break-even and net ROI.
- Uses profitability hard gates so a fast-selling expensive item cannot become a top recommendation when projected resale economics are weak.
- Recalibrates scarcity for the practical paid UGC Limited supply range instead of awarding near-max scarcity to most drops.
- Combines original-stock demand, sell-through, price/capital efficiency, projected resale value, confidence, risk, and late-stage entry timing.
- Enriches only the strongest modeled candidates with cached Roblox resale aggregates and reseller-book depth.
- Uses RAP, current resale floor, observed reseller count, listing depth around the floor, recent resale volume, and floor/RAP stability when available.
- Labels resale liquidity as STRONG, HEALTHY, THIN, VERY THIN, CROWDED, or MODELED.
- Produces HIGH RESALE, STRONG, WATCH, SPECULATIVE, or AVOID recommendations.
- Keeps near-sellout timing actionable when the resale case is strong, while an AVOID profitability result caps the entry score.

Paper Portfolio row-level value, P/L, ROI, and profit coloring now use estimated net reseller proceeds rather than gross listing price.

Regression coverage adds cases for 2x break-even at a 50% reseller share, expensive near-sellout rejection, profitable/liquid ranking, unprofitable live-market hard gating, and reseller-book depth parsing.
