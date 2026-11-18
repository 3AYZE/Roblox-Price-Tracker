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
    public bool LimitedVerified { get; init; }
    public bool PrimaryMarketVerified { get; init; }
    public int? VerifiedOriginalPrice { get; init; }
    public long? LowestResaleAsk { get; init; }
    public string VerificationStatus => !LimitedVerified
        ? "LIMITED STATUS UNVERIFIED"
        : PrimaryMarketVerified ? "MARKETPLACE-VERIFIED ORIGINAL"
        : "ORIGINAL PRICE UNVERIFIED — RESEARCH ONLY";
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
    private const int MaxHydrate = 18;
    private const int MaxSearchPages = 2;
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
        // Search may omit itemRestrictions; bundle details must corroborate them
        // before an unconfirmed listing can enter the Live Drop Scanner.
        var foundById = new Dictionary<long, RobloxAnimationBundleOffer>();
        Uri? pageUri = SearchEndpoint;
        for (var page = 0; page < MaxSearchPages && pageUri is not null; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            using var searchResponse = await _client.GetAsync(pageUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (searchResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (page == 0) throw new HttpRequestException("Roblox rate-limited animation pack discovery (HTTP 429).");
                _logger.Info("Animation bundle pagination rate limited; keeping first-page results.");
                break;
            }
            searchResponse.EnsureSuccessStatusCode();
            await using var searchStream = await searchResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var searchDocument = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in RobloxAnimationBundleResearchParser.ParseSearch(searchDocument.RootElement))
            {
                if (!foundById.TryGetValue(item.BundleId, out var previous) || !previous.LimitedVerified && item.LimitedVerified)
                    foundById[item.BundleId] = item;
            }

            var cursor = searchDocument.RootElement.TryGetProperty("nextPageCursor", out var cursorNode) &&
                cursorNode.ValueKind == JsonValueKind.String ? cursorNode.GetString() : null;
            pageUri = string.IsNullOrWhiteSpace(cursor) || foundById.Count >= MaxHydrate
                ? null : new Uri(SearchEndpoint + "&Cursor=" + Uri.EscapeDataString(cursor));
        }
        var found = foundById.Values.Take(MaxHydrate).ToArray();
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
                var verified = RobloxAnimationBundleResearchParser.ApplyBundleDetails(candidate, detail.RootElement);
                if (verified is not null)
                    hydrated.Add(verified); // An invalid/off-sale detail must NOT fall back to an active candidate.
            }
            catch (JsonException ex)
            {
                _logger.Info($"Animation bundle {candidate.BundleId} details were not usable: {ex.Message}");
                hydrated.Add(candidate);
            }
        }

        // Do not promote a bundle to a current Limited without affirmative metadata.
        hydrated.RemoveAll(x => !x.LimitedVerified);
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
            if (remaining is 0 || Bool(row, "isOffSale") == true) continue;
            result.Add(new RobloxAnimationBundleOffer(
                id, String(row, "name") ?? $"Animation bundle {id}",
                String(row, "creatorName") ?? "Unknown creator",
                Long(row, "creatorTargetId") ?? 0,
                PositiveInt(row, "price"),
                String(row, "collectibleItemId"),
                remaining)
            {
                LimitedVerified = HasLimitedRestrictions(row)
            });
        }
        return result;
    }

    public static RobloxAnimationBundleOffer? ApplyBundleDetails(
        RobloxAnimationBundleOffer candidate, JsonElement root)
    {
        if (!ValidAnimationBundle(root, out var id) || id != candidate.BundleId)
            return null;
        // Search and details have different sparse JSON shapes. If details explicitly
        // supply restrictions, those MUST confirm Limited; otherwise retain a
        // Limited flag only if search already confirmed it.
        var detailsHaveRestrictions = root.TryGetProperty("itemRestrictions", out var detailRestrictions) &&
            detailRestrictions.ValueKind == JsonValueKind.Array;
        var verifiedLimited = detailsHaveRestrictions
            ? HasLimitedRestrictions(root) : candidate.LimitedVerified;
        if (!verifiedLimited) return null;
        if (Long(root, "unitsAvailableForConsumption") is 0 ||
            Bool(root, "isOffSale") == true)
            return null;
        var status = String(root, "priceStatus");
        if (status is "Free" or "OffSale" or "Off Sale") return null;
        return candidate with
        {
            LimitedVerified = verifiedLimited,
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
        // Some search feeds omit restrictions. Admit only as unverified discovery:
        // bundle details must establish Limited before displaying it.
        if (row.TryGetProperty("itemRestrictions", out var restrictions) &&
            restrictions.ValueKind == JsonValueKind.Array && !HasLimitedRestrictions(row))
            return false;
        return true;
    }

    private static bool HasLimitedRestrictions(JsonElement row)
    {
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
