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
        long AssetId, int AssetType, int Price, bool MarketplaceVerified, long? OriginalUnitsAvailable,
        bool? IsEmissive = null, bool? IsPbr = null);

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

    // Classify by explicitly supplied marketplace metadata. A missing field is UNKNOWN,
    // never evidence that an item is non-emissive or uses standard materials.
    public const string Standard = "STANDARD";
    public const string Emissive = "EMISSIVE";
    public const string Pbr = "PBR";
    public const string Unknown = "UNKNOWN";

    public readonly record struct MaterialCategory(int AssetType, string MaterialClass);

    public static string MaterialClass(bool? isEmissive, bool? isPbr) =>
        (isEmissive, isPbr) switch
        {
            (true, false) => Emissive,
            (false, true) => Pbr,
            (false, false) => Standard,
            // A missing flag or two premium flags is ambiguous metadata. Keep
            // the item researchable, but never infer a priced material class.
            _ => Unknown
        };

    public static IReadOnlyDictionary<MaterialCategory, int> FindLowestObservedOriginalPricesByMaterial(
        IEnumerable<OriginalOffer> offers) =>
        offers.Where(x => x.AssetId > 0 && x.AssetType > 0 &&
                          x.MarketplaceVerified && x.OriginalUnitsAvailable is > 0 &&
                          UgcHunterEntryPricePolicy.IsEligiblePrimaryPrice(x.Price))
            .GroupBy(x => new MaterialCategory(x.AssetType, MaterialClass(x.IsEmissive, x.IsPbr)))
            .ToDictionary(x => x.Key, x => x.Min(y => y.Price));

    // Research benchmarks are historical observations, NOT official/dynamic floors.
    // Do not feed them to ROI scoring, item verification, or price-distortion gates.
    public static int? October2026TypicalOriginalReference(int assetType, string materialClass) =>
        (assetType, materialClass) switch
        {
            (8, Standard) => 95,
            (41, Standard) => 65,
            (42 or 43 or 44, Standard) => 55,
            (46, Standard) => 140,
            (61, Standard) => 50,
            _ => null
        };

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
