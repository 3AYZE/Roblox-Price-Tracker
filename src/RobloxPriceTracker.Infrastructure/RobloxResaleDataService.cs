using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public sealed record ResaleDataPoint(DateTimeOffset Date, double Value);

public sealed record RobloxResaleMarketData(
    long AssetId,
    bool IsAvailable,
    string Source,
    long? TotalSales,
    double? RecentAveragePrice,
    IReadOnlyList<ResaleDataPoint> PriceDataPoints,
    IReadOnlyList<ResaleDataPoint> VolumeDataPoints,
    string? CollectibleItemId,
    string? Error)
{
    public double SalesLast24h
    {
        get
        {
            var valid = VolumeDataPoints.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
            if (valid.Length == 0) return 0;
            var newest = valid[0].Date;
            return valid.Where(x => x.Date >= newest - TimeSpan.FromHours(25)).Sum(x => x.Value);
        }
    }

    public double SalesLast7d
    {
        get
        {
            var valid = VolumeDataPoints.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
            if (valid.Length == 0) return 0;
            var newest = valid[0].Date;
            return valid.Where(x => x.Date >= newest - TimeSpan.FromDays(7.25)).Sum(x => x.Value);
        }
    }

    public double SalesPerDay7d => SalesLast7d / 7d;
    public DateTimeOffset? LastVolumeDate => VolumeDataPoints.Where(x => x.Value > 0).OrderByDescending(x => x.Date).FirstOrDefault()?.Date;
    public bool HasSalesSeries => VolumeDataPoints.Any(x => x.Value > 0);
    public bool HasPriceSeries => PriceDataPoints.Any(x => x.Value > 0);
}

/// <summary>
/// Retrieves Roblox's anonymous resale aggregates (RAP and daily sale volume) without
/// account cookies. Legacy limiteds use economy.roblox.com. Collectible-system items
/// fall back to marketplace-sales after resolving collectibleItemId through catalog details.
/// Results are cached to avoid turning UI refreshes into high-frequency API traffic.
/// </summary>
public sealed class RobloxResaleDataService
{
    private static readonly Uri CatalogDetailsEndpoint = new("https://catalog.roblox.com/v1/catalog/items/details");
    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _requestGate = new(3, 3);
    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();
    private string? _anonymousCsrfToken;

