using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Conservative entry prices for verified creator-owned original stock. These
/// October 2026 references are user-configured buying limits, not Roblox's
/// official current category floors or observed creator listing minimums.
/// Resale asks, RAP and regional display quotes are never inputs.
/// </summary>
public static class UgcCategoryBuyingPricePolicy
{
    public const string Good = "GOOD_BUYING_PRICE";
    public const string Above = "ABOVE_ENTRY_PRICE";
    public const string Insufficient = "INSUFFICIENT_ENTRY_PRICE_EVIDENCE";

    public sealed record SpecialMaterialBenchmark
    {
        public int CeilingRobux { get; init; }
        public string EvidenceSource { get; init; } = "";
        public DateTimeOffset VerifiedAtUtc { get; init; }

        public bool IsUsable(DateTimeOffset observedAtUtc) =>
            CeilingRobux is > 0 and <= 10000 &&
            !string.IsNullOrWhiteSpace(EvidenceSource) &&
            VerifiedAtUtc != default && VerifiedAtUtc <= observedAtUtc;
    }

    public sealed record Configuration
    {
        public string ReferencePeriod { get; init; } = "2026-10";
        public int Hat { get; init; } = 95;
        public int Hair { get; init; } = 65;
        public int Face { get; init; } = 55;
        public int Neck { get; init; } = 55;
        public int Shoulder { get; init; } = 55;
        public int Back { get; init; } = 140;
        public int Emote { get; init; } = 50;

        // Explicit, independently reviewed benchmarks only. Empty by default;
        // a verified material flag alone is not evidence of a price premium.
        // Keys use "assetType:EMISSIVE" or "assetType:PBR".
        public Dictionary<string, SpecialMaterialBenchmark> SpecialMaterialBenchmarks { get; init; } = new();

        public bool IsValid =>
            ReferencePeriod == "2026-10" &&
            new[] { Hat, Hair, Face, Neck, Shoulder, Back, Emote }
                .All(x => x is > 0 and <= 10000) &&
            SpecialMaterialBenchmarks is not null &&
            SpecialMaterialBenchmarks.All(x =>
                x.Value is not null && x.Value.IsUsable(DateTimeOffset.UtcNow) &&
                x.Key.Split(':') is [var type, var material] &&
                int.TryParse(type, out var assetType) && assetType > 0 &&
                material is (UgcCategoryPrimaryPricePolicy.Emissive or UgcCategoryPrimaryPricePolicy.Pbr));
    }

    public sealed record Decision(string Classification, int? CeilingRobux, string Explanation,
        int? EarlierVerifiedPrice = null)
    {
        public bool IsGoodBuyingPrice => Classification == Good;
        public string StatusText => Classification switch
        {
            Good => "GOOD BUYING PRICE",
            Above when EarlierVerifiedPrice is not null => "ABOVE ENTRY PRICE — CREATOR REPRICED",
            Above => "ABOVE ENTRY PRICE",
            _ => "ENTRY PRICE — INSUFFICIENT EVIDENCE"
        };
    }

