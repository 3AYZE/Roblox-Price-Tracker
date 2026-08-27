using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public enum RobloxUgcDiscoveryFailureKind
{
    RateLimited,
    Offline,
    Protocol
}

public sealed class RobloxUgcDiscoveryException : Exception
{
    public RobloxUgcDiscoveryException(
        RobloxUgcDiscoveryFailureKind kind,
        string message,
        int? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public RobloxUgcDiscoveryFailureKind Kind { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

public sealed record RobloxUgcCatalogCandidate(
    long AssetId,
    string Name,
    string CreatorName,
    long CreatorId,
    int AssetType,
    string Category,
    int Price,
    long PurchaseCount,
    long? UnitsAvailable,
    long? TotalQuantity,
    long FavoriteCount,
    string? CollectibleItemId = null,
    long? LowestResalePrice = null,
    bool HasResellers = false,
    bool PrimaryMarketVerified = false);

public sealed record RobloxUgcDiscoveryResult(
    IReadOnlyList<RobloxUgcCatalogCandidate> Items,
    int DiscoveredCount,
    int HydratedCount);

/// <summary>
/// Finds current UGC Limiteds in two stages. Roblox's search endpoint is treated as discovery-only:
/// it currently returns Collectible rows with price=0/purchaseCount=null for many assets. The IDs are
/// therefore hydrated through the catalog batch-details endpoint before buyability, supply, and price
/// filters are applied. This also keeps Hunter to one normal search request plus one bounded detail batch.
/// </summary>
public sealed class RobloxUgcDiscoveryService
{
    private static readonly Uri DiscoveryEndpoint = new(
        "https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=1&Limit=30");
    private static readonly Uri DetailsEndpoint = new("https://catalog.roblox.com/v1/catalog/items/details");

    private const int MaxDiscoveryIds = 40;
    private const int MaxRateLimitAttempts = 2;

    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly RobloxMarketplaceItemService _marketplaceItems;
    private string? _anonymousCsrfToken;

    public RobloxUgcDiscoveryService(HttpClient httpClient, AppLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _marketplaceItems = new RobloxMarketplaceItemService(httpClient, logger);
    }

    public async Task<RobloxUgcDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var searchDocument = await FetchDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        var ids = RobloxUgcCatalogDiscoveryParser.ParseDiscoveryIds(searchDocument.RootElement)
            .Distinct()
            .Take(MaxDiscoveryIds)
            .ToArray();

        if (ids.Length == 0)
        {
            _logger.Info("UGC Hunter discovery returned no UGC Collectible asset IDs.");
            return new RobloxUgcDiscoveryResult(Array.Empty<RobloxUgcCatalogCandidate>(), 0, 0);
        }

        // Avoid immediately issuing another catalog request in the same burst. Roblox's public
        // catalog edge currently rate-limits rapid sequential search calls aggressively.
        await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken).ConfigureAwait(false);

        using var detailDocument = await FetchDetailsAsync(ids, cancellationToken).ConfigureAwait(false);
        var hydratedRows = detailDocument.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;
        var catalogCandidates = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(detailDocument.RootElement);

        var collectibleIds = catalogCandidates
            .Select(x => x.CollectibleItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var marketplace = collectibleIds.Length == 0
            ? new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase)
            : await _marketplaceItems.GetManyAsync(collectibleIds, cancellationToken).ConfigureAwait(false);

        var candidates = new List<RobloxUgcCatalogCandidate>(catalogCandidates.Count);
        var verified = 0;
        var rejectedUnavailable = 0;
        foreach (var candidate in catalogCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CollectibleItemId) &&
                marketplace.TryGetValue(candidate.CollectibleItemId, out var authoritative) &&
                authoritative.IsAvailable)
            {
                verified++;
                if (!authoritative.IsPrimaryPurchasable)
                {
                    rejectedUnavailable++;
                    continue;
                }

                candidates.Add(candidate with
                {
                    Price = authoritative.Price ?? candidate.Price,
                    PurchaseCount = authoritative.PrimarySales ?? candidate.PurchaseCount,
                    UnitsAvailable = authoritative.UnitsAvailable ?? candidate.UnitsAvailable,
                    TotalQuantity = authoritative.TotalStock ?? candidate.TotalQuantity,
                    LowestResalePrice = authoritative.LowestResalePrice,
                    HasResellers = authoritative.HasResellers,
                    PrimaryMarketVerified = true,
                    CollectibleItemId = authoritative.CollectibleItemId
                });
                continue;
            }

            // Never surface a sold-out row just because Marketplace Items is temporarily unavailable.
            // Catalog fallback is allowed only when catalog details still report purchasable stock.
            if (candidate.UnitsAvailable is > 0)
                candidates.Add(candidate);
            else
                rejectedUnavailable++;
        }

