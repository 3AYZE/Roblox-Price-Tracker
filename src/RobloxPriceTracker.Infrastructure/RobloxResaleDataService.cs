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
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public bool RateLimited { get; init; }

    // A historical series is not evidence of current trading volume. Roblox publishes
    // daily buckets, so use the same 48-hour freshness window as Official Hunt.
    public bool HasSalesCoverage(DateTimeOffset nowUtc) =>
        OfficialResaleCoveragePolicy.HasRecentVolume(VolumeDataPoints, nowUtc);

    public double SalesWithinDays(double days, DateTimeOffset nowUtc)
    {
        if (days <= 0 || !HasSalesCoverage(nowUtc)) return 0;
        var cutoff = nowUtc - TimeSpan.FromDays(days);
        return VolumeDataPoints.Where(x => double.IsFinite(x.Value) && x.Value >= 0 &&
            x.Date >= cutoff && x.Date <= nowUtc).Sum(x => x.Value);
    }

    public double SalesLast24h => SalesWithinDays(25d / 24d, DateTimeOffset.UtcNow);
    public double SalesLast7d => SalesWithinDays(7.25d, DateTimeOffset.UtcNow);
    public double SalesLast30d => SalesWithinDays(30.25d, DateTimeOffset.UtcNow);
    public double SalesPerDay7d => SalesLast7d / 7d;
    public DateTimeOffset? LastVolumeDate => VolumeDataPoints.Where(x => x.Value > 0).OrderByDescending(x => x.Date).FirstOrDefault()?.Date;
    public bool HasSalesSeries => HasSalesCoverage(DateTimeOffset.UtcNow);
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
    private readonly ConcurrentDictionary<string, CacheEntry> _modernCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, CacheEntry> _legacyOnlyCache = new();
    private string? _anonymousCsrfToken;
    private long _cooldownUntilUtcTicks;

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

    // Official catalog search already returns a collectible ID for migrated items.
    // Prefer that validated ID so each supported asset needs one resale GET,
    // avoiding an unnecessary Economy request and catalog ID lookup.
    public async Task<IReadOnlyDictionary<long, RobloxResaleMarketData>> GetManyKnownCollectiblesAsync(
        IEnumerable<(long AssetId, string? CollectibleItemId)> assets,
        CancellationToken cancellationToken = default)
    {
        var distinct = assets.Where(x => x.AssetId > 0)
            .GroupBy(x => x.AssetId)
            .Select(group => group.OrderByDescending(x =>
                Guid.TryParse(x.CollectibleItemId, out _)).First())
            .ToArray();
        var tasks = distinct.Select(async asset => new KeyValuePair<long, RobloxResaleMarketData>(
            asset.AssetId,
            await GetKnownCollectibleAsync(asset.AssetId, asset.CollectibleItemId, cancellationToken)
                .ConfigureAwait(false)));
        var pairs = await Task.WhenAll(tasks).ConfigureAwait(false);
        return pairs.ToDictionary(x => x.Key, x => x.Value);
    }

    public async Task<RobloxResaleMarketData> GetKnownCollectibleAsync(
        long assetId, string? collectibleItemId, CancellationToken cancellationToken = default)
    {
        if (assetId <= 0 || !Guid.TryParse(collectibleItemId, out var parsedId))
            return await GetAsync(assetId, cancellationToken).ConfigureAwait(false);

        var knownId = parsedId.ToString("D");
        var modern = await GetModernAsync(assetId, knownId, cancellationToken).ConfigureAwait(false);
        // An independently verified RAP is useful even when Roblox has no
        // current sale buckets. Never request another endpoint just to fill 7d.
        if (modern.RateLimited || modern.RecentAveragePrice is > 0)
            return modern;

        // With a known GUID, only legacy RAP may add useful independent
        // coverage. Fetch it directly, without resolving the ID or repeating
        // the modern request; reject legacy history that demonstrably stopped.
        var legacy = await GetLegacyForKnownAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (legacy.RateLimited)
            return modern.IsAvailable ? modern with
            {
                RateLimited = true,
                RetryAfterUtc = legacy.RetryAfterUtc,
                Error = "Legacy RAP rate-limited; retaining available Marketplace data."
            } : legacy;
        return MergeIndependentCoverage(legacy, modern,
            OfficialResaleCoveragePolicy.IsDemonstrablyStaleLegacy(legacy, DateTimeOffset.UtcNow), knownId);
    }

    private async Task<RobloxResaleMarketData> GetLegacyForKnownAsync(
        long assetId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_legacyOnlyCache.TryGetValue(assetId, out var cached) && cached.ExpiresAtUtc > now)
            return cached.Data;

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_legacyOnlyCache.TryGetValue(assetId, out cached) && cached.ExpiresAtUtc > now)
                return cached.Data;
            if (GetCooldown(now, assetId) is { } coolingDown) return coolingDown;

            RobloxResaleMarketData result;
            try
            {
                result = await FetchResaleDataAsync(
                    new Uri($"https://economy.roblox.com/v1/assets/{assetId}/resale-data"),
                    assetId, "Roblox Economy", null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Info($"Resale legacy RAP fallback failed for asset {assetId}: {ex.Message}");
                result = Unavailable(assetId, ex.Message, "Roblox Economy");
            }

            if (result.RateLimited) ApplyCooldown(result.RetryAfterUtc);
            var expires = DateTimeOffset.UtcNow + (result.IsAvailable ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(3));
            if (result.RetryAfterUtc is { } retryAfter && retryAfter > expires) expires = retryAfter;
            _legacyOnlyCache[assetId] = new CacheEntry(result, expires);
            return result;
        }
        finally { _requestGate.Release(); }
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

            if (GetCooldown(now, assetId) is { } coolingDown)
                return coolingDown;

            RobloxResaleMarketData result;
            try
            {
                var legacy = await FetchResaleDataAsync(
                    new Uri($"https://economy.roblox.com/v1/assets/{assetId}/resale-data"),
                    assetId,
                    "Roblox Economy",
                    null,
                    cancellationToken).ConfigureAwait(false);

                // Roblox's old Economy feed can return a populated RAP and
                // historical price/volume series that stopped updating. Never
                // let such a response prevent the modern collectible lookup.
                var staleLegacy = OfficialResaleCoveragePolicy.IsDemonstrablyStaleLegacy(
                    legacy, DateTimeOffset.UtcNow);
                if (legacy.RateLimited ||
                    (HasMeaningfulMarketData(legacy) && !staleLegacy && legacy.HasSalesCoverage(DateTimeOffset.UtcNow)))
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
                        result = MergeIndependentCoverage(legacy, modern, staleLegacy, collectibleId);
                    }
                    else
                    {
                        result = staleLegacy
                            ? Unavailable(assetId,
                                "Legacy Roblox resale history is stale and a collectible ID could not be resolved.")
                            : legacy with
                            {
                                Error = legacy.HasSalesCoverage(DateTimeOffset.UtcNow) ? legacy.Error :
                                    "Current sales unavailable: collectible ID could not be resolved."
                            };
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
            if (result.RateLimited) ApplyCooldown(result.RetryAfterUtc);
            var expires = DateTimeOffset.UtcNow + ttl;
            if (result.RetryAfterUtc is { } retryAfter && retryAfter > expires)
                expires = retryAfter;
            _cache[assetId] = new CacheEntry(result, expires);
            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<RobloxResaleMarketData> GetModernAsync(
        long assetId,
        string collectibleItemId,
        CancellationToken cancellationToken = default)
    {
        if (assetId <= 0 || string.IsNullOrWhiteSpace(collectibleItemId))
            return Unavailable(assetId, "Collectible item ID is unavailable.", collectibleItemId: collectibleItemId);

        var key = collectibleItemId.Trim();
        var now = DateTimeOffset.UtcNow;
        if (_modernCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            return cached.Data;

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_modernCache.TryGetValue(key, out cached) && cached.ExpiresAtUtc > now)
                return cached.Data;

            if (GetCooldown(now, assetId) is { } coolingDown)
                return coolingDown;

            RobloxResaleMarketData result;
            try
            {
                var encoded = Uri.EscapeDataString(key);
                result = await FetchResaleDataAsync(
                    new Uri($"https://apis.roblox.com/marketplace-sales/v1/item/{encoded}/resale-data"),
                    assetId,
                    "Roblox Marketplace Sales",
                    key,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Modern resale aggregate request failed for asset {assetId}: {ex.Message}");
                result = Unavailable(assetId, ex.Message, "Roblox Marketplace Sales", key);
            }

            if (result.RateLimited) ApplyCooldown(result.RetryAfterUtc);
            var expires = DateTimeOffset.UtcNow + (result.IsAvailable ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2));
            if (result.RetryAfterUtc is { } retryAfter && retryAfter > expires)
                expires = retryAfter;
            _modernCache[key] = new CacheEntry(result, expires);
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
            var unavailable = Unavailable(assetId, $"{source} returned HTTP {(int)response.StatusCode}.", source, collectibleItemId);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var now = DateTimeOffset.UtcNow;
                var retry = response.Headers.RetryAfter;
                var requested = retry?.Date ?? (retry?.Delta is { } delta ? now + delta : now.AddMinutes(5));
                return unavailable with
                {
                    RateLimited = true,
                    RetryAfterUtc = requested < now.AddSeconds(30)
                        ? now.AddSeconds(30)
                        : requested > now.AddMinutes(30) ? now.AddMinutes(30) : requested
                };
            }
            return unavailable;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return RobloxResaleDataParser.Parse(assetId, document.RootElement, source, collectibleItemId);
    }

    private async Task<string?> ResolveCollectibleItemIdAsync(
        long assetId, CancellationToken cancellationToken)
    {
        // Prefer catalog batch details. If it omits a collectibleItemId, Roblox
        // Economy's asset-detail endpoint can supply the migrated identifier.
        // A missing identifier is never replaced with the numeric asset ID.
        try
        {
            var payload = new { items = new[] { new { itemType = "Asset", id = assetId } } };
            HttpResponseMessage response = await SendCatalogRequestAsync(
                payload, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                TryGetCsrfToken(response, out var token))
            {
                response.Dispose();
                _anonymousCsrfToken = token;
                response = await SendCatalogRequestAsync(payload, token, cancellationToken).ConfigureAwait(false);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (!item.TryGetProperty("id", out var returned) ||
                                !returned.TryGetInt64(out var returnedId) || returnedId != assetId)
                                continue;
                            if (item.TryGetProperty("collectibleItemId", out var id) &&
                                id.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(id.GetString()))
                                return id.GetString();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Info($"Catalog collectible ID lookup failed for asset {assetId}: {ex.Message}");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://economy.roblox.com/v2/assets/{assetId}/details");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("collectibleItemId", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Info($"Economy collectible ID lookup failed for asset {assetId}: {ex.Message}");
        }

        return null;
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
        data.RecentAveragePrice is > 0 || data.PriceDataPoints.Any(x => x.Value > 0) ||
        data.VolumeDataPoints.Any(x => x.Value > 0) || data.HasSalesSeries;

    // RAP and current volume are independent observations. A legacy RAP may still be
    // trustworthy when its daily volume has stopped, while modern sales can be fresh.
    // Historical series from an expired legacy feed must never become current evidence.
    public static RobloxResaleMarketData MergeIndependentCoverage(
        RobloxResaleMarketData legacy, RobloxResaleMarketData modern,
        bool staleLegacy, string collectibleId)
    {
        var fallback = !staleLegacy && HasMeaningfulMarketData(legacy) ? legacy : null;
        if (!HasMeaningfulMarketData(modern) && fallback is null)
            return modern.RateLimited ? modern : Unavailable(legacy.AssetId,
                staleLegacy ? "Legacy history stale; current Marketplace sales unavailable." :
                    modern.Error ?? legacy.Error ?? "Roblox returned no usable resale aggregates.",
                "Roblox Marketplace Sales", collectibleId);

        var now = DateTimeOffset.UtcNow;
        var source = HasMeaningfulMarketData(modern) ? modern : fallback!;
        var rap = modern.RecentAveragePrice is > 0 ? modern.RecentAveragePrice : fallback?.RecentAveragePrice;
        var prices = modern.PriceDataPoints.Any(p => p.Value > 0)
            ? modern.PriceDataPoints : fallback?.PriceDataPoints ?? [];
        var volumes = OfficialResaleCoveragePolicy.CompletedDailyVolume(modern.VolumeDataPoints, 7, now).HasValue
            ? modern.VolumeDataPoints :
            OfficialResaleCoveragePolicy.CompletedDailyVolume(fallback?.VolumeDataPoints, 7, now).HasValue
                ? fallback!.VolumeDataPoints :
            modern.HasSalesCoverage(now) ? modern.VolumeDataPoints :
            fallback?.HasSalesCoverage(now) == true ? fallback.VolumeDataPoints :
            modern.VolumeDataPoints.Count > 0 ? modern.VolumeDataPoints : fallback?.VolumeDataPoints ?? [];

        return source with
        {
            IsAvailable = rap is > 0 || prices.Any(p => p.Value > 0) ||
                OfficialResaleCoveragePolicy.HasRecentVolume(volumes, now),
            RecentAveragePrice = rap,
            PriceDataPoints = prices,
            VolumeDataPoints = volumes,
            CollectibleItemId = collectibleId,
            RateLimited = modern.RateLimited,
            RetryAfterUtc = modern.RetryAfterUtc,
            Error = modern.RateLimited ? "Current Marketplace sales rate-limited; showing previously verified data." :
                !OfficialResaleCoveragePolicy.HasRecentVolume(volumes, now)
                    ? modern.Error ?? "Current sale volume unavailable from Roblox." : null
        };
    }

    private RobloxResaleMarketData? GetCooldown(DateTimeOffset now, long assetId)
    {
        var until = Interlocked.Read(ref _cooldownUntilUtcTicks);
        return until > now.UtcTicks
            ? Unavailable(assetId, "Roblox resale API is rate-limited; waiting for Retry-After.") with
            {
                RateLimited = true,
                RetryAfterUtc = new DateTimeOffset(until, TimeSpan.Zero)
            }
            : null;
    }

    private void ApplyCooldown(DateTimeOffset? retryAfterUtc)
    {
        if (retryAfterUtc is not { } retryAfter) return;
        var proposed = retryAfter.UtcTicks;
        var previous = Interlocked.Read(ref _cooldownUntilUtcTicks);
        while (proposed > previous)
        {
            var observed = Interlocked.CompareExchange(ref _cooldownUntilUtcTicks, proposed, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

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
        var available = rap is > 0 || pricePoints.Any(x => x.Value > 0) ||
            volumePoints.Any(x => x.Value > 0) ||
            OfficialResaleCoveragePolicy.HasRecentVolume(volumePoints, DateTimeOffset.UtcNow);

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
            if (value is null || !double.IsFinite(value.Value) || value.Value < 0) continue;
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