    public RobloxResaleDataService(HttpClient httpClient, AppLogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<long, RobloxResaleMarketData>> GetManyAsync(
        IEnumerable<long> assetIds,
        CancellationToken cancellationToken = default)
    {
        var ids = assetIds.Where(x => x > 0).Distinct().ToArray();
        var tasks = ids.Select(async id => new KeyValuePair<long, RobloxResaleMarketData>(
            id,
            await GetAsync(id, cancellationToken).ConfigureAwait(false)));
        var pairs = await Task.WhenAll(tasks).ConfigureAwait(false);
        return pairs.ToDictionary(x => x.Key, x => x.Value);
    }

    public async Task<RobloxResaleMarketData> GetAsync(long assetId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0)
        {
            return Unavailable(assetId, "Invalid asset ID.");
        }

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(assetId, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Data;
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(assetId, out cached) && cached.ExpiresAtUtc > now)
            {
                return cached.Data;
            }

            RobloxResaleMarketData result;
            try
            {
                var legacy = await FetchResaleDataAsync(
                    new Uri($"https://economy.roblox.com/v1/assets/{assetId}/resale-data"),
                    assetId,
                    "Roblox Economy",
                    null,
                    cancellationToken).ConfigureAwait(false);

                if (HasMeaningfulMarketData(legacy))
                {
                    result = legacy;
                }
                else
                {
                    var collectibleId = await ResolveCollectibleItemIdAsync(assetId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(collectibleId))
                    {
                        var encodedId = Uri.EscapeDataString(collectibleId);
                        var modern = await FetchResaleDataAsync(
                            new Uri($"https://apis.roblox.com/marketplace-sales/v1/item/{encodedId}/resale-data"),
                            assetId,
                            "Roblox Marketplace Sales",
                            collectibleId,
                            cancellationToken).ConfigureAwait(false);
                        result = HasMeaningfulMarketData(modern) ? modern : legacy with
                        {
                            CollectibleItemId = collectibleId,
                            Error = modern.Error ?? legacy.Error ?? "Roblox returned no usable resale aggregates for this item."
                        };
                    }
                    else
                    {
                        result = legacy;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Resale aggregate request failed for asset {assetId}: {ex}");
                result = Unavailable(assetId, ex.Message);
            }

            var ttl = result.IsAvailable ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
            _cache[assetId] = new CacheEntry(result, DateTimeOffset.UtcNow + ttl);
            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<RobloxResaleMarketData> FetchResaleDataAsync(
        Uri uri,
        long assetId,
        string source,
        string? collectibleItemId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Unavailable(assetId, $"{source} returned HTTP {(int)response.StatusCode}.", source, collectibleItemId);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return RobloxResaleDataParser.Parse(assetId, document.RootElement, source, collectibleItemId);
    }

    private async Task<string?> ResolveCollectibleItemIdAsync(long assetId, CancellationToken cancellationToken)
    {
        var payload = new { items = new[] { new { itemType = "Asset", id = assetId } } };
        HttpResponseMessage response = await SendCatalogRequestAsync(payload, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var token))
        {
            response.Dispose();
            _anonymousCsrfToken = token;
            response = await SendCatalogRequestAsync(payload, token, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var returnedId) || returnedId != assetId) continue;
                if (item.TryGetProperty("collectibleItemId", out var collectible) && collectible.ValueKind == JsonValueKind.String)
                {
                    return collectible.GetString();
                }
            }
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendCatalogRequestAsync<TPayload>(TPayload payload, string? csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CatalogDetailsEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(csrfToken)) request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetCsrfToken(HttpResponseMessage response, out string token)
    {
        token = string.Empty;
        if (!response.Headers.TryGetValues("x-csrf-token", out var values)) return false;
        token = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        return token.Length > 0;
    }

    private static bool HasMeaningfulMarketData(RobloxResaleMarketData data) =>
        data.RecentAveragePrice is > 0 || data.PriceDataPoints.Any(x => x.Value > 0) || data.VolumeDataPoints.Any(x => x.Value > 0);

    private static RobloxResaleMarketData Unavailable(long assetId, string error, string source = "Unavailable", string? collectibleItemId = null) =>
        new(assetId, false, source, null, null, Array.Empty<ResaleDataPoint>(), Array.Empty<ResaleDataPoint>(), collectibleItemId, error);

    private sealed record CacheEntry(RobloxResaleMarketData Data, DateTimeOffset ExpiresAtUtc);
}

public static class RobloxResaleDataParser
{
    public static RobloxResaleMarketData Parse(long assetId, JsonElement root, string source, string? collectibleItemId = null)
    {
        var pricePoints = ParsePoints(root, "priceDataPoints");
        var volumePoints = ParsePoints(root, "volumeDataPoints");
        var rap = TryGetDouble(root, "recentAveragePrice");
        var sales = TryGetInt64(root, "sales");
        var available = rap is > 0 || pricePoints.Any(x => x.Value > 0) || volumePoints.Any(x => x.Value > 0);

        return new RobloxResaleMarketData(
            assetId,
            available,
            source,
            sales,
            rap is > 0 ? rap : null,
            pricePoints,
            volumePoints,
            collectibleItemId,
            available ? null : "Roblox returned an empty resale-data series.");
    }

    private static IReadOnlyList<ResaleDataPoint> ParsePoints(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var points) || points.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ResaleDataPoint>();
        }

        var result = new List<ResaleDataPoint>();
        foreach (var point in points.EnumerateArray())
        {
            if (!point.TryGetProperty("date", out var dateElement) || dateElement.ValueKind != JsonValueKind.String) continue;
            if (!DateTimeOffset.TryParse(dateElement.GetString(), out var date)) continue;
            var value = TryGetDouble(point, "value");
            if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) continue;
            result.Add(new ResaleDataPoint(date, value.Value));
        }
        return result.OrderBy(x => x.Date).ToArray();
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number) return null;
        return property.TryGetDouble(out var value) ? value : null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number) return null;
        return property.TryGetInt64(out var value) ? value : null;
    }
}