        _logger.Info($"UGC Hunter authoritative discovery: {ids.Length} IDs, {hydratedRows} catalog rows, {verified} marketplace-verified, {rejectedUnavailable} unavailable rejected, {candidates.Count} active candidates.");
        return new RobloxUgcDiscoveryResult(candidates, ids.Length, hydratedRows);
    }

    private async Task<JsonDocument> FetchDiscoveryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRateLimitAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, DiscoveryEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Offline, "Roblox UGC catalog search timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Offline, $"Roblox UGC catalog search failed: {ex.Message}", innerException: ex);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = GetRetryAfter(response) ?? TimeSpan.FromSeconds(3);
                response.Dispose();
                if (attempt + 1 < MaxRateLimitAttempts)
                {
                    var delay = ClampRetryDelay(retryAfter);
                    _logger.Info($"UGC Hunter discovery rate limited; retrying after {delay.TotalSeconds:0.#}s.");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new RobloxUgcDiscoveryException(
                    RobloxUgcDiscoveryFailureKind.RateLimited,
                    "Roblox catalog is rate limiting UGC Hunter. Wait a few seconds and refresh again.",
                    429,
                    retryAfter);
            }

            using (response)
            {
                await EnsureSuccessAsync(response, "UGC catalog search", cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Protocol, $"Roblox UGC search returned malformed JSON: {ex.Message}", innerException: ex);
                }
            }
        }

        throw new InvalidOperationException("UGC discovery retry loop ended unexpectedly.");
    }

    private async Task<JsonDocument> FetchDetailsAsync(long[] ids, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRateLimitAttempts; attempt++)
        {
            var response = await SendDetailsWithCsrfHandshakeAsync(ids, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = GetRetryAfter(response) ?? TimeSpan.FromSeconds(3);
                response.Dispose();
                if (attempt + 1 < MaxRateLimitAttempts)
                {
                    var delay = ClampRetryDelay(retryAfter);
                    _logger.Info($"UGC Hunter detail hydration rate limited; retrying after {delay.TotalSeconds:0.#}s.");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new RobloxUgcDiscoveryException(
                    RobloxUgcDiscoveryFailureKind.RateLimited,
                    "Roblox catalog is rate limiting UGC item details. Wait a few seconds and refresh again.",
                    429,
                    retryAfter);
            }

            using (response)
            {
                await EnsureSuccessAsync(response, "UGC item detail hydration", cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Protocol, $"Roblox UGC item details returned malformed JSON: {ex.Message}", innerException: ex);
                }
            }
        }

        throw new InvalidOperationException("UGC detail retry loop ended unexpectedly.");
    }

    private async Task<HttpResponseMessage> SendDetailsWithCsrfHandshakeAsync(long[] ids, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendDetailsRequestAsync(ids, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var challengedToken))
            {
                response.Dispose();
                _anonymousCsrfToken = challengedToken;
                response = await SendDetailsRequestAsync(ids, challengedToken, cancellationToken).ConfigureAwait(false);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Offline, "Roblox UGC item detail request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Offline, $"Roblox UGC item detail request failed: {ex.Message}", innerException: ex);
        }
    }

    private async Task<HttpResponseMessage> SendDetailsRequestAsync(long[] ids, string? csrfToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            items = ids.Select(id => new { itemType = "Asset", id }).ToArray()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, DetailsEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(csrfToken))
            request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = await ReadResponseDetailAsync(response, cancellationToken).ConfigureAwait(false);
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" · {detail}";
        var status = (int)response.StatusCode;
        var kind = status >= 500 ? RobloxUgcDiscoveryFailureKind.Offline : RobloxUgcDiscoveryFailureKind.Protocol;
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new RobloxUgcDiscoveryException(
                kind,
                $"Roblox rejected anonymous {operation} with HTTP 403 after the CSRF handshake{suffix}.",
                status);
        }

        throw new RobloxUgcDiscoveryException(
            kind,
            $"Roblox {operation} returned HTTP {status} ({response.StatusCode}){suffix}.",
            status);
    }

    private static bool TryGetCsrfToken(HttpResponseMessage response, out string token)
    {
        token = string.Empty;
        if (!response.Headers.TryGetValues("x-csrf-token", out var values)) return false;
        token = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        return token.Length > 0;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) return remaining;
        }
        return null;
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 1000d, 10000d));

    private static async Task<string?> ReadResponseDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (text.Length == 0) return null;
            return text.Length <= 180 ? text : text[..180] + "…";
        }
        catch
        {
            return null;
        }
    }
}

public static class RobloxUgcCatalogDiscoveryParser
{
    public static IReadOnlyList<long> ParseDiscoveryIds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<long>();

