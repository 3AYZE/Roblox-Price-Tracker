using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public sealed record RobloxMarketplaceItemData(
    long AssetId,
    string CollectibleItemId,
    bool IsAvailable,
    int? Price,
    long? PrimarySales,
    long? UnitsAvailable,
    long? TotalStock,
    long? LowestResalePrice,
    bool HasResellers,
    string? SaleLocationType,
    int? ProductSaleStatus,
    string? CollectibleProductId,
    string? Error)
{
    public bool IsPrimaryPurchasable =>
        IsAvailable &&
        Price is > 0 &&
        UnitsAvailable is > 0 &&
        !string.IsNullOrWhiteSpace(SaleLocationType) &&
        SaleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads Roblox's current collectible marketplace item state. This endpoint exposes the
/// authoritative primary-sale counters/stock and lowest resale listing for collectible-system
/// items. Hunter uses it after catalog ID discovery so placeholder catalog fields do not become
/// user-facing market data.
/// </summary>
public sealed class RobloxMarketplaceItemService
{
    private static readonly Uri Endpoint = new("https://apis.roblox.com/marketplace-items/v1/items/details");
    private const int MaxBatchSize = 40;
    private const int MaxAttempts = 2;

    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _anonymousCsrfToken;

    public RobloxMarketplaceItemService(HttpClient httpClient, AppLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, RobloxMarketplaceItemData>> GetManyAsync(
        IEnumerable<string> collectibleItemIds,
        CancellationToken cancellationToken = default)
    {
        var ids = collectibleItemIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxBatchSize)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var id in ids)
        {
            if (_cache.TryGetValue(id, out var cached) && cached.ExpiresAtUtc > now)
                result[id] = cached.Data;
            else
                missing.Add(id);
        }

        if (missing.Count == 0) return result;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            var stillMissing = new List<string>();
            foreach (var id in missing)
            {
                if (_cache.TryGetValue(id, out var cached) && cached.ExpiresAtUtc > now)
                    result[id] = cached.Data;
                else
                    stillMissing.Add(id);
            }

            if (stillMissing.Count == 0) return result;

            IReadOnlyList<RobloxMarketplaceItemData> fetched;
            try
            {
                using var document = await FetchAsync(stillMissing.ToArray(), cancellationToken).ConfigureAwait(false);
                fetched = RobloxMarketplaceItemParser.Parse(document.RootElement);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Marketplace item details request failed: {ex.Message}");
                fetched = stillMissing
                    .Select(id => new RobloxMarketplaceItemData(0, id, false, null, null, null, null, null, false, null, null, null, ex.Message))
                    .ToArray();
            }

            var fetchedById = fetched
                .Where(x => !string.IsNullOrWhiteSpace(x.CollectibleItemId))
                .ToDictionary(x => x.CollectibleItemId, StringComparer.OrdinalIgnoreCase);
            foreach (var id in stillMissing)
            {
                var data = fetchedById.TryGetValue(id, out var value)
                    ? value
                    : new RobloxMarketplaceItemData(0, id, false, null, null, null, null, null, false, null, null, null, "Roblox did not return this collectible item ID.");
                _cache[id] = new CacheEntry(data, DateTimeOffset.UtcNow + (data.IsAvailable ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(45)));
                result[id] = data;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonDocument> FetchAsync(string[] collectibleItemIds, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var response = await SendWithCsrfHandshakeAsync(collectibleItemIds, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
                response.Dispose();
                if (attempt + 1 < MaxAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Clamp(retry.TotalMilliseconds, 1000d, 8000d));
                    _logger.Info($"Marketplace item details rate limited; retrying after {delay.TotalSeconds:0.#}s.");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new HttpRequestException("Roblox Marketplace Items returned HTTP 429.", null, HttpStatusCode.TooManyRequests);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Roblox Marketplace Items returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Marketplace item details retry loop ended unexpectedly.");
    }

    private async Task<HttpResponseMessage> SendWithCsrfHandshakeAsync(string[] collectibleItemIds, CancellationToken cancellationToken)
    {
        var response = await SendAsync(collectibleItemIds, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var token))
        {
            response.Dispose();
            _anonymousCsrfToken = token;
            response = await SendAsync(collectibleItemIds, token, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(string[] collectibleItemIds, string? csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { itemIds = collectibleItemIds })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(csrfToken))
            request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetCsrfToken(HttpResponseMessage response, out string token)
    {
        token = string.Empty;
        if (!response.Headers.TryGetValues("x-csrf-token", out var values)) return false;
        token = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        return token.Length > 0;
    }

    private sealed record CacheEntry(RobloxMarketplaceItemData Data, DateTimeOffset ExpiresAtUtc);
}

public static class RobloxMarketplaceItemParser
{
    public static IReadOnlyList<RobloxMarketplaceItemData> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return Array.Empty<RobloxMarketplaceItemData>();

        var result = new List<RobloxMarketplaceItemData>();
        foreach (var row in root.EnumerateArray())
        {
            var collectibleId = GetString(row, "collectibleItemId");
            if (string.IsNullOrWhiteSpace(collectibleId)) continue;

            var assetId = TryGetInt64(row, "itemTargetId", out var parsedAssetId) ? parsedAssetId : 0;
            var errorCode = row.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null
                ? error.ToString()
                : null;
            var available = assetId > 0 && string.IsNullOrWhiteSpace(errorCode);
            var price = TryGetInt32(row, "price", out var parsedPrice) ? parsedPrice : (int?)null;
            var sales = TryGetInt64(row, "sales", out var parsedSales) ? Math.Max(0, parsedSales) : (long?)null;
            var units = TryGetInt64(row, "unitsAvailableForConsumption", out var parsedUnits) ? Math.Max(0, parsedUnits) : (long?)null;
            var stock = TryGetInt64(row, "assetStock", out var parsedStock) && parsedStock > 0 ? parsedStock : (long?)null;
            var floor = TryGetInt64(row, "lowestResalePrice", out var parsedFloor) && parsedFloor > 0 ? parsedFloor : (long?)null;
            var hasResellers = TryGetBoolean(row, "hasResellers", out var parsedHasResellers) && parsedHasResellers;
            var saleLocation = GetString(row, "saleLocationType");
            var status = TryGetInt32(row, "productSaleStatus", out var parsedStatus) ? parsedStatus : (int?)null;
            var productId = GetString(row, "collectibleProductId");

            result.Add(new RobloxMarketplaceItemData(
                assetId,
                collectibleId,
                available,
                price,
                sales,
                units,
                stock,
                floor,
                hasResellers,
                saleLocation,
                status,
                productId,
                errorCode));
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
