using System.Net;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Separate, research-only path for Animation bundle IDs. These are not asset IDs:
/// do not submit them to asset resale endpoints or UGC Hunter paper investments.
/// </summary>
public sealed record RobloxAnimationBundleOffer(
    long BundleId,
    string Name,
    string CreatorName,
    long CreatorId,
    int? CatalogQuotedPrice,
    string? CollectibleItemId,
    long? OriginalUnitsAvailable)
{
    public bool PrimaryMarketVerified { get; init; }
    public int? VerifiedOriginalPrice { get; init; }
    public long? LowestResaleAsk { get; init; }
    public string VerificationStatus => PrimaryMarketVerified
        ? "MARKETPLACE-VERIFIED ORIGINAL"
        : "UNVERIFIED — RESEARCH ONLY";
    public string OriginalPriceText => PrimaryMarketVerified && VerifiedOriginalPrice is > 0
        ? $"{VerifiedOriginalPrice:N0} R$"
        : "—";
    public string ResaleAskText => LowestResaleAsk is > 0 ? $"{LowestResaleAsk:N0} R$" : "—";
    public string OriginalStockText => PrimaryMarketVerified && OriginalUnitsAvailable is > 0
        ? $"{OriginalUnitsAvailable:N0}" : "—";
    public string ResearchReferenceText => "300 R$ documented pack floor (Sep 2026)";
    public string RobloxUrl => $"https://www.roblox.com/bundles/{BundleId}";
}

/// <summary>
/// Discovery uses ONE paced animation-bundle catalog page when the user opens research.
/// Search and bundle-detail prices are never promoted to verified creator-stock prices.
/// Only the existing Marketplace Items API can verify primary stock and its price.
/// </summary>
public sealed class RobloxAnimationBundleResearchService
{
    private static readonly Uri SearchEndpoint = new(
        "https://catalog.roblox.com/v1/search/items/details?Category=12&Subcategory=38&salesTypeFilter=2&SortType=3&Limit=30");
    private const int MaxHydrate = 12;
    private readonly HttpClient _client;
    private readonly AppLogger _logger;
    private readonly RobloxMarketplaceItemService _marketplace;

    public RobloxAnimationBundleResearchService(HttpClient client, AppLogger logger)
    {
        _client = client;
        _logger = logger;
        _marketplace = new RobloxMarketplaceItemService(client, logger);
    }

    public async Task<IReadOnlyList<RobloxAnimationBundleOffer>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        using var searchResponse = await _client.GetAsync(SearchEndpoint,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Roblox rate-limited animation pack discovery (HTTP 429).");
        searchResponse.EnsureSuccessStatusCode();
        await using var searchStream = await searchResponse.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var searchDocument = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var found = RobloxAnimationBundleResearchParser.ParseSearch(searchDocument.RootElement)
            .Take(MaxHydrate).ToArray();
        var hydrated = new List<RobloxAnimationBundleOffer>(found.Length);

        foreach (var candidate in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No parallel burst: this research-only operation is explicitly requested.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            using var response = await _client.GetAsync(
                $"https://catalog.roblox.com/v1/bundles/{candidate.BundleId}/details",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.Info("Animation bundle details rate limited; stopping this batch.");
                hydrated.Add(candidate);
                break;
            }
            if (!response.IsSuccessStatusCode)
            {
                hydrated.Add(candidate);
                continue;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var detail = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                hydrated.Add(RobloxAnimationBundleResearchParser.ApplyBundleDetails(
                    candidate, detail.RootElement) ?? candidate);
            }
            catch (JsonException ex)
            {
                _logger.Info($"Animation bundle {candidate.BundleId} details were not usable: {ex.Message}");
                hydrated.Add(candidate);
            }
        }

        var collectibleIds = hydrated.Select(x => x.CollectibleItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyDictionary<string, RobloxMarketplaceItemData> primary = collectibleIds.Length == 0
            ? new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase)
            : await _marketplace.GetManyAsync(collectibleIds, cancellationToken).ConfigureAwait(false);

        return hydrated.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.CollectibleItemId) ||
                !primary.TryGetValue(item.CollectibleItemId, out var market) ||
                market.AssetId != item.BundleId || !market.IsPrimaryPurchasable)
                return item; // Catalog-only quotes never become original-stock prices.

            return item with
            {
                VerifiedOriginalPrice = market.Price,
                OriginalUnitsAvailable = market.UnitsAvailable,
                LowestResaleAsk = market.LowestResalePrice,
                PrimaryMarketVerified = true
            };
        }).ToArray();
    }
}

public static class RobloxAnimationBundleResearchParser
{
    public static IReadOnlyList<RobloxAnimationBundleOffer> ParseSearch(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<RobloxAnimationBundleOffer>();
        var seen = new HashSet<long>();
        foreach (var row in data.EnumerateArray())
        {
            if (!ValidAnimationBundle(row, out var id) || !seen.Add(id))
                continue;
            var remaining = Long(row, "unitsAvailableForConsumption");
            if (remaining is 0) continue;
            result.Add(new RobloxAnimationBundleOffer(
                id, String(row, "name") ?? $"Animation bundle {id}",
                String(row, "creatorName") ?? "Unknown creator",
                Long(row, "creatorTargetId") ?? 0,
                PositiveInt(row, "price"),
                String(row, "collectibleItemId"),
                remaining));
        }
        return result;
    }

    public static RobloxAnimationBundleOffer? ApplyBundleDetails(
        RobloxAnimationBundleOffer candidate, JsonElement root)
    {
        if (!ValidAnimationBundle(root, out var id) || id != candidate.BundleId)
            return null;
        if (Long(root, "unitsAvailableForConsumption") is 0 ||
            Bool(root, "isOffSale") == true)
            return null;
        var status = String(root, "priceStatus");
        if (status is "Free" or "OffSale" or "Off Sale") return null;
        return candidate with
        {
            Name = String(root, "name") ?? candidate.Name,
            CatalogQuotedPrice = PositiveInt(root, "price") ?? candidate.CatalogQuotedPrice,
            CollectibleItemId = String(root, "collectibleItemId") ?? candidate.CollectibleItemId,
            OriginalUnitsAvailable = Long(root, "unitsAvailableForConsumption") ?? candidate.OriginalUnitsAvailable
        };
    }

    private static bool ValidAnimationBundle(JsonElement row, out long id)
    {
        id = Long(row, "id") ?? 0;
        if (id <= 0 || !string.Equals(String(row, "itemType"), "Bundle",
                StringComparison.OrdinalIgnoreCase)) return false;
        if (!row.TryGetProperty("bundleType", out var type)) return false;
        bool animation = type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 2 ||
            type.ValueKind == JsonValueKind.String &&
            (string.Equals(type.GetString(), "Animations", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type.GetString(), "AvatarAnimations", StringComparison.OrdinalIgnoreCase));
        if (!animation || Long(row, "creatorTargetId") is 1) return false;
        if (!row.TryGetProperty("itemRestrictions", out var restrictions) ||
            restrictions.ValueKind != JsonValueKind.Array) return false;
        return restrictions.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String &&
            v.GetString() is "Limited" or "LimitedUnique" or "Collectible");
    }

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var n) ? n : null;
    private static int? PositiveInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var n) && n > 0 ? n : null;
    private static bool? Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? v.GetBoolean() : null;
}
