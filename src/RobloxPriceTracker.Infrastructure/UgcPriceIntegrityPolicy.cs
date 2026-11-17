namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Evidence-based creator-price integrity checks for UGC Limited investments.
/// A creator listing, a resale ASK, RAP, and an actual resale are four different
/// signals. No single creator-controlled reference or resale ask can determine
/// a bargain or independently establish fair market value.
/// </summary>
public static class UgcPriceIntegrityPolicy
{
    public const string Passed = "PASSED";
    public const string Insufficient = "INSUFFICIENT_PRICE_DATA";
    public const string Excluded = "EXCLUDED_PRICE_DISTORTION";

    public sealed record Configuration
    {
        public double MinCreatorPriceMultiple { get; init; } = 3.0;
        public int MinCreatorPriceIncreaseRobux { get; init; } = 100;
        public int MinimumObservationSeparationMinutes { get; init; } = 15;
        public int MaxHistoryAgeDays { get; init; } = 180;
        // Roblox can restrict resale for up to 30 days after original purchase.
        // Earliest locally verified creator offer is only a conservative lower
        // bound on eligibility age, NOT a guaranteed release-date timestamp.
        public int MinimumResaleEligibilityDays { get; init; } = 30;
        public double MaximumResaleAskToCreatorRatio { get; init; } = 0.80;
        public double MaximumUnsupportedResales30d { get; init; } = 2;
        public double IndependentSupportMinimumResales30d { get; init; } = 8;
        public double IndependentSupportDailyPriceRatio { get; init; } = 0.70;
        public double MaximumUnsupportedDailyPriceRatio { get; init; } = 0.60;
        public int MinimumDailyPriceEvidenceDays { get; init; } = 3;
        public int MinimumComparableCount { get; init; } = 3;
        public double ComparablePriceDivergenceMultiple { get; init; } = 3.0;

        public bool IsValid =>
            MinCreatorPriceMultiple is >= 2 and <= 15 &&
            MinCreatorPriceIncreaseRobux is >= 25 and <= 10000 &&
            MinimumObservationSeparationMinutes is >= 5 and <= 1440 &&
            MaxHistoryAgeDays is >= 7 and <= 730 &&
            MinimumResaleEligibilityDays is >= 14 and <= 60 &&
            MaximumResaleAskToCreatorRatio is >= 0.2 and < 1 &&
            MaximumUnsupportedResales30d is >= 0 and <= 15 &&
            IndependentSupportMinimumResales30d is >= 3 and <= 100 &&
            IndependentSupportDailyPriceRatio is >= .25 and <= 1.5 &&
            MaximumUnsupportedDailyPriceRatio is >= .1 and < 1 &&
            MinimumDailyPriceEvidenceDays is >= 2 and <= 30 &&
            MinimumComparableCount is >= 3 and <= 20 &&
            ComparablePriceDivergenceMultiple is >= 2 and <= 20;
    }

    public sealed record PrimaryPriceObservation(
        long AssetId, long CreatorId, int AssetType, int PrimaryPrice,
        long? OriginalUnitsRemaining, DateTimeOffset ObservedAtUtc,
        bool MarketplaceVerified, string Source);

    public sealed record ResaleEvidence(
        long? LowestResaleAsk,
        double? Rap,
        double? CompletedResales30d,
        double? DailyAveragePriceMedian30d,
        int PriceEvidenceDays,
        int ComparableCount = 0,
        double? ComparableVerifiedResaleMedian = null);

    public sealed record Decision(
        string Classification, IReadOnlyList<string> ReasonCodes,
        string Explanation, int? EarliestVerifiedPrimaryPrice = null,
        double? CreatorPriceMultiple = null)
    {
        public bool IsExcluded => Classification == Excluded;
    }

