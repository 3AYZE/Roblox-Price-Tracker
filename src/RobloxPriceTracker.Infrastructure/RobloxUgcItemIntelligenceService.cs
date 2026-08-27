using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public sealed record RobloxCatalogAssetDetail(
    long AssetId,
    string Name,
    string CreatorName,
    long CreatorId,
    string CreatorType,
    int AssetType,
    int? Price,
    long? PurchaseCount,
    long? UnitsAvailable,
    long? TotalQuantity,
    long FavoriteCount,
    string? CollectibleItemId,
    string? SaleLocationType,
    bool IsOffSale,
    bool IsCollectible)
{
    public bool IsShopPurchasable =>
        Price is > 0 &&
        UnitsAvailable is > 0 &&
        !IsOffSale &&
        !string.IsNullOrWhiteSpace(SaleLocationType) &&
        SaleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase);
}

public sealed record UgcDataQuality(
    int Score,
    string Label,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Missing)
{
    public string Summary => $"{Score}% {Label}";
}

public static class UgcDataQualityEvaluator
{
    public static UgcDataQuality Evaluate(
        RobloxCatalogAssetDetail catalog,
        RobloxMarketplaceItemData? marketplace,
        RobloxResaleMarketData? resale,
        RobloxResellerMarketData? resellers,
        long? floor)
    {
        var score = 0;
        var evidence = new List<string>();
        var missing = new List<string>();

        Add(catalog.AssetId > 0, 10, "catalog identity", "catalog identity", ref score, evidence, missing);
        Add(marketplace is { IsAvailable: true }, 20, "primary market verified", "primary market verification", ref score, evidence, missing);
        Add((marketplace?.Price ?? catalog.Price) is > 0, 10, "original price", "original price", ref score, evidence, missing);
        Add((marketplace?.TotalStock ?? catalog.TotalQuantity) is > 0 && (marketplace?.UnitsAvailable ?? catalog.UnitsAvailable) is not null,
            15, "supply + remaining", "complete supply", ref score, evidence, missing);
        Add(floor is > 0, 15, "live resale floor", "live resale floor", ref score, evidence, missing);
        Add(resale?.RecentAveragePrice is > 0, 15, "RAP", "RAP", ref score, evidence, missing);
        Add(resellers is { IsAvailable: true, ObservedListings: > 0 }, 10, "reseller depth", "reseller depth", ref score, evidence, missing);
        Add(resale?.HasSalesSeries == true, 5, "resale volume series", "resale volume series", ref score, evidence, missing);

        var label = score switch
        {
            >= 85 => "VERIFIED",
            >= 70 => "STRONG",
            >= 50 => "PARTIAL",
            _ => "LOW"
        };
        return new UgcDataQuality(score, label, evidence, missing);
    }

    private static void Add(
        bool present,
        int points,
        string evidenceText,
        string missingText,
        ref int score,
        List<string> evidence,
        List<string> missing)
    {
        if (present)
        {
            score += points;
            evidence.Add(evidenceText);
        }
        else
        {
            missing.Add(missingText);
        }
    }
}

public sealed record RobloxCreatorLimitedItem(
    long AssetId,
    string Name,
    int? OriginalPrice,
    long? Sold,
    long? Remaining,
    long? TotalSupply,
    long? CurrentFloor,
    bool MarketplaceVerified)
{
    public double? SellThrough => TotalSupply is > 0 && Sold is { } sold
        ? Math.Clamp(sold / (double)TotalSupply.Value, 0d, 1d)
        : null;

    public double? GrossResaleMultiple => OriginalPrice is > 0 && CurrentFloor is > 0
        ? CurrentFloor.Value / (double)OriginalPrice.Value
        : null;

    public bool? ProfitableAfterFee => OriginalPrice is > 0 && CurrentFloor is > 0
        ? CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare > OriginalPrice.Value
        : null;
}

public sealed record RobloxCreatorIntelligence(
    long CreatorId,
    string CreatorName,
    string CreatorType,
    int SampleSize,
    int VerifiedCount,
    int ActiveCount,
    int SoldOutCount,
    int FloorSampleCount,
    int ProfitableAtFloorCount,
    double? AverageSellThrough,
    double? ProfitableShare,
    double? MedianGrossResaleMultiple,
    string TrackRecord,
    IReadOnlyList<RobloxCreatorLimitedItem> Items,
    string? Error);

