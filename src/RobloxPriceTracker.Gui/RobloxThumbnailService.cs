using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

/// <summary>
/// Shared, type-safe Roblox PNG thumbnail loader. Asset IDs and bundle IDs have
/// separate caches, and pending/blocked thumbnails are never cached as pictures.
/// No credentials, user cookies, or personal data are required.
/// </summary>
public sealed class RobloxThumbnailService
{
    private static readonly Uri AssetEndpoint = new("https://thumbnails.roblox.com/v1/assets");
    private static readonly Uri BundleEndpoint = new("https://thumbnails.roblox.com/v1/bundles/thumbnails");
    private static readonly TimeSpan ThumbnailTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan MissingRetryDelay = TimeSpan.FromMinutes(3);
    private const int MaxBatchSize = 80;
    private const int MaxCacheItems = 5000;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<long, CacheEntry> _assetCache = new();
    private readonly ConcurrentDictionary<long, CacheEntry> _bundleCache = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public RobloxThumbnailService(HttpClient httpClient) => _httpClient = httpClient;

    public Task<IReadOnlyDictionary<long, string>> GetAssetThumbnailUrlsAsync(
        IEnumerable<long> assetIds, CancellationToken cancellationToken = default) =>
        LoadAsync(assetIds, "assetIds", AssetEndpoint, _assetCache, cancellationToken);

    public Task<IReadOnlyDictionary<long, string>> GetBundleThumbnailUrlsAsync(
        IEnumerable<long> bundleIds, CancellationToken cancellationToken = default) =>
        LoadAsync(bundleIds, "bundleIds", BundleEndpoint, _bundleCache, cancellationToken);

    private async Task<IReadOnlyDictionary<long, string>> LoadAsync(
        IEnumerable<long> identifiers, string queryName, Uri endpoint,
        ConcurrentDictionary<long, CacheEntry> cache, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = identifiers.Where(x => x > 0).Distinct().Take(MaxCacheItems).ToArray();
        var result = new Dictionary<long, string>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            if (cache.TryGetValue(id, out var entry) && entry.ExpiresAtUtc > now)
            {
                if (entry.ImageUrl is { } cachedUrl) result[id] = cachedUrl;
            }
            else missing.Add(id);
        }

        foreach (var chunk in missing.Chunk(MaxBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Concurrent scan/selection requests re-check the cache after waiting.
                var pending = chunk.Where(id => !cache.TryGetValue(id, out var entry) ||
                                                entry.ExpiresAtUtc <= DateTimeOffset.UtcNow).ToArray();
                if (pending.Length == 0)
                {
                    foreach (var id in chunk)
                        if (cache.TryGetValue(id, out var hit) && hit.ImageUrl is { } url) result[id] = url;
                    continue;
                }

                // Bundle thumbnails do not accept the asset-only returnPolicy parameter.
                var options = queryName == "assetIds"
                    ? "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false"
                    : "&size=150x150&format=Png&isCircular=false";
                var uri = new Uri($"{endpoint}?{queryName}={Uri.EscapeDataString(string.Join(',', pending))}" +
                    options);
                using var response = await _httpClient.GetAsync(uri,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    break; // Do not cache 429; next refresh can retry.
                if (!response.IsSuccessStatusCode)
                    continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var accepted = RobloxThumbnailResponseParser.ParseCompleted(
                    json.RootElement, pending.ToHashSet());
                var observedAt = DateTimeOffset.UtcNow;
                foreach (var id in pending)
                {
                    if (accepted.TryGetValue(id, out var url))
                    {
                        cache[id] = new CacheEntry(url, observedAt + ThumbnailTtl);
                        result[id] = url;
                    }
                    else
                    {
                        // Pending and blocked states get a short retry, never a fake image.
                        cache[id] = new CacheEntry(null, observedAt + MissingRetryDelay);
                    }
                }
                foreach (var id in chunk)
                {
                    if (!result.ContainsKey(id) && cache.TryGetValue(id, out var cached) &&
                        cached.ImageUrl is { } url && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                        result[id] = url;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // Images are optional, not a reason to discard valid price data.
            }
            catch (JsonException)
            {
                // Roblox can temporarily return a malformed body.
            }
            finally
            {
                _requestGate.Release();
            }
        }

        if (cache.Count > MaxCacheItems)
        {
            foreach (var old in cache.Where(x => x.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                                     .Take(cache.Count - MaxCacheItems))
                cache.TryRemove(old.Key, out _);
        }
        return result;
    }

    public async Task<string?> GetAssetThumbnailUrlAsync(
        long assetId, CancellationToken cancellationToken = default)
    {
        var result = await GetAssetThumbnailUrlsAsync(new[] { assetId }, cancellationToken)
            .ConfigureAwait(false);
        return result.TryGetValue(assetId, out var url) ? url : null;
    }

    private sealed record CacheEntry(string? ImageUrl, DateTimeOffset ExpiresAtUtc);
}
