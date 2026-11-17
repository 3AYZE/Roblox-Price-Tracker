namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Primary-sale comparisons use the creator's current paid original-stock price,
/// never lowestResalePrice or RAP. Roblox's official category floors are not
/// available anonymously from the documented authenticated API, so a scan-wide
/// minimum is always labeled "observed" instead of "official".
/// </summary>
public static class UgcCategoryPrimaryPricePolicy
{
    public readonly record struct OriginalOffer(
        long AssetId, int AssetType, int Price, bool MarketplaceVerified, long? OriginalUnitsAvailable);

    public static IReadOnlyDictionary<int, int> FindLowestObservedOriginalPrices(
        IEnumerable<OriginalOffer> offers) =>
        offers
            .Where(x => x.AssetId > 0 && x.AssetType > 0 &&
                        x.MarketplaceVerified && x.OriginalUnitsAvailable is > 0 &&
                        UgcHunterEntryPricePolicy.IsEligiblePrimaryPrice(x.Price))
            .GroupBy(x => x.AssetType)
            .ToDictionary(x => x.Key, x => x.Min(item => item.Price));

    public static bool IsAtObservedLow(OriginalOffer item, IReadOnlyDictionary<int, int> lowest) =>
        item.MarketplaceVerified && item.OriginalUnitsAvailable is > 0 &&
        lowest.TryGetValue(item.AssetType, out var price) && price == item.Price;

    // Discovery-only visual hints: no item is "confirmed emissive" or "confirmed
    // dance" solely from a title. These never affect a price floor.
    public static bool HasEmissiveNameHint(string? name) =>
        HasAnyToken(name, "emissive", "glowing", "glow", "luminous", "neon", "radiant", "aura");

    public static bool HasDanceNameHint(string? name) =>
        HasAnyToken(name, "dance", "dancing", "groove", "boogie", "shuffle");

    private static bool HasAnyToken(string? name, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var words = name.Split([' ', '-', '_', '[', ']', '(', ')', ':', '/', '.', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Any(word => tokens.Contains(word, StringComparer.OrdinalIgnoreCase));
    }
}