        var result = new List<long>();
        foreach (var row in data.EnumerateArray())
        {
            if (!TryGetInt64(row, "id", out var id) || id <= 0) continue;
            var itemType = GetString(row, "itemType");
            if (!string.IsNullOrWhiteSpace(itemType) && !itemType.Equals("Asset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasLimitedRestriction(row)) continue;
            var creatorId = TryGetInt64(row, "creatorTargetId", out var creator) ? creator : 0;
            if (creatorId == 1) continue;
            var assetType = TryGetInt32(row, "assetType", out var parsedAssetType) ? parsedAssetType : 0;
            if (!IsSupportedUgcAssetType(assetType)) continue;

            // Intentionally do NOT inspect price/purchaseCount/remaining supply here. Roblox's
            // current Limited search response can report price=0 and null aggregate sales data.
            result.Add(id);
        }
        return result;
    }

    public static IReadOnlyList<RobloxUgcCatalogCandidate> ParseHydratedCandidates(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<RobloxUgcCatalogCandidate>();

        var candidates = new List<RobloxUgcCatalogCandidate>();
        foreach (var row in data.EnumerateArray())
        {
            if (!TryParseHydratedCandidate(row, out var candidate)) continue;
            candidates.Add(candidate);
        }
        return candidates;
    }

    private static bool TryParseHydratedCandidate(JsonElement row, out RobloxUgcCatalogCandidate candidate)
    {
        candidate = default!;
        if (!TryGetInt64(row, "id", out var id) || id <= 0) return false;
        if (!TryGetInt32(row, "price", out var price) || price <= 0) return false;

        var itemType = GetString(row, "itemType");
        if (!string.IsNullOrWhiteSpace(itemType) && !itemType.Equals("Asset", StringComparison.OrdinalIgnoreCase)) return false;
        if (!HasLimitedRestriction(row)) return false;

        var creatorId = TryGetInt64(row, "creatorTargetId", out var creator) ? creator : 0;
        if (creatorId == 1) return false;

        var assetType = TryGetInt32(row, "assetType", out var parsedAssetType) ? parsedAssetType : 0;
        if (!IsSupportedUgcAssetType(assetType)) return false;
        if (TryGetBoolean(row, "isOffSale", out var isOffSale) && isOffSale) return false;

        var priceStatus = GetString(row, "priceStatus");
        if (!string.IsNullOrWhiteSpace(priceStatus) &&
            (priceStatus.Equals("OffSale", StringComparison.OrdinalIgnoreCase) ||
             priceStatus.Equals("Off Sale", StringComparison.OrdinalIgnoreCase) ||
             priceStatus.Equals("Free", StringComparison.OrdinalIgnoreCase))) return false;

        var saleLocationType = GetString(row, "saleLocationType");
        if (string.IsNullOrWhiteSpace(saleLocationType) || !saleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase)) return false;

        long? unitsAvailable = TryGetInt64(row, "unitsAvailableForConsumption", out var units) ? Math.Max(0, units) : null;
        long? totalQuantity = TryGetInt64(row, "totalQuantity", out var total) && total > 0 ? total : null;
        var purchaseCount = TryGetInt64(row, "purchaseCount", out var purchases) ? Math.Max(0, purchases) : 0;
        if (totalQuantity is { } knownTotal && unitsAvailable is { } remaining)
            purchaseCount = Math.Max(purchaseCount, Math.Max(0, knownTotal - remaining));

        var favorites = TryGetInt64(row, "favoriteCount", out var favs) ? Math.Max(0, favs) : 0;
        candidate = new RobloxUgcCatalogCandidate(
            id,
            GetString(row, "name") ?? $"Asset {id}",
            GetString(row, "creatorName") ?? "Unknown creator",
            creatorId,
            assetType,
            CategoryName(assetType),
            price,
            purchaseCount,
            unitsAvailable,
            totalQuantity,
            favorites,
            GetString(row, "collectibleItemId"));
        return true;
    }

    public static bool IsSupportedUgcAssetType(int assetType) =>
        assetType == 8 ||
        assetType is >= 41 and <= 47 ||
        assetType is >= 64 and <= 72 ||
        assetType is 76 or 77 or 79 ||
        assetType is >= 88 and <= 90;

    public static string CategoryName(int assetType) => assetType switch
    {
        8 => "Hat",
        41 => "Hair",
        42 => "Face",
        43 => "Neck",
        44 => "Shoulder",
        45 => "Front",
        46 => "Back",
        47 => "Waist",
        64 => "T-Shirt",
        65 => "Shirt",
        66 => "Pants",
        67 => "Jacket",
        68 => "Sweater",
        69 => "Shorts",
        70 or 71 => "Shoes",
        72 => "Dress / Skirt",
        76 => "Eyebrow",
        77 => "Eyelash",
        79 => "Dynamic Head",
        88 => "Face Makeup",
        89 => "Lip Makeup",
        90 => "Eye Makeup",
        _ => "Accessory"
    };

    private static bool HasLimitedRestriction(JsonElement row) =>
        GetStringArray(row, "itemRestrictions").Any(x =>
            x.Equals("Limited", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("LimitedUnique", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("Collectible", StringComparison.OrdinalIgnoreCase));

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
