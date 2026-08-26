using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public sealed record RobloxResellerMarketData(
    long AssetId,
    bool IsAvailable,
    string CollectibleItemId,
    int ObservedListings,
    bool HasMoreListings,
    long? LowestPrice,
    double? MedianTop10,
    double? MedianTop30,
    int ListingsWithin10Pct,
    int ListingsWithin20Pct,
    string? Error);

/// <summary>
/// Reads the anonymous seller book for collectible-system Limiteds. Only the first
/// 100 listings are sampled; HasMoreListings is retained so scoring can treat the
/// observed count as a lower bound instead of pretending it is the full market.
/// </summary>
public sealed class RobloxResellerDataService
{
    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _requestGate = new(3, 3);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public RobloxResellerDataService(HttpClient httpClient, AppLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<RobloxResellerMarketData> GetAsync(
        long assetId,
        string collectibleItemId,
        CancellationToken cancellationToken = default)
    {
        if (assetId <= 0 || string.IsNullOrWhiteSpace(collectibleItemId))
            return Unavailable(assetId, collectibleItemId, "Collectible item ID is unavailable.");

        var key = collectibleItemId.Trim();
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            return cached.Data;

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(key, out cached) && cached.ExpiresAtUtc > now)
                return cached.Data;

            RobloxResellerMarketData result;
            try
            {
                var encoded = Uri.EscapeDataString(key);
                var uri = new Uri($"https://apis.roblox.com/marketplace-sales/v1/item/{encoded}/resellers?limit=100");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    result = Unavailable(assetId, key, $"Roblox Marketplace Sales returned HTTP {(int)response.StatusCode}.");
                }
                else
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    result = RobloxResellerDataParser.Parse(assetId, key, document.RootElement);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Reseller book request failed for asset {assetId}: {ex.Message}");
                result = Unavailable(assetId, key, ex.Message);
            }

            _cache[key] = new CacheEntry(result, DateTimeOffset.UtcNow + (result.IsAvailable ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(2)));
            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static RobloxResellerMarketData Unavailable(long assetId, string? collectibleItemId, string error) =>
        new(assetId, false, collectibleItemId ?? string.Empty, 0, false, null, null, null, 0, 0, error);

    private sealed record CacheEntry(RobloxResellerMarketData Data, DateTimeOffset ExpiresAtUtc);
}

public static class RobloxResellerDataParser
{
    public static RobloxResellerMarketData Parse(long assetId, string collectibleItemId, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new RobloxResellerMarketData(assetId, false, collectibleItemId, 0, false, null, null, null, 0, 0, "Roblox returned no reseller data array.");

        var prices = new List<long>();
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("price", out var priceElement) || priceElement.ValueKind != JsonValueKind.Number) continue;
            if (!priceElement.TryGetInt64(out var price) || price <= 0) continue;
            prices.Add(price);
        }

        prices.Sort();
        var hasMore = root.TryGetProperty("nextPageCursor", out var cursor)
            && cursor.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cursor.GetString());

        if (prices.Count == 0)
            return new RobloxResellerMarketData(assetId, false, collectibleItemId, 0, hasMore, null, null, null, 0, 0, "No positive reseller prices were returned.");

        var floor = prices[0];
        var within10 = prices.Count(x => x <= floor * 1.10d);
        var within20 = prices.Count(x => x <= floor * 1.20d);
        return new RobloxResellerMarketData(
            assetId,
            true,
            collectibleItemId,
            prices.Count,
            hasMore,
            floor,
            Median(prices.Take(10)),
            Median(prices.Take(30)),
            within10,
            within20,
            null);
    }

    private static double? Median(IEnumerable<long> values)
    {
        var data = values.OrderBy(x => x).ToArray();
        if (data.Length == 0) return null;
        var middle = data.Length / 2;
        return data.Length % 2 == 1
            ? data[middle]
            : (data[middle - 1] + data[middle]) / 2d;
    }
}