public static class RobloxCreatorIntelligenceCalculator
{
    public static RobloxCreatorIntelligence Build(
        long creatorId,
        string creatorName,
        string creatorType,
        IReadOnlyList<RobloxCreatorLimitedItem> items,
        string? error = null)
    {
        var verified = items.Count(x => x.MarketplaceVerified);
        var active = items.Count(x => x.Remaining is > 0);
        var soldOut = items.Count(x => x.TotalSupply is > 0 && x.Remaining == 0);
        var floorItems = items.Where(x => x.ProfitableAfterFee is not null).ToArray();
        var profitable = floorItems.Count(x => x.ProfitableAfterFee == true);
        var profitableShare = floorItems.Length > 0 ? profitable / (double)floorItems.Length : (double?)null;
        var sellThrough = items.Where(x => x.SellThrough is not null).Select(x => x.SellThrough!.Value).ToArray();
        var multiples = items.Where(x => x.GrossResaleMultiple is not null).Select(x => x.GrossResaleMultiple!.Value).OrderBy(x => x).ToArray();

        var trackRecord = floorItems.Length < 3
            ? "INSUFFICIENT"
            : profitableShare switch
            {
                >= 0.60d => "STRONG",
                >= 0.35d => "MIXED",
                _ => "WEAK"
            };

        return new RobloxCreatorIntelligence(
            creatorId,
            creatorName,
            creatorType,
            items.Count,
            verified,
            active,
            soldOut,
            floorItems.Length,
            profitable,
            sellThrough.Length > 0 ? sellThrough.Average() : null,
            profitableShare,
            Median(multiples),
            trackRecord,
            items,
            error);
    }

    private static double? Median(double[] values)
    {
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2d;
    }
}

public sealed record RobloxUgcItemIntelligence(
    RobloxCatalogAssetDetail Catalog,
    RobloxMarketplaceItemData? Marketplace,
    RobloxResaleMarketData? Resale,
    RobloxResellerMarketData? Resellers,
    UgcResaleScore? Score,
    UgcDataQuality Quality,
    RobloxCreatorIntelligence? Creator,
    DateTimeOffset ObservedAtUtc)
{
    public int? OriginalPrice => Marketplace?.Price ?? Catalog.Price;
    public long? Sold => Marketplace?.PrimarySales ?? Catalog.PurchaseCount;
    public long? Remaining => Marketplace?.UnitsAvailable ?? Catalog.UnitsAvailable;
    public long? TotalSupply => Marketplace?.TotalStock ?? Catalog.TotalQuantity;
    public long? CurrentFloor => Resellers?.LowestPrice ?? Marketplace?.LowestResalePrice;
    public double? Rap => Resale?.RecentAveragePrice;
    public int SellerCount => Resellers?.ObservedListings ?? 0;
    public double Sales7d => Resale?.SalesLast7d ?? 0d;
    public double Sales30d => SalesWithinDays(Resale, 30.25d);
    public double? CurrentFloorNetRoi => OriginalPrice is > 0 && CurrentFloor is > 0
        ? CurrentFloor.Value * UgcResaleScoring.CommunityLimitedResellerShare / OriginalPrice.Value - 1d
        : null;
    public string Recommendation => Quality.Score < 45
        ? "INSUFFICIENT DATA"
        : Score?.Recommendation ?? "NOT ANALYZABLE";

    private static double SalesWithinDays(RobloxResaleMarketData? resale, double days)
    {
        if (resale is null) return 0d;
        var points = resale.VolumeDataPoints.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
        if (points.Length == 0) return 0d;
        var newest = points[0].Date;
        return points.Where(x => x.Date >= newest - TimeSpan.FromDays(days)).Sum(x => x.Value);
    }
}

/// <summary>
/// On-demand item analyzer. It keeps Hunter scanning lightweight by doing deeper Roblox market
/// and creator-history calls only when the user explicitly analyzes an item.
/// </summary>
public sealed class RobloxUgcItemIntelligenceService
{
    private static readonly Uri CatalogDetailsEndpoint = new("https://catalog.roblox.com/v1/catalog/items/details");
    private const int MaxAttempts = 2;
    private const int MaxCreatorSample = 24;

    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly RobloxMarketplaceItemService _marketplaceItems;
    private readonly RobloxResaleDataService _resaleData;
    private readonly RobloxResellerDataService _resellers;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private string? _anonymousCsrfToken;

