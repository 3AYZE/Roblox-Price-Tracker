namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Minimum independently observed evidence for a UGC buy shortlist.
/// Modeled scores, catalog search metadata and resale asks cannot qualify an item.
/// Unknown creator supply or resale volume never means zero.
/// </summary>
public static class UgcQualifiedOpportunityPolicy
{
    public sealed record Evidence(
        bool PrimaryMarketVerified,
        DateTimeOffset ObservedAtUtc,
        int OriginalPrice,
        long? OriginalUnitsRemaining,
        long? VerifiedOriginalTotal,
        int StockObservationCount,
        double UnitsDepletedPerMinute,
        bool HasCurrentResaleVolume,
        double? VerifiedCompletedResales30d,
        int VolumeEvidenceDays,
        double? VerifiedResalePriceMedian30d,
        int PriceEvidenceDays,
        long? ResaleFloor,
        double RiskScore,
        bool IsExcludedPriceDistortion);

    public sealed record Decision(bool Qualified, string Reason);

    public static Decision Evaluate(Evidence item, DateTimeOffset nowUtc)
    {
        if (!item.PrimaryMarketVerified || item.OriginalPrice <= 0)
            return new(false, "UNVERIFIED_ORIGINAL_LISTING");
        if (item.ObservedAtUtc == default || item.ObservedAtUtc > nowUtc ||
            nowUtc - item.ObservedAtUtc > TimeSpan.FromMinutes(2))
            return new(false, "STALE_ORIGINAL_STOCK");
        if (item.VerifiedOriginalTotal is not > 0 || item.OriginalUnitsRemaining is not > 0 ||
            item.OriginalUnitsRemaining > item.VerifiedOriginalTotal)
            return new(false, "ORIGINAL_SUPPLY_UNKNOWN_OR_SOLD_OUT");
        if (item.OriginalUnitsRemaining > 300 ||
            item.OriginalUnitsRemaining > item.VerifiedOriginalTotal.Value * 0.25d)
            return new(false, "ORIGINAL_STOCK_NOT_SCARCE");
        if (item.StockObservationCount < 2 ||
            !double.IsFinite(item.UnitsDepletedPerMinute) || item.UnitsDepletedPerMinute <= 0)
            return new(false, "DEPLETION_UNCONFIRMED");
        if (!item.HasCurrentResaleVolume || item.VerifiedCompletedResales30d is not >= 3 ||
            item.VolumeEvidenceDays < 2)
            return new(false, "RESALE_LIQUIDITY_UNCONFIRMED");
        if (item.VerifiedResalePriceMedian30d is not > 0 ||
            item.PriceEvidenceDays < 2 || item.ResaleFloor is not > 0)
            return new(false, "RESALE_PRICE_EVIDENCE_MISSING");
        // Community Limited sellers retain 50% after the platform's resale share.
        // Require a 20% cushion over creator price at BOTH independent median and ask floor.
        var minimumGrossResale = item.OriginalPrice * 1.20d /
            UgcResaleScoring.CommunityLimitedResellerShare;
        if (item.VerifiedResalePriceMedian30d < minimumGrossResale ||
            item.ResaleFloor < minimumGrossResale)
            return new(false, "NET_PROFIT_MARGIN_INSUFFICIENT");
        if (!double.IsFinite(item.RiskScore) || item.RiskScore >= 70 || item.IsExcludedPriceDistortion)
            return new(false, "MARKET_RISK_OR_PRICE_DISTORTION");
        return new(true, "VERIFIED_EVIDENCE");
    }
}
