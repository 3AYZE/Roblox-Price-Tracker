namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Acceptance policy for ACTIVE creator-stock UGC listings. Catalog data finds
/// candidates only. Reseller inventory, catalog-only counts and unknown stock
/// can never qualify as original inventory.
/// </summary>
public static class UgcActiveStockPolicy
{
    public static readonly TimeSpan MaximumDisplayedAge = TimeSpan.FromSeconds(120);

    public static bool IsMarketplaceVerified(
        long expectedAssetId,
        string? expectedCollectibleItemId,
        RobloxMarketplaceItemData? marketplace) =>
        MarketplaceRejectionReason(expectedAssetId, expectedCollectibleItemId, marketplace) is null;

    // Stable diagnostic categories; discovery logs counts rather than per-item
    // warnings so a failing public API cannot flood the user's application log.
    public static string? MarketplaceRejectionReason(
        long expectedAssetId,
        string? expectedCollectibleItemId,
        RobloxMarketplaceItemData? marketplace)
    {
        if (expectedAssetId <= 0 || string.IsNullOrWhiteSpace(expectedCollectibleItemId))
            return "missing expected identity";
        if (marketplace is null) return "missing Marketplace quote";
        if (marketplace.AssetId != expectedAssetId ||
            !string.Equals(marketplace.CollectibleItemId, expectedCollectibleItemId,
                StringComparison.OrdinalIgnoreCase))
            return "mismatched asset or collectible ID";
        if (!marketplace.IsAvailable) return "Marketplace unavailable or error";
        if (marketplace.Price is not > 0) return "unknown or zero primary price";
        if (marketplace.UnitsAvailable is null) return "unknown original remaining";
        if (marketplace.UnitsAvailable <= 0) return "sold out original inventory";
        if (string.IsNullOrWhiteSpace(marketplace.SaleLocationType)) return "unknown sale location";
        if (!marketplace.SaleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase))
            return "not purchasable in Shop";
        if (marketplace.TotalStock is { } total && total < marketplace.UnitsAvailable)
            return "remaining exceeds original total";
        return null;
    }

    public readonly record struct DepletionObservation(
        long? Remaining, long? Total, DateTimeOffset AtUtc, bool Verified);

    // Measure actual remaining original stock, not catalog purchase counters.
    // A restock, unknown total, stale sample or unverified observation breaks the series.
    public static double DepletionPerMinute(
        IReadOnlyList<DepletionObservation> history, TimeSpan window)
    {
        if (history.Count < 2 || window <= TimeSpan.Zero) return 0;
        var latest = history[^1];
        if (!Valid(latest)) return 0;
        var oldest = latest;
        for (var i = history.Count - 2; i >= 0; i--)
        {
            var point = history[i];
            if (!Valid(point) || point.Total != latest.Total ||
                point.AtUtc >= oldest.AtUtc || latest.AtUtc - point.AtUtc > window ||
                point.Remaining < oldest.Remaining) break;
            oldest = point;
        }
        var elapsed = latest.AtUtc - oldest.AtUtc;
        return elapsed.TotalSeconds < 10 ? 0 :
            (oldest.Remaining!.Value - latest.Remaining!.Value) / elapsed.TotalMinutes;

        static bool Valid(DepletionObservation x) => x.Verified && x.AtUtc != default &&
            x.Total is > 0 && x.Remaining is >= 0 && x.Remaining <= x.Total;
    }

    // A small edition alone is not scarcity. Require at least half the
    // verified original supply to be gone; integer division handles odd totals.
    public static bool HasMinimumSellThrough(bool verified, long? remaining, long? total) =>
        verified && total is > 0 && remaining is > 0 && remaining <= total.Value / 2;

    // Age is not a ranking factor within the first year. Unknown or invalid
    // creation dates cannot establish that an asset is too old.
    public static bool IsWithinScannerAge(DateTimeOffset? createdAtUtc, DateTimeOffset nowUtc) =>
        createdAtUtc is not { } created || created == default || created > nowUtc ||
        created > nowUtc.AddYears(-1);

    public static bool IsActiveSnapshot(
        bool marketplaceVerified,
        long? creatorUnitsLeft,
        int? creatorPrice,
        DateTimeOffset? verifiedScanAtUtc,
        DateTimeOffset nowUtc)
    {
        if (!marketplaceVerified || creatorUnitsLeft is not > 0 ||
            creatorPrice is not > 0 || verifiedScanAtUtc is null)
            return false;
        var age = nowUtc - verifiedScanAtUtc.Value;
        return age >= TimeSpan.Zero && age <= MaximumDisplayedAge;
    }
}