    public RobloxUgcItemIntelligenceService(HttpClient httpClient, AppLogger logger, RobloxResaleDataService resaleData)
    {
        _httpClient = httpClient;
        _logger = logger;
        _resaleData = resaleData;
        _marketplaceItems = new RobloxMarketplaceItemService(httpClient, logger);
        _resellers = new RobloxResellerDataService(httpClient, logger);
    }

    public async Task<RobloxUgcItemIntelligence> AnalyzeAsync(long assetId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0) throw new ArgumentOutOfRangeException(nameof(assetId));

        var catalog = await GetAssetDetailAsync(assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roblox did not return catalog details for this asset.");

        RobloxMarketplaceItemData? marketplace = null;
        var collectibleId = catalog.CollectibleItemId;
        if (!string.IsNullOrWhiteSpace(collectibleId))
        {
            var map = await _marketplaceItems.GetManyAsync(new[] { collectibleId }, cancellationToken).ConfigureAwait(false);
            if (map.TryGetValue(collectibleId, out var item) && item.IsAvailable) marketplace = item;
        }

        RobloxResaleMarketData resale = !string.IsNullOrWhiteSpace(collectibleId)
            ? await _resaleData.GetModernAsync(assetId, collectibleId, cancellationToken).ConfigureAwait(false)
            : await _resaleData.GetAsync(assetId, cancellationToken).ConfigureAwait(false);

        collectibleId ??= resale.CollectibleItemId;
        if (marketplace is null && !string.IsNullOrWhiteSpace(collectibleId))
        {
            var map = await _marketplaceItems.GetManyAsync(new[] { collectibleId }, cancellationToken).ConfigureAwait(false);
            if (map.TryGetValue(collectibleId, out var item) && item.IsAvailable) marketplace = item;
        }

        RobloxResellerMarketData? resellerBook = null;
        if (!string.IsNullOrWhiteSpace(collectibleId))
            resellerBook = await _resellers.GetAsync(assetId, collectibleId, cancellationToken).ConfigureAwait(false);

        var originalPrice = marketplace?.Price ?? catalog.Price;
        var sold = marketplace?.PrimarySales ?? catalog.PurchaseCount ?? 0;
        var remaining = marketplace?.UnitsAvailable ?? catalog.UnitsAvailable;
        var total = marketplace?.TotalStock ?? catalog.TotalQuantity;
        var floor = resellerBook?.LowestPrice ?? marketplace?.LowestResalePrice;
        var sales30 = SalesWithinDays(resale, 30.25d);
        var hasMarket = floor is > 0 || resale.IsAvailable || resellerBook is { IsAvailable: true };

        UgcResaleScore? score = null;
        if (originalPrice is > 0)
        {
            score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
                originalPrice.Value,
                total,
                sold,
                remaining,
                0d,
                0d,
                catalog.FavoriteCount,
                0,
                resale.RecentAveragePrice,
                floor,
                resellerBook?.ObservedListings ?? 0,
                resellerBook?.HasMoreListings ?? false,
                resellerBook?.ListingsWithin10Pct ?? 0,
                resellerBook?.ListingsWithin20Pct ?? 0,
                resale.SalesLast7d,
                sales30,
                resellerBook?.MedianTop10,
                hasMarket));
        }

        var quality = UgcDataQualityEvaluator.Evaluate(catalog, marketplace, resale, resellerBook, floor);
        RobloxCreatorIntelligence? creator = null;
        if (catalog.CreatorId > 1)
        {
            try
            {
                creator = await GetCreatorIntelligenceAsync(catalog, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Creator intelligence failed for {catalog.CreatorId}: {ex.Message}");
                creator = RobloxCreatorIntelligenceCalculator.Build(
                    catalog.CreatorId,
                    catalog.CreatorName,
                    catalog.CreatorType,
                    Array.Empty<RobloxCreatorLimitedItem>(),
                    ex.Message);
            }
        }

        return new RobloxUgcItemIntelligence(catalog, marketplace, resale, resellerBook, score, quality, creator, DateTimeOffset.UtcNow);
    }

    private async Task<RobloxCreatorIntelligence> GetCreatorIntelligenceAsync(RobloxCatalogAssetDetail source, CancellationToken cancellationToken)
    {
        var creatorType = source.CreatorType.Equals("Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "User";
        var uri = new Uri(
            $"https://catalog.roblox.com/v1/search/items/details?Category=1&CreatorTargetId={source.CreatorId}&CreatorType={creatorType}&salesTypeFilter=2&SortType=3&Limit=30");
        using var search = await SendGetWithRetryAsync(uri, "creator Limited search", cancellationToken).ConfigureAwait(false);
        await using var searchStream = await search.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var searchDocument = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var ids = RobloxCatalogAssetDetailParser.ParseLimitedSearchIds(searchDocument.RootElement, source.CreatorId)
            .Distinct()
            .Take(MaxCreatorSample)
            .ToArray();

        if (ids.Length == 0)
            return RobloxCreatorIntelligenceCalculator.Build(source.CreatorId, source.CreatorName, creatorType, Array.Empty<RobloxCreatorLimitedItem>());

        using var detailDocument = await FetchCatalogDetailsAsync(ids, cancellationToken).ConfigureAwait(false);
        var details = RobloxCatalogAssetDetailParser.ParseMany(detailDocument.RootElement)
            .Where(x => x.CreatorId == source.CreatorId && x.IsCollectible)
            .ToDictionary(x => x.AssetId);

        var collectibleIds = details.Values
            .Select(x => x.CollectibleItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var market = collectibleIds.Length == 0
            ? new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase)
            : await _marketplaceItems.GetManyAsync(collectibleIds, cancellationToken).ConfigureAwait(false);

        var items = new List<RobloxCreatorLimitedItem>();
        foreach (var id in ids)
        {
            if (!details.TryGetValue(id, out var detail)) continue;
            RobloxMarketplaceItemData? verified = null;
            if (!string.IsNullOrWhiteSpace(detail.CollectibleItemId) && market.TryGetValue(detail.CollectibleItemId, out var marketItem) && marketItem.IsAvailable)
                verified = marketItem;

            items.Add(new RobloxCreatorLimitedItem(
                detail.AssetId,
                detail.Name,
                verified?.Price ?? detail.Price,
                verified?.PrimarySales ?? detail.PurchaseCount,
                verified?.UnitsAvailable ?? detail.UnitsAvailable,
                verified?.TotalStock ?? detail.TotalQuantity,
                verified?.LowestResalePrice,
                verified is not null));
        }

        return RobloxCreatorIntelligenceCalculator.Build(source.CreatorId, source.CreatorName, creatorType, items);
    }

    private async Task<RobloxCatalogAssetDetail?> GetAssetDetailAsync(long assetId, CancellationToken cancellationToken)
    {
        using var document = await FetchCatalogDetailsAsync(new[] { assetId }, cancellationToken).ConfigureAwait(false);
        return RobloxCatalogAssetDetailParser.ParseMany(document.RootElement).FirstOrDefault(x => x.AssetId == assetId);
    }

    private async Task<JsonDocument> FetchCatalogDetailsAsync(long[] ids, CancellationToken cancellationToken)
    {
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var response = await SendCatalogDetailsWithCsrfAsync(ids, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
                    response.Dispose();
                    if (attempt + 1 < MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retry.TotalMilliseconds, 1000d, 8000d)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpRequestException("Roblox catalog details returned HTTP 429.", null, HttpStatusCode.TooManyRequests);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Roblox catalog details returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("Catalog detail retry loop ended unexpectedly.");
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendCatalogDetailsWithCsrfAsync(long[] ids, CancellationToken cancellationToken)
    {
        var response = await SendCatalogDetailsAsync(ids, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var token))
        {
            response.Dispose();
            _anonymousCsrfToken = token;
            response = await SendCatalogDetailsAsync(ids, token, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendCatalogDetailsAsync(long[] ids, string? csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CatalogDetailsEndpoint)
        {
            Content = JsonContent.Create(new { items = ids.Select(id => new { itemType = "Asset", id }).ToArray() })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(csrfToken)) request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGetWithRetryAsync(Uri uri, string operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
                response.Dispose();
                if (attempt + 1 < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retry.TotalMilliseconds, 1000d, 8000d)), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException($"Roblox {operation} returned HTTP 429.", null, HttpStatusCode.TooManyRequests);
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Roblox {operation} returned HTTP {(int)status}.", null, status);
            }
            return response;
        }
        throw new InvalidOperationException($"{operation} retry loop ended unexpectedly.");
    }

    private static double SalesWithinDays(RobloxResaleMarketData resale, double days)
    {
        var points = resale.VolumeDataPoints.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
        if (points.Length == 0) return 0d;
        var newest = points[0].Date;
        return points.Where(x => x.Date >= newest - TimeSpan.FromDays(days)).Sum(x => x.Value);
    }

    private static bool TryGetCsrfToken(HttpResponseMessage response, out string token)
    {
        token = string.Empty;
        if (!response.Headers.TryGetValues("x-csrf-token", out var values)) return false;
        token = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        return token.Length > 0;
    }
}

public static class RobloxCatalogAssetDetailParser
{
    public static IReadOnlyList<RobloxCatalogAssetDetail> ParseMany(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<RobloxCatalogAssetDetail>();

        var result = new List<RobloxCatalogAssetDetail>();
        foreach (var row in data.EnumerateArray())
        {
            if (!TryGetInt64(row, "id", out var id) || id <= 0) continue;
            var itemType = GetString(row, "itemType");
            if (!string.IsNullOrWhiteSpace(itemType) && !itemType.Equals("Asset", StringComparison.OrdinalIgnoreCase)) continue;

            var restrictions = GetStringArray(row, "itemRestrictions");
            var collectible = restrictions.Any(x =>
                x.Equals("Collectible", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("Limited", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("LimitedUnique", StringComparison.OrdinalIgnoreCase));

            result.Add(new RobloxCatalogAssetDetail(
                id,
                GetString(row, "name") ?? $"Asset {id}",
                GetString(row, "creatorName") ?? "Unknown creator",
                TryGetInt64(row, "creatorTargetId", out var creator) ? creator : 0,
                NormalizeCreatorType(row),
                TryGetInt32(row, "assetType", out var assetType) ? assetType : 0,
                TryGetInt32(row, "price", out var price) ? price : null,
                TryGetInt64(row, "purchaseCount", out var purchases) ? Math.Max(0, purchases) : null,
                TryGetInt64(row, "unitsAvailableForConsumption", out var units) ? Math.Max(0, units) : null,
                TryGetInt64(row, "totalQuantity", out var total) && total > 0 ? total : null,
                TryGetInt64(row, "favoriteCount", out var favorites) ? Math.Max(0, favorites) : 0,
                GetString(row, "collectibleItemId"),
                GetString(row, "saleLocationType"),
                TryGetBoolean(row, "isOffSale", out var offSale) && offSale,
                collectible));
        }
        return result;
    }

    public static IReadOnlyList<long> ParseLimitedSearchIds(JsonElement root, long creatorId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<long>();

        var ids = new List<long>();
        foreach (var row in data.EnumerateArray())
        {
            if (!TryGetInt64(row, "id", out var id) || id <= 0) continue;
            if (TryGetInt64(row, "creatorTargetId", out var rowCreator) && creatorId > 0 && rowCreator != creatorId) continue;
            var restrictions = GetStringArray(row, "itemRestrictions");
            if (!restrictions.Any(x => x.Equals("Collectible", StringComparison.OrdinalIgnoreCase) || x.Equals("Limited", StringComparison.OrdinalIgnoreCase) || x.Equals("LimitedUnique", StringComparison.OrdinalIgnoreCase)))
                continue;
            ids.Add(id);
        }
        return ids;
    }

    private static string NormalizeCreatorType(JsonElement row)
    {
        var text = GetString(row, "creatorType");
        if (!string.IsNullOrWhiteSpace(text))
            return text.Equals("Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "User";
        if (TryGetInt32(row, "creatorType", out var numeric) && numeric == 2) return "Group";
        return "User";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var token) && token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var token) && token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out value);
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var token)) return false;
        if (token.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (token.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }
}
