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
