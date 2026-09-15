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
/// UGC Limited discovery with bounded multi-window momentum scanning and defensive compatibility
/// fallbacks. Roblox's legacy catalog V1 search surface is useful but not actively maintained, so
/// Hunter never treats one fragile category route as mandatory. Collectibles (Category=2) is the
/// primary V1 bucket, Category=1 is the protocol fallback, and CommunityCreations is supplemental.
/// Search rows only discover IDs; hydrated catalog and Marketplace Item data remain authoritative.
/// </summary>
public sealed class RobloxUgcDiscoveryService
{
    private sealed record DiscoveryFeed(
        string Name,
        Uri PrimaryEndpoint,
        Uri? FallbackEndpoint,
        int MaxPages,
        int Budget,
        double Weight,
        bool ReserveRecent = false);

    private sealed class DiscoverySignal
    {
        private readonly HashSet<string> _feeds = new(StringComparer.OrdinalIgnoreCase);

        public DiscoverySignal(long assetId) => AssetId = assetId;

        public long AssetId { get; }
        public double Score { get; private set; }
        public int BestRank { get; private set; } = int.MaxValue;
        public int FeedHits => _feeds.Count;

        public void Observe(string feedName, int rank, double weight)
        {
            if (!_feeds.Add(feedName)) return;
            BestRank = Math.Min(BestRank, rank);
            var rankStrength = Math.Clamp(1d - (rank - 1d) / 120d, 0.08d, 1d);
            Score += weight * rankStrength * 100d;
        }

        public double CompositeScore => Score + Math.Max(0, FeedHits - 1) * 18d;
    }

    private sealed record DiscoveryIdSet(long[] Ids, int FeedsUsed, int PagesUsed, bool FromCache);
    private sealed record HydrationResult(IReadOnlyList<RobloxUgcCatalogCandidate> Items, int Rows, int Requests);

    private static readonly DiscoveryFeed[] DiscoveryFeeds =
    [
        new(
            "collectibles-sales-day",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=2&salesTypeFilter=2&SortType=2&SortAggregation=1&Limit=30"),
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=1&Limit=30"),
            MaxPages: 2,
            Budget: 60,
            Weight: 1.35d),
        new(
            "collectibles-sales-week",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=2&salesTypeFilter=2&SortType=2&SortAggregation=3&Limit=30"),
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=3&Limit=30"),
            MaxPages: 3,
            Budget: 90,
            Weight: 1.50d),
        new(
            "collectibles-sales-month",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=2&salesTypeFilter=2&SortType=2&SortAggregation=4&Limit=30"),
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=2&SortAggregation=4&Limit=30"),
            MaxPages: 2,
            Budget: 60,
            Weight: 1.05d),
        new(
            "community-sales-week",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=13&salesTypeFilter=2&SortType=2&SortAggregation=3&Limit=30"),
            null,
            MaxPages: 3,
            Budget: 90,
            Weight: 1.10d),
        new(
            "recent-collectibles",
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=2&salesTypeFilter=2&SortType=3&Limit=30"),
            new Uri("https://catalog.roblox.com/v1/search/items/details?Category=1&salesTypeFilter=2&SortType=3&Limit=30"),
            MaxPages: 1,
            Budget: 30,
            Weight: 0.20d,
            ReserveRecent: true)
    ];

    private static readonly Uri DetailsEndpoint = new("https://catalog.roblox.com/v1/catalog/items/details");

    private const int MaxDiscoveryIds = 120;
    private const int MomentumQuota = 100;
    private const int RecentReserve = MaxDiscoveryIds - MomentumQuota;
    private const int MaxDetailBatchSize = 40;
    private const int DetailIsolationChunkSize = 10;
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

        await Task.Delay(
            discovery.FromCache ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(500),
            cancellationToken).ConfigureAwait(false);

        var catalogCandidates = new List<RobloxUgcCatalogCandidate>(discovery.Ids.Length);
        var hydratedRows = 0;
        var detailRequests = 0;

