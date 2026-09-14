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
/// Finds buyable UGC Limiteds without imposing an item-age window. Discovery deliberately scans
/// several pages of Roblox's UGC best-selling day/week/month feeds, combines repeated appearances
/// into a momentum signal, and keeps a small recent-drop reserve. This allows an older Limited to
/// re-enter Hunter when current demand revives instead of requiring it to be a new release or a
/// first-page catalog result. Search rows are discovery-only; IDs are hydrated through bounded
/// catalog and Marketplace Item batches before buyability, stock, and price checks are applied.
/// </summary>
public sealed class RobloxUgcDiscoveryService
{
    private sealed record DiscoveryFeed(
        string Name,
        Uri Endpoint,
        int MaxPages,
        int Budget,
        double Weight,
        bool ReserveRecent = false);

    private sealed class DiscoverySignal
    {
        private readonly HashSet<string> _feeds = new(StringComparer.OrdinalIgnoreCase);

        public DiscoverySignal(long assetId)
        {
            AssetId = assetId;
        }

        public long AssetId { get; }
        public double Score { get; private set; }
        public int BestRank { get; private set; } = int.MaxValue;
        public int FeedHits => _feeds.Count;

        public void Observe(string feedName, int rank, double weight)
        {
            if (!_feeds.Add(feedName)) return;
            BestRank = Math.Min(BestRank, rank);

            // Rank is intentionally a discovery signal, not a fabricated sales velocity. A high
            // position in several independent sales windows is stronger evidence than appearing in
            // only one window. Actual purchase-count deltas still drive Hunter velocity later.
            var rankStrength = Math.Clamp(1d - (rank - 1d) / 120d, 0.08d, 1d);
            Score += weight * rankStrength * 100d;
        }

        public double CompositeScore => Score + Math.Max(0, FeedHits - 1) * 18d;
    }

    private sealed record DiscoveryIdSet(
        long[] Ids,
        int FeedsUsed,
        int PagesUsed,
        bool FromCache);

    private static readonly DiscoveryFeed[] DiscoveryFeeds =
    [
        // UGC-specific momentum feeds come first and paginate beyond Roblox's 30-row first page.
        // The previous implementation only kept the first 10 UGC weekly rows, which could exclude
        // a legitimate older seller such as Caesar Crown even when its current demand was strong.
        new(
            "ugc-sales-day",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=13&salesTypeFilter=2&SortType=2&SortAggregation=1&Limit=30"),
            MaxPages: 2,
            Budget: 60,
            Weight: 1.35d),
        new(
            "ugc-sales-week",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=13&salesTypeFilter=2&SortType=2&SortAggregation=3&Limit=30"),
            MaxPages: 3,
            Budget: 90,
            Weight: 1.50d),
        new(
            "ugc-sales-month",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=13&salesTypeFilter=2&SortType=2&SortAggregation=4&Limit=30"),
            MaxPages: 2,
            Budget: 60,
            Weight: 1.05d),

        // Broader Limited feeds are fallback coverage for catalog taxonomy drift.
        new(
            "all-sales-day",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=1&Limit=30"),
            MaxPages: 1,
            Budget: 30,
            Weight: 0.90d),
        new(
            "all-sales-week",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=3&Limit=30"),
            MaxPages: 2,
            Budget: 60,
            Weight: 1.00d),

        // Keep some room for brand-new Limiteds that have not built enough sales history yet.
        new(
            "recent-limiteds",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=13&salesTypeFilter=2&SortType=3&Limit=30"),
            MaxPages: 1,
            Budget: 30,
            Weight: 0.20d,
            ReserveRecent: true)
    ];

    private static readonly Uri DetailsEndpoint = new("https://catalog.roblox.com/v1/catalog/items/details");

    private const int MaxDiscoveryIds = 140;
    private const int MomentumQuota = 120;
    private const int RecentReserve = MaxDiscoveryIds - MomentumQuota;
    private const int MaxDetailBatchSize = 40;
    private const int MaxMarketplaceBatchSize = 40;
    private const int MaxRateLimitAttempts = 2;
    private static readonly TimeSpan DiscoveryCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InterPageDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan InterFeedDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InterDetailBatchDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan InterMarketplaceBatchDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly RobloxMarketplaceItemService _marketplaceItems;
    private readonly bool _expandDiscovery;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private DiscoveryIdSet? _cachedDiscovery;
    private DateTimeOffset _cachedDiscoveryExpiresAtUtc;
    private string? _anonymousCsrfToken;

    public RobloxUgcDiscoveryService(HttpClient httpClient, AppLogger logger, bool expandDiscovery = true)
    {
        _httpClient = httpClient;
        _logger = logger;
        _marketplaceItems = new RobloxMarketplaceItemService(httpClient, logger);
        _expandDiscovery = expandDiscovery;
    }