    public static async Task<Configuration> LoadAsync(string dataDirectory, AppLogger? logger,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(dataDirectory, "ugc-buying-price-ceilings.json");
        if (!File.Exists(path)) return new Configuration();
        try
        {
            var result = await SafeJsonStore.LoadWithRecoveryAsync<Configuration>(
                path, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, logger, cancellationToken)
                .ConfigureAwait(false);
            if (result is { IsValid: true }) return result;
            logger?.Info("UgcCategoryBuyingPricePolicy.LoadAsync: invalid benchmark settings; default October 2026 ceilings used; check config evidence and category limits.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.Info($"UgcCategoryBuyingPricePolicy.LoadAsync: configuration not loaded ({ex.GetType().Name}); default October 2026 ceilings used; check ugc-buying-price-ceilings.json.");
        }
        return new Configuration();
    }

    public static Decision Evaluate(
        UgcPriceIntegrityPolicy.PrimaryPriceObservation current, string materialClass,
        IEnumerable<UgcPriceIntegrityPolicy.PrimaryPriceObservation> history,
        Configuration? configured = null)
    {
        if (!UgcPriceIntegrityPolicy.IsVerifiedOriginal(current))
            return new(Insufficient, null, "Creator original-stock price is not verified by Marketplace.");

        var rules = configured is { IsValid: true } ? configured : new Configuration();
        // Missing material metadata never establishes a standard material or
        // unlocks premium pricing. The standard reference is only a conservative
        // display bound for affordable research candidates with unknown material.
        var standardCeiling = current.AssetType switch
        {
            8 => (int?)rules.Hat,
            41 => rules.Hair,
            42 => rules.Face,
            43 => rules.Neck,
            44 => rules.Shoulder,
            46 => rules.Back,
            61 => rules.Emote,
            _ => null
        };
        int? ceiling;
        if (materialClass is UgcCategoryPrimaryPricePolicy.Standard or UgcCategoryPrimaryPricePolicy.Unknown)
            ceiling = standardCeiling;
        else if (materialClass is UgcCategoryPrimaryPricePolicy.Emissive or UgcCategoryPrimaryPricePolicy.Pbr &&
                 rules.SpecialMaterialBenchmarks.TryGetValue($"{current.AssetType}:{materialClass}", out var benchmark) &&
                 benchmark.IsUsable(current.ObservedAtUtc))
            ceiling = benchmark.CeilingRobux;
        else
            ceiling = null;

        // Evaluate verified historical increases BEFORE the category ceiling.
        // Even a small increase, or one still inside the category ceiling, is
        // not an entry at the previously available price.
        var earlier = history
            .Where(p => UgcPriceIntegrityPolicy.IsVerifiedOriginal(p) &&
                        p.AssetId == current.AssetId && p.CreatorId == current.CreatorId &&
                        p.AssetType == current.AssetType && p.ObservedAtUtc < current.ObservedAtUtc &&
                        p.PrimaryPrice < current.PrimaryPrice)
            .OrderBy(p => p.PrimaryPrice)
            .ThenBy(p => p.ObservedAtUtc)
            .FirstOrDefault();
        if (earlier is not null)
        {
            var effectiveCeiling = ceiling is > 0 ? Math.Min(ceiling.Value, earlier.PrimaryPrice) : earlier.PrimaryPrice;
            var description = $"Above entry price: {current.PrimaryPrice:N0} R$ vs {effectiveCeiling:N0} R$. Earlier Marketplace-verified creator price: {earlier.PrimaryPrice:N0} R$; price increased (intent unknown).";
            return new(Above, effectiveCeiling, description, earlier.PrimaryPrice);
        }

        if (ceiling is not > 0)
            return new(Insufficient, null,
                $"No trusted {materialClass.ToLowerInvariant()} buying benchmark for category {current.AssetType}; research only.");

        if (materialClass == UgcCategoryPrimaryPricePolicy.Unknown && current.PrimaryPrice > ceiling.Value)
            return new(Above, ceiling,
                $"Above conservative category limit: {current.PrimaryPrice:N0} R$ vs {ceiling:N0} R$. Material is unverified.");
        if (materialClass == UgcCategoryPrimaryPricePolicy.Unknown)
            return new(Insufficient, ceiling,
                $"Material unverified. Standard category research limit: {ceiling:N0} R$ ({rules.ReferencePeriod}); not a qualified buying price.");

        if (current.PrimaryPrice <= ceiling.Value)
            return new(Good, ceiling,
                $"Verified creator price {current.PrimaryPrice:N0} R$ is within the {rules.ReferencePeriod} category buying reference {ceiling:N0} R$. Other opportunity checks still apply.");

        return new(Above, ceiling,
            $"Above category buying reference: {current.PrimaryPrice:N0} R$ vs {ceiling:N0} R$ ({rules.ReferencePeriod}).");
    }

    // The normal affordable scanner is not the qualified investment shortlist.
    // Unknown material below a conservative reference can be researched, never
    // recommended for purchase. Repriced and excluded listings never enter the live scanner.
    public static bool IsAffordableListing(string classification, int price, int? ceiling, bool excluded) =>
        !excluded && price > 0 && ceiling is > 0 && price <= ceiling.Value &&
        classification is (Good or Insufficient);

    // Defense in depth: cached or misclassified rows must not bypass the
    // historic price anchor or the standard category reference. Special materials
    // use only their explicitly evidenced buying ceiling.
    public static bool IsLiveScannerListing(string classification, int price, int? ceiling,
        bool excluded, int? earlierVerifiedPrice, int assetType, string materialClass)
    {
        if (!IsAffordableListing(classification, price, ceiling, excluded) ||
            (earlierVerifiedPrice is > 0 && price > earlierVerifiedPrice.Value))
            return false;
        if (materialClass is UgcCategoryPrimaryPricePolicy.Standard or UgcCategoryPrimaryPricePolicy.Unknown)
        {
            var reference = UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(
                assetType, UgcCategoryPrimaryPricePolicy.Standard);
            return reference is > 0 && price <= reference.Value;
        }
        return materialClass is UgcCategoryPrimaryPricePolicy.Emissive or UgcCategoryPrimaryPricePolicy.Pbr &&
            classification == Good;
    }

    public static long AffordableStockOrder(bool affordable, long? remaining) =>
        affordable && remaining is > 0 ? remaining.Value : long.MaxValue;
}
