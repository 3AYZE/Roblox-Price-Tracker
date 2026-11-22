using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcQualifiedOpportunityRequiresIndependentEvidenceAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var good = new UgcQualifiedOpportunityPolicy.Evidence(
            PrimaryMarketVerified: true,
            ObservedAtUtc: now,
            OriginalPrice: 50,
            OriginalUnitsRemaining: 100,
            VerifiedOriginalTotal: 1_000,
            StockObservationCount: 3,
            UnitsDepletedPerMinute: 2,
            HasCurrentResaleVolume: true,
            VerifiedCompletedResales30d: 6,
            VolumeEvidenceDays: 3,
            VerifiedResalePriceMedian30d: 200,
            PriceEvidenceDays: 3,
            ResaleFloor: 180,
            RiskScore: 25,
            IsExcludedPriceDistortion: false);

        AssertTrue(UgcQualifiedOpportunityPolicy.Evaluate(good, now).Qualified);
        AssertEqual("ORIGINAL_STOCK_NOT_SCARCE", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { OriginalUnitsRemaining = 2_900, VerifiedOriginalTotal = 3_000 }, now).Reason);
        AssertEqual("ORIGINAL_STOCK_NOT_SCARCE", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { OriginalUnitsRemaining = 600, VerifiedOriginalTotal = 1_000 }, now).Reason);
        AssertEqual("ORIGINAL_SUPPLY_UNKNOWN_OR_SOLD_OUT", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VerifiedOriginalTotal = null }, now).Reason);
        AssertEqual("ORIGINAL_SUPPLY_UNKNOWN_OR_SOLD_OUT", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { OriginalUnitsRemaining = 0 }, now).Reason);
        AssertEqual("UNVERIFIED_ORIGINAL_LISTING", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { PrimaryMarketVerified = false }, now).Reason);
        AssertEqual("STALE_ORIGINAL_STOCK", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { ObservedAtUtc = now.AddMinutes(-3) }, now).Reason);
        AssertEqual("DEPLETION_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { StockObservationCount = 1 }, now).Reason);
        AssertEqual("DEPLETION_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { UnitsDepletedPerMinute = 0 }, now).Reason);
        AssertEqual("RESALE_LIQUIDITY_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { HasCurrentResaleVolume = false }, now).Reason);
        AssertEqual("RESALE_LIQUIDITY_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VerifiedCompletedResales30d = null }, now).Reason);
        AssertEqual("RESALE_LIQUIDITY_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VerifiedCompletedResales30d = 0 }, now).Reason);
        AssertEqual("RESALE_LIQUIDITY_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VerifiedCompletedResales30d = 2 }, now).Reason);
        AssertEqual("RESALE_LIQUIDITY_UNCONFIRMED", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VolumeEvidenceDays = 1 }, now).Reason);
        AssertEqual("RESALE_PRICE_EVIDENCE_MISSING", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { PriceEvidenceDays = 1 }, now).Reason);
        AssertEqual("RESALE_PRICE_EVIDENCE_MISSING", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { ResaleFloor = null }, now).Reason);
        AssertEqual("NET_PROFIT_MARGIN_INSUFFICIENT", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { ResaleFloor = 100 }, now).Reason);
        AssertEqual("NET_PROFIT_MARGIN_INSUFFICIENT", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { VerifiedResalePriceMedian30d = 100 }, now).Reason);
        AssertEqual("MARKET_RISK_OR_PRICE_DISTORTION", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { RiskScore = 70 }, now).Reason);
        AssertEqual("MARKET_RISK_OR_PRICE_DISTORTION", UgcQualifiedOpportunityPolicy.Evaluate(
            good with { IsExcludedPriceDistortion = true }, now).Reason);
        return Task.CompletedTask;
    }

    static Task TestOfficialResaleMergeKeepsIndependentCoverageAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var legacy = new RobloxResaleMarketData(456, true, "Roblox Economy", 100, 550,
            [new(now.AddDays(-5), 500)], [new(now.AddDays(-5), 4)], null, null);
        var modern = new RobloxResaleMarketData(456, true, "Roblox Marketplace Sales", null, null,
            [], [new(now.AddHours(-3), 0), new(now.AddDays(-2), 8)], "collectible-456", null);
        var merged = RobloxResaleDataService.MergeIndependentCoverage(
            legacy, modern, staleLegacy: false, "collectible-456");
        AssertTrue(merged.IsAvailable && merged.HasSalesCoverage(now));
        AssertEqual(550d, merged.RecentAveragePrice);
        AssertEqual(8d, merged.SalesWithinDays(7, now));
        AssertEqual("collectible-456", merged.CollectibleItemId);

        // A real HTTP 429 still propagates its cooldown, even if legacy RAP is usable.
        var rateLimited = RobloxResaleDataService.MergeIndependentCoverage(
            legacy, modern with
            {
                IsAvailable = false,
                VolumeDataPoints = [],
                RateLimited = true,
                RetryAfterUtc = now.AddMinutes(5)
            }, staleLegacy: false, "collectible-456");
        AssertTrue(rateLimited.RateLimited);
        AssertEqual(550d, rateLimited.RecentAveragePrice);
        AssertTrue(rateLimited.RetryAfterUtc > now);
        AssertTrue(!rateLimited.HasSalesCoverage(now));

        // A stale legacy RAP never silently becomes current when modern data is absent.
        var expired = legacy with
        {
            PriceDataPoints = [new(now.AddDays(-90), 500)],
            VolumeDataPoints = [new(now.AddDays(-90), 4)]
        };
        var emptyModern = modern with { IsAvailable = false, VolumeDataPoints = [], Error = "No data" };
        var unavailable = RobloxResaleDataService.MergeIndependentCoverage(
            expired, emptyModern, staleLegacy: true, "collectible-456");
        AssertTrue(!unavailable.IsAvailable);
        AssertTrue(unavailable.RecentAveragePrice is null);
        return Task.CompletedTask;
    }
}