    public async Task<RobloxUgcDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await GetDiscoveryIdsAsync(cancellationToken).ConfigureAwait(false);
        if (discovery.Ids.Length == 0)
        {
            _logger.Info("UGC Hunter discovery returned no UGC Collectible asset IDs.");
            return new RobloxUgcDiscoveryResult(Array.Empty<RobloxUgcCatalogCandidate>(), 0, 0);
        }

        // Avoid issuing hydration in the same burst as a newly refreshed discovery pass. Cached
        // discovery IDs skip the longer search fan-out, but are still rehydrated for fresh sales.
        await Task.Delay(discovery.FromCache ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        var idArray = discovery.Ids;
        var catalogCandidates = new List<RobloxUgcCatalogCandidate>(idArray.Length);
        var hydratedRows = 0;
        var detailBatches = 0;

        for (var offset = 0; offset < idArray.Length; offset += MaxDetailBatchSize)
        {
            var batch = idArray.Skip(offset).Take(MaxDetailBatchSize).ToArray();
            try
            {
                using var detailDocument = await FetchDetailsAsync(batch, cancellationToken).ConfigureAwait(false);
                detailBatches++;
                if (detailDocument.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    hydratedRows += data.GetArrayLength();
                catalogCandidates.AddRange(RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(detailDocument.RootElement));
            }
            catch (RobloxUgcDiscoveryException ex) when (catalogCandidates.Count > 0)
            {
                _logger.Info($"UGC Hunter kept {catalogCandidates.Count} hydrated candidates after a later detail batch failed: {ex.Message}");
                break;
            }

            if (offset + MaxDetailBatchSize < idArray.Length)
                await Task.Delay(InterDetailBatchDelay, cancellationToken).ConfigureAwait(false);
        }

        catalogCandidates = catalogCandidates
            .GroupBy(x => x.AssetId)
            .Select(x => x.First())
            .ToList();

        var collectibleIds = catalogCandidates
            .Select(x => x.CollectibleItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var marketplace = await GetMarketplaceInBatchesAsync(collectibleIds, cancellationToken).ConfigureAwait(false);

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

        _logger.Info(
            $"UGC Hunter broad momentum discovery: {discovery.FeedsUsed} feed(s), {discovery.PagesUsed} page(s), " +
            $"{idArray.Length} selected IDs{(discovery.FromCache ? " (cached discovery)" : string.Empty)}, " +
            $"{detailBatches} detail batch(es), {hydratedRows} catalog rows, {verified} marketplace-verified, " +
            $"{rejectedUnavailable} unavailable rejected, {candidates.Count} active candidates.");
        return new RobloxUgcDiscoveryResult(candidates, idArray.Length, hydratedRows);
    }

    private async Task<DiscoveryIdSet> GetDiscoveryIdsAsync(CancellationToken cancellationToken)
    {
        if (!_expandDiscovery)
            return await DiscoverIdsFromFeedsAsync(DiscoveryFeeds.Take(1).ToArray(), cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var cached = _cachedDiscovery;
        if (cached is not null && _cachedDiscoveryExpiresAtUtc > now)
            return cached with { FromCache = true };

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _cachedDiscovery;
            if (cached is not null && _cachedDiscoveryExpiresAtUtc > now)
                return cached with { FromCache = true };

            var refreshed = await DiscoverIdsFromFeedsAsync(DiscoveryFeeds, cancellationToken).ConfigureAwait(false);
            _cachedDiscovery = refreshed with { FromCache = false };
            _cachedDiscoveryExpiresAtUtc = DateTimeOffset.UtcNow + DiscoveryCacheDuration;
            return refreshed;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task<DiscoveryIdSet> DiscoverIdsFromFeedsAsync(
        IReadOnlyList<DiscoveryFeed> feeds,
        CancellationToken cancellationToken)
    {
        var signals = new Dictionary<long, DiscoverySignal>();
        var recentIds = new List<long>(RecentReserve * 2);
        var recentSeen = new HashSet<long>();
        var feedsUsed = 0;
        var pagesUsed = 0;
        RobloxUgcDiscoveryException? firstFailure = null;

        for (var feedIndex = 0; feedIndex < feeds.Count; feedIndex++)
        {
            var feed = feeds[feedIndex];
            string? cursor = null;
            var rowsConsidered = 0;
            var feedProducedPage = false;

            for (var page = 0; page < feed.MaxPages && rowsConsidered < feed.Budget; page++)
            {
                var pageUri = BuildPageUri(feed.Endpoint, cursor);
                JsonDocument document;
                try
                {
                    document = await FetchDiscoveryAsync(pageUri, cancellationToken).ConfigureAwait(false);
                }
                catch (RobloxUgcDiscoveryException ex)
                {
                    firstFailure ??= ex;
                    _logger.Info($"UGC Hunter discovery skipped {feed.Name} page {page + 1}: {ex.Message}");
                    break;
                }

                using (document)
                {
                    feedProducedPage = true;
                    pagesUsed++;
                    var pageIds = RobloxUgcCatalogDiscoveryParser.ParseDiscoveryIds(document.RootElement);
                    for (var rowIndex = 0; rowIndex < pageIds.Count && rowsConsidered < feed.Budget; rowIndex++)
                    {
                        var id = pageIds[rowIndex];
                        rowsConsidered++;
                        var rank = page * 30 + rowIndex + 1;
                        if (!signals.TryGetValue(id, out var signal))
                        {
                            signal = new DiscoverySignal(id);
                            signals[id] = signal;
                        }
                        signal.Observe(feed.Name, rank, feed.Weight);

                        if (feed.ReserveRecent && recentSeen.Add(id))
                            recentIds.Add(id);
                    }

                    cursor = GetNextPageCursor(document.RootElement);
                }

                if (string.IsNullOrWhiteSpace(cursor) || rowsConsidered >= feed.Budget)
                    break;

                await Task.Delay(InterPageDelay, cancellationToken).ConfigureAwait(false);
            }

            if (feedProducedPage) feedsUsed++;
            if (feedIndex + 1 < feeds.Count)
                await Task.Delay(InterFeedDelay, cancellationToken).ConfigureAwait(false);
        }

        if (signals.Count == 0 && firstFailure is not null)
            throw firstFailure;

        var selected = new List<long>(MaxDiscoveryIds);
        var selectedSet = new HashSet<long>();

        foreach (var signal in signals.Values
                     .OrderByDescending(x => x.CompositeScore)
                     .ThenByDescending(x => x.FeedHits)
                     .ThenBy(x => x.BestRank)
                     .Take(MomentumQuota))
        {
            if (selectedSet.Add(signal.AssetId))
                selected.Add(signal.AssetId);
        }

        foreach (var id in recentIds)
        {
            if (selected.Count >= MaxDiscoveryIds) break;
            if (selectedSet.Add(id)) selected.Add(id);
        }

        // If the recent reserve was not needed, fill the remaining capacity with the next best
        // momentum candidates rather than leaving hydration slots unused.
        if (selected.Count < MaxDiscoveryIds)
        {
            foreach (var signal in signals.Values
                         .OrderByDescending(x => x.CompositeScore)
                         .ThenByDescending(x => x.FeedHits)
                         .ThenBy(x => x.BestRank))
            {
                if (selected.Count >= MaxDiscoveryIds) break;
                if (selectedSet.Add(signal.AssetId)) selected.Add(signal.AssetId);
            }
        }

        return new DiscoveryIdSet(selected.ToArray(), feedsUsed, pagesUsed, FromCache: false);
    }

    private static Uri BuildPageUri(Uri endpoint, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return endpoint;
        var separator = endpoint.Query.Length == 0 ? "?" : "&";
        return new Uri(endpoint.AbsoluteUri + separator + "cursor=" + Uri.EscapeDataString(cursor));
    }

    private static string? GetNextPageCursor(JsonElement root)
    {
        if (root.TryGetProperty("nextPageCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
            return cursor.GetString();

        // Keep compatibility with wrappers that nest pagination metadata inside data.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("nextPageCursor", out cursor) && cursor.ValueKind == JsonValueKind.String)
            return cursor.GetString();

        return null;
    }

    private async Task<Dictionary<string, RobloxMarketplaceItemData>> GetMarketplaceInBatchesAsync(
        string[] collectibleIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < collectibleIds.Length; offset += MaxMarketplaceBatchSize)
        {
            var batch = collectibleIds.Skip(offset).Take(MaxMarketplaceBatchSize).ToArray();
            var rows = await _marketplaceItems.GetManyAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (var pair in rows)
                result[pair.Key] = pair.Value;

            if (offset + MaxMarketplaceBatchSize < collectibleIds.Length)
                await Task.Delay(InterMarketplaceBatchDelay, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<JsonDocument> FetchDiscoveryAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRateLimitAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
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

            // Search is discovery-only. Some Roblox best-selling rows omit itemRestrictions and
            // other detail fields even when salesTypeFilter=2 is used. Do not throw away those IDs;
            // the hydrated detail parser below remains the strict Limited/Shop/price gate.
            var restrictions = GetStringArray(row, "itemRestrictions");
            if (restrictions.Length > 0 && !HasLimitedRestriction(row)) continue;

            var creatorId = TryGetInt64(row, "creatorTargetId", out var creator) ? creator : 0;
            if (creatorId == 1) continue;
            var assetType = TryGetInt32(row, "assetType", out var parsedAssetType) ? parsedAssetType : 0;
            if (assetType > 0 && !IsSupportedUgcAssetType(assetType)) continue;

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