    /// <summary>
    /// Conservative hard exclusion:
    /// (1) two chronologically distinct marketplace-verified creator prices;
    /// (2) at least a configured large repricing;
    /// (3) resale asks below the new creator anchor; AND
    /// (4) independent weak trade evidence or a supported comparable divergence.
    /// High independently evidenced resale demand can override the price-rise
    /// concern. Unknown volume is NEVER coerced to zero.
    /// </summary>
    public static Decision Evaluate(
        PrimaryPriceObservation current,
        IEnumerable<PrimaryPriceObservation> historicalObservations,
        ResaleEvidence market,
        Configuration? settings = null)
    {
        var rules = settings is { IsValid: true } ? settings : new Configuration();
        if (!IsVerifiedOriginal(current))
            return new Decision(Insufficient, ["PRIMARY_PRICE_UNVERIFIED"],
                "Current creator price is not verified as purchasable original stock.");

        var prior = historicalObservations
            .Where(x => IsVerifiedOriginal(x) &&
                        x.AssetId == current.AssetId && x.AssetType == current.AssetType &&
                        x.CreatorId == current.CreatorId &&
                        x.ObservedAtUtc <= current.ObservedAtUtc.AddMinutes(-rules.MinimumObservationSeparationMinutes) &&
                        x.ObservedAtUtc >= current.ObservedAtUtc.AddDays(-rules.MaxHistoryAgeDays))
            .OrderBy(x => x.PrimaryPrice)
            .ThenBy(x => x.ObservedAtUtc)
            .FirstOrDefault();

        if (prior is null)
        {
            if (HasIndependentDemandSupport(current, market, rules))
                return new Decision(Passed, ["INDEPENDENT_RESALE_SUPPORT", "HISTORY_UNAVAILABLE"],
                    "No verified earlier creator price; independent resale turnover and daily prices support the market.");
            return new Decision(Insufficient, ["VERIFIED_CREATOR_HISTORY_MISSING"],
                "Verified earlier creator prices are unavailable; current creator asking price is not a value benchmark.");
        }

        var multiple = (double)current.PrimaryPrice / prior.PrimaryPrice;
        var largeRise = multiple >= rules.MinCreatorPriceMultiple &&
            current.PrimaryPrice - prior.PrimaryPrice >= rules.MinCreatorPriceIncreaseRobux;

        if (!largeRise)
            return new Decision(Passed, ["NO_LARGE_VERIFIED_REPRICING"],
                "Verified earlier original-sale observations do not satisfy the configured large-increase gate.",
                prior.PrimaryPrice, multiple);

        if (HasIndependentDemandSupport(current, market, rules))
            return new Decision(Passed, ["LARGE_VERIFIED_REPRICING", "INDEPENDENT_RESALE_SUPPORT"],
                "Creator price rose substantially, but independently recorded resale turnover and prices support the new level.",
                prior.PrimaryPrice, multiple);

        var creatorAnchorBargain = market.LowestResaleAsk is > 0 &&
            market.LowestResaleAsk.Value <= current.PrimaryPrice * rules.MaximumResaleAskToCreatorRatio;
        var weakTransactions = market.CompletedResales30d is >= 0 &&
            market.CompletedResales30d <= rules.MaximumUnsupportedResales30d;
        var confirmedLowerTradePrices =
            market.CompletedResales30d is >= 3 &&
            market.PriceEvidenceDays >= rules.MinimumDailyPriceEvidenceDays &&
            market.DailyAveragePriceMedian30d is > 0 &&
            market.DailyAveragePriceMedian30d < current.PrimaryPrice * rules.MaximumUnsupportedDailyPriceRatio;
        var comparableDivergence =
            market.ComparableCount >= rules.MinimumComparableCount &&
            market.ComparableVerifiedResaleMedian is > 0 &&
            current.PrimaryPrice >= market.ComparableVerifiedResaleMedian.Value * rules.ComparablePriceDivergenceMultiple;

        var reasonCodes = new List<string> { "LARGE_VERIFIED_REPRICING" };
        if (creatorAnchorBargain) reasonCodes.Add("CREATOR_ANCHOR_FALSE_BARGAIN");
        if (weakTransactions) reasonCodes.Add("INSUFFICIENT_COMPLETED_RESALES");
        if (confirmedLowerTradePrices) reasonCodes.Add("VERIFIED_RESALE_BELOW_CREATOR_REFERENCE");
        if (comparableDivergence) reasonCodes.Add("COMPARABLE_RESALE_DIVERGENCE");
        if (prior.OriginalUnitsRemaining is > 0 && current.OriginalUnitsRemaining is > 0 &&
            current.OriginalUnitsRemaining < prior.OriginalUnitsRemaining)
            reasonCodes.Add("PRICE_RISE_DURING_INVENTORY_DECLINE");

        // A new Limited may have almost no resales solely because original
        // purchasers cannot yet resell. Since the app has no authoritative
        // per-copy release/lock-until feed, avoid HARD exclusions until at least
        // 30 days after the earliest trusted original-price observation.
        if (current.ObservedAtUtc - prior.ObservedAtUtc <
            TimeSpan.FromDays(rules.MinimumResaleEligibilityDays))
        {
            reasonCodes.Add("RESALE_HOLDING_PERIOD_UNMATURED");
            return new Decision(Insufficient, reasonCodes.ToArray(),
                $"Creator price rose {multiple:0.0}×, but the earliest verified original-sale " +
                "observation is too recent to use low resale volume as evidence: " +
                "Roblox may hold originally purchased Limiteds for up to 30 days.",
                prior.PrimaryPrice, multiple);
        }

        if (creatorAnchorBargain && (weakTransactions || confirmedLowerTradePrices || comparableDivergence))
            return new Decision(Excluded, reasonCodes.ToArray(),
                $"Verified creator price rose from {prior.PrimaryPrice:N0} to {current.PrimaryPrice:N0} R$ " +
                $"({multiple:0.0}×). The cheaper resale ask is not an independent discount; " +
                "completed resale/comparable evidence does not support the creator anchor.",
                prior.PrimaryPrice, multiple);

        reasonCodes.Add("INDEPENDENT_CORROBORATION_MISSING");
        return new Decision(Insufficient, reasonCodes.ToArray(),
            $"Creator price rose {multiple:0.0}× from {prior.PrimaryPrice:N0} to " +
            $"{current.PrimaryPrice:N0} R$, but independent evidence is insufficient " +
            "to establish a distorted investment valuation.",
            prior.PrimaryPrice, multiple);
    }

    public static bool IsVerifiedOriginal(PrimaryPriceObservation p) =>
        p.MarketplaceVerified && p.AssetId > 0 && p.CreatorId > 0 && p.AssetType > 0 &&
        p.PrimaryPrice > 0 && p.OriginalUnitsRemaining is > 0 &&
        p.ObservedAtUtc != default &&
        string.Equals(p.Source, "RobloxMarketplacePrimary", StringComparison.Ordinal);

    private static bool HasIndependentDemandSupport(PrimaryPriceObservation current, ResaleEvidence market,
        Configuration rules) =>
        market.CompletedResales30d >= rules.IndependentSupportMinimumResales30d &&
        market.PriceEvidenceDays >= rules.MinimumDailyPriceEvidenceDays &&
        market.DailyAveragePriceMedian30d >= current.PrimaryPrice * rules.IndependentSupportDailyPriceRatio;
}
