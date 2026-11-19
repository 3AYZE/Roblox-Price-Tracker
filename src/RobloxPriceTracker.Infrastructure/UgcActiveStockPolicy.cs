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
        RobloxMarketplaceItemData? marketplace)
    {
        if (expectedAssetId <= 0 ||
            string.IsNullOrWhiteSpace(expectedCollectibleItemId) ||
            marketplace is null ||
            marketplace.AssetId != expectedAssetId ||
            !string.Equals(marketplace.CollectibleItemId, expectedCollectibleItemId,
                StringComparison.OrdinalIgnoreCase) ||
            !marketplace.IsPrimaryPurchasable)
            return false;

        return marketplace.TotalStock is not { } total ||
            total >= marketplace.UnitsAvailable!.Value;
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