        foreach (var batch in discovery.Ids.Chunk(MaxDetailBatchSize))
        {
            try
            {
                var hydrated = await HydrateBatchResilientAsync(batch, cancellationToken).ConfigureAwait(false);
                catalogCandidates.AddRange(hydrated.Items);
                hydratedRows += hydrated.Rows;
                detailRequests += hydrated.Requests;
            }
            catch (RobloxUgcDiscoveryException ex) when (catalogCandidates.Count > 0)
            {
                _logger.Info($"UGC Hunter kept {catalogCandidates.Count} hydrated candidates after a later detail batch failed: {ex.Message}");
                break;
            }

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

            if (candidate.UnitsAvailable is > 0)
                candidates.Add(candidate);
            else
                rejectedUnavailable++;
        }

        _logger.Info(
            $"UGC Hunter resilient momentum discovery: {discovery.FeedsUsed} feed(s), {discovery.PagesUsed} page(s), " +
            $"{discovery.Ids.Length} selected IDs{(discovery.FromCache ? " (cached discovery)" : string.Empty)}, " +
            $"{detailRequests} detail request(s), {hydratedRows} catalog rows, {verified} marketplace-verified, " +
            $"{rejectedUnavailable} unavailable rejected, {candidates.Count} active candidates.");

        return new RobloxUgcDiscoveryResult(candidates, discovery.Ids.Length, hydratedRows);
    }

    private async Task<HydrationResult> HydrateBatchResilientAsync(long[] batch, CancellationToken cancellationToken)
    {
        try
        {
            return await HydrateOnceAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (RobloxUgcDiscoveryException ex) when (
            ex.Kind == RobloxUgcDiscoveryFailureKind.Protocol &&
            ex.StatusCode == (int)HttpStatusCode.BadRequest &&
            batch.Length > DetailIsolationChunkSize)
        {
            var recovered = new List<RobloxUgcCatalogCandidate>();
            var rows = 0;
            var requests = 1;
            var successes = 0;

            foreach (var chunk in batch.Chunk(DetailIsolationChunkSize))
            {
                try
                {
                    var partial = await HydrateOnceAsync(chunk, cancellationToken).ConfigureAwait(false);
                    recovered.AddRange(partial.Items);
                    rows += partial.Rows;
                    requests += partial.Requests;
                    successes++;
                }
                catch (RobloxUgcDiscoveryException child) when (
                    child.Kind == RobloxUgcDiscoveryFailureKind.Protocol &&
                    child.StatusCode == (int)HttpStatusCode.BadRequest)
                {
                    requests++;
                    _logger.Info(
                        $"UGC Hunter skipped a {chunk.Length}-ID hydration chunk after Roblox HTTP 400. " +
                        $"First asset: {chunk[0]}.");
                }
            }

            if (successes == 0)
                throw;

            _logger.Info($"UGC Hunter recovered {recovered.Count} candidates from a partially invalid detail batch.");
            return new HydrationResult(recovered, rows, requests);
        }
    }

    private async Task<HydrationResult> HydrateOnceAsync(long[] ids, CancellationToken cancellationToken)
    {
        using var document = await FetchDetailsAsync(ids, cancellationToken).ConfigureAwait(false);
        var rows = 0;
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            rows = data.GetArrayLength();
        var parsed = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(document.RootElement);
        return new HydrationResult(parsed, rows, 1);
    }

    private async Task<DiscoveryIdSet> GetDiscoveryIdsAsync(CancellationToken cancellationToken)
    {
        if (!_expandDiscovery)
            return await DiscoverIdsFromFeedsAsync(DiscoveryFeeds.Take(1).ToArray(), cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        if (_cachedDiscovery is { } cached && _cachedDiscoveryExpiresAtUtc > now)
            return cached with { FromCache = true };

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedDiscovery is { } insideCached && _cachedDiscoveryExpiresAtUtc > now)
                return insideCached with { FromCache = true };

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

        foreach (var feed in feeds)
        {
            var activeEndpoint = feed.PrimaryEndpoint;
            string? cursor = null;
            var rowsConsidered = 0;
            var producedPage = false;

            for (var page = 0; page < feed.MaxPages && rowsConsidered < feed.Budget; page++)
            {
                JsonDocument document;
                try
                {
                    document = await FetchDiscoveryAsync(BuildPageUri(activeEndpoint, cursor), cancellationToken).ConfigureAwait(false);
                }
                catch (RobloxUgcDiscoveryException ex) when (
                    page == 0 &&
                    feed.FallbackEndpoint is not null &&
                    ex.Kind == RobloxUgcDiscoveryFailureKind.Protocol)
                {
                    firstFailure ??= ex;
                    activeEndpoint = feed.FallbackEndpoint;
                    cursor = null;
                    _logger.Info(
                        $"UGC Hunter {feed.Name} primary route returned protocol error; retrying with broad Collectibles fallback. " +
                        ex.Message);
                    try
                    {
                        document = await FetchDiscoveryAsync(activeEndpoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (RobloxUgcDiscoveryException fallbackEx)
                    {
                        firstFailure ??= fallbackEx;
                        _logger.Info($"UGC Hunter skipped {feed.Name}: fallback failed: {fallbackEx.Message}");
                        break;
                    }
                }
                catch (RobloxUgcDiscoveryException ex)
                {
                    firstFailure ??= ex;
                    _logger.Info($"UGC Hunter discovery skipped {feed.Name} page {page + 1}: {ex.Message}");
                    break;
                }

                using (document)
                {
                    producedPage = true;
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
                        if (feed.ReserveRecent && recentSeen.Add(id)) recentIds.Add(id);
                    }

                    cursor = GetNextPageCursor(document.RootElement);
                }

                if (string.IsNullOrWhiteSpace(cursor) || rowsConsidered >= feed.Budget)
                    break;

                await Task.Delay(InterPageDelay, cancellationToken).ConfigureAwait(false);
            }

            if (producedPage) feedsUsed++;
            await Task.Delay(InterFeedDelay, cancellationToken).ConfigureAwait(false);
        }

        if (signals.Count == 0 && firstFailure is not null)
            throw firstFailure;

        var selected = new List<long>(MaxDiscoveryIds);
        var selectedSet = new HashSet<long>();
        var ordered = signals.Values
            .OrderByDescending(x => x.CompositeScore)
            .ThenByDescending(x => x.FeedHits)
            .ThenBy(x => x.BestRank)
            .ToArray();

        foreach (var signal in ordered.Take(MomentumQuota))
        {
            if (selectedSet.Add(signal.AssetId)) selected.Add(signal.AssetId);
        }

        foreach (var id in recentIds)
        {
            if (selected.Count >= MaxDiscoveryIds) break;
            if (selectedSet.Add(id)) selected.Add(id);
        }

        foreach (var signal in ordered)
        {
            if (selected.Count >= MaxDiscoveryIds) break;
            if (selectedSet.Add(signal.AssetId)) selected.Add(signal.AssetId);
        }

        return new DiscoveryIdSet(selected.ToArray(), feedsUsed, pagesUsed, FromCache: false);
    }

    private static Uri BuildPageUri(Uri endpoint, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return endpoint;
        var separator = endpoint.Query.Length == 0 ? "?" : "&";
        return new Uri(endpoint.AbsoluteUri + separator + "Cursor=" + Uri.EscapeDataString(cursor));
    }

    private static string? GetNextPageCursor(JsonElement root)
    {
        if (root.TryGetProperty("nextPageCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
            return cursor.GetString();
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
            foreach (var pair in rows) result[pair.Key] = pair.Value;
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
                throw new RobloxUgcDiscoveryException(
                    RobloxUgcDiscoveryFailureKind.Offline,
                    $"Roblox UGC catalog search failed: {ex.Message}",
                    innerException: ex);
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
                await EnsureSuccessAsync(response, $"UGC catalog search ({endpoint.AbsolutePath})", cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new RobloxUgcDiscoveryException(
                        RobloxUgcDiscoveryFailureKind.Protocol,
                        $"Roblox UGC search returned malformed JSON: {ex.Message}",
                        innerException: ex);
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
                    throw new RobloxUgcDiscoveryException(
                        RobloxUgcDiscoveryFailureKind.Protocol,
                        $"Roblox UGC item details returned malformed JSON: {ex.Message}",
                        innerException: ex);
                }
            }
        }

        throw new InvalidOperationException("UGC detail retry loop ended unexpectedly.");
    }

    private async Task<HttpResponseMessage> SendDetailsWithCsrfHandshakeAsync(long[] ids, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendDetailsRequestAsync(ids, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var token))
            {
                response.Dispose();
                _anonymousCsrfToken = token;
                response = await SendDetailsRequestAsync(ids, token, cancellationToken).ConfigureAwait(false);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RobloxUgcDiscoveryException(RobloxUgcDiscoveryFailureKind.Offline, "Roblox UGC item detail request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new RobloxUgcDiscoveryException(
                RobloxUgcDiscoveryFailureKind.Offline,
                $"Roblox UGC item detail request failed: {ex.Message}",
                innerException: ex);
        }
    }

    private async Task<HttpResponseMessage> SendDetailsRequestAsync(
        long[] ids,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        var payload = new { items = ids.Select(id => new { itemType = "Asset", id }).ToArray() };
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

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
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

    private static async Task<string?> ReadResponseDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
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
            if (!string.Equals(itemType, "Asset", StringComparison.OrdinalIgnoreCase)) continue;

            var restrictions = GetStringArray(row, "itemRestrictions");
            if (restrictions.Length > 0 && !HasLimitedRestriction(row)) continue;

            var creatorId = TryGetInt64(row, "creatorTargetId", out var creator) ? creator : 0;
            if (creatorId == 1) continue;

            var assetType = TryGetInt32(row, "assetType", out var parsedAssetType) ? parsedAssetType : 0;
            if (assetType > 0 && !IsSupportedUgcAssetType(assetType)) continue;
            result.Add(id);
        }
        return result;
    }

    public static IReadOnlyList<RobloxUgcCatalogCandidate> ParseHydratedCandidates(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<RobloxUgcCatalogCandidate>();

        var result = new List<RobloxUgcCatalogCandidate>();
        foreach (var row in data.EnumerateArray())
        {
            if (TryParseHydratedCandidate(row, out var candidate)) result.Add(candidate);
        }
        return result;
    }

    private static bool TryParseHydratedCandidate(JsonElement row, out RobloxUgcCatalogCandidate candidate)
    {
        candidate = default!;
        if (!TryGetInt64(row, "id", out var id) || id <= 0) return false;
        if (!TryGetInt32(row, "price", out var price) || price <= 0) return false;
        if (!string.Equals(GetString(row, "itemType"), "Asset", StringComparison.OrdinalIgnoreCase)) return false;
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
             priceStatus.Equals("Free", StringComparison.OrdinalIgnoreCase)))
            return false;

        var saleLocationType = GetString(row, "saleLocationType");
        if (string.IsNullOrWhiteSpace(saleLocationType) ||
            !saleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase))
            return false;

        long? unitsAvailable = TryGetInt64(row, "unitsAvailableForConsumption", out var units)
            ? Math.Max(0, units)
            : null;
        long? totalQuantity = TryGetInt64(row, "totalQuantity", out var total) && total > 0
            ? total
            : null;
        var purchases = TryGetInt64(row, "purchaseCount", out var purchaseCount)
            ? Math.Max(0, purchaseCount)
            : 0;
        if (totalQuantity is { } knownTotal && unitsAvailable is { } remaining)
            purchases = Math.Max(purchases, Math.Max(0, knownTotal - remaining));

        var favorites = TryGetInt64(row, "favoriteCount", out var favoriteCount)
            ? Math.Max(0, favoriteCount)
            : 0;

        candidate = new RobloxUgcCatalogCandidate(
            id,
            GetString(row, "name") ?? $"Asset {id}",
            GetString(row, "creatorName") ?? "Unknown creator",
            creatorId,
            assetType,
            CategoryName(assetType),
            price,
            purchases,
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
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var token) &&
               token.ValueKind == JsonValueKind.Number &&
               token.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var token) &&
               token.ValueKind == JsonValueKind.Number &&
               token.TryGetInt64(out value);
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
