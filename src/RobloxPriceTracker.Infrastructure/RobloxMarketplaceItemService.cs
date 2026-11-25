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
    // Set only when a Marketplace response is actually received. Cached reads
    // retain this timestamp and never renew stock verification.
    public DateTimeOffset? VerifiedAtUtc { get; init; }

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
    private long _cooldownUntilUtcTicks;

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

            if (Interlocked.Read(ref _cooldownUntilUtcTicks) > DateTimeOffset.UtcNow.UtcTicks)
            {
                foreach (var id in stillMissing)
                    result[id] = new RobloxMarketplaceItemData(0, id, false, null, null, null,
                        null, null, false, null, null, null, "Marketplace HTTP 429 cooldown active.");
                return result;
            }

            IReadOnlyList<RobloxMarketplaceItemData> fetched;
            try
            {
                using var document = await FetchAsync(stillMissing.ToArray(), cancellationToken).ConfigureAwait(false);
                var receivedAt = DateTimeOffset.UtcNow;
                fetched = RobloxMarketplaceItemParser.Parse(document.RootElement)
                    .Select(x => x with { VerifiedAtUtc = receivedAt }).ToArray();
                if (fetched.Count == 0)
                {
                    // Roblox occasionally acknowledges a batch with HTTP 200 but
                    // returns no rows. Recover in smaller requests once; empty
                    // responses are never proof of available creator inventory.
                    _logger.Info($"MarketplaceItemService.GetManyAsync: empty batch response for " +
                        $"{stillMissing.Count} collectible IDs; retrying once in smaller batches. " +
                        "No stock will be accepted without a matching Marketplace row.");
                    var recovered = new List<RobloxMarketplaceItemData>();
                    var batchSize = Math.Max(1, (stillMissing.Count + 1) / 2);
                    foreach (var chunk in stillMissing.Chunk(batchSize))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken).ConfigureAwait(false);
                        try
                        {
                            using var retry = await FetchAsync(chunk, cancellationToken).ConfigureAwait(false);
                            var retryReceivedAt = DateTimeOffset.UtcNow;
                            recovered.AddRange(RobloxMarketplaceItemParser.Parse(retry.RootElement)
                                .Select(x => x with { VerifiedAtUtc = retryReceivedAt }));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            _logger.Info("MarketplaceItemService.GetManyAsync: recovery stopped by HTTP 429; " +
                                "unresolved IDs remain unverified. Check Roblox Retry-After before refreshing.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.Info($"MarketplaceItemService.GetManyAsync: recovery chunk unavailable ({ex.Message}); " +
                                "unresolved creator stock remains unverified.");
                        }
                    }

                    fetched = recovered;
                    _logger.Info($"MarketplaceItemService.GetManyAsync: empty batch recovery returned " +
                        $"{recovered.Count}/{stillMissing.Count} rows; missing IDs remain unavailable.");
                }
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
                .GroupBy(x => x.CollectibleItemId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var id in stillMissing)
            {
                var data = fetchedById.TryGetValue(id, out var value)
                    ? value
                    : new RobloxMarketplaceItemData(0, id, false, null, null, null, null, null, false, null, null, null, "Roblox did not return this collectible item ID.");
                // Short-lived stock snapshots; an in-stock item can sell out quickly.
                _cache[id] = new CacheEntry(data, DateTimeOffset.UtcNow +
                    (data.IsPrimaryPurchasable ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(20)));
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
                var retryHeader = response.Headers.RetryAfter;
                var retry = retryHeader?.Delta ??
                    (retryHeader?.Date is { } retryAt ? retryAt - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(3));
                retry = retry < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retry;
                response.Dispose();
                if (attempt + 1 < MaxAttempts && retry <= TimeSpan.FromSeconds(8))
                {
                    _logger.Info($"Marketplace item details rate limited; retrying after {retry.TotalSeconds:0.#}s.");
                    await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resumeAt = DateTimeOffset.UtcNow.Add(retry);
                Interlocked.Exchange(ref _cooldownUntilUtcTicks, resumeAt.UtcTicks);
                _logger.Info($"MarketplaceItemService.FetchAsync: HTTP 429 cooldown until {resumeAt:O}; " +
                    "creator stock will remain unverified until then.");
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
            // A malformed row must not discard valid stock from the rest of the batch.
            if (row.ValueKind != JsonValueKind.Object) continue;
            var collectibleId = GetString(row, "collectibleItemId");
            if (string.IsNullOrWhiteSpace(collectibleId)) continue;

            var assetId = TryGetInt64(row, "itemTargetId", out var parsedAssetId) ? parsedAssetId : 0;
            var errorCode = row.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null
                ? error.ToString()
                : null;
            var available = assetId > 0 && string.IsNullOrWhiteSpace(errorCode);
            var price = TryGetInt32(row, "price", out var parsedPrice) ? parsedPrice : (int?)null;
            var sales = TryGetInt64(row, "sales", out var parsedSales) && parsedSales >= 0 ? parsedSales : (long?)null;
            var units = TryGetInt64(row, "unitsAvailableForConsumption", out var parsedUnits) && parsedUnits >= 0 ? parsedUnits : (long?)null;
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
