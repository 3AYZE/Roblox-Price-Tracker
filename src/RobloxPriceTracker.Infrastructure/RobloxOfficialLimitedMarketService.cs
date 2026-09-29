using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

public sealed record RobloxOfficialLimitedCatalogItem(
    long AssetId,
    string Name,
    long? FloorPrice,
    long? ListedPrice,
    long FavoriteCount,
    long? PurchaseCount,
    int AssetType,
    int DiscoveryStrength);

public sealed record RobloxOfficialLimitedMarketItem(
    long AssetId,
    string Name,
    long? FloorPrice,
    double? Rap,
    double Sales7d,
    double Sales30d,
    double? FairValue,
    double? DiscountPct,
    int HuntScore,
    int ValueScore,
    int FlipScore,
    int StabilityScore,
    int EvidenceScore,
    int UpsideScore,
    int RiskScore,
    string Status,
    string Trend,
    bool IsHuntCandidate,
    bool IsFresh,
    double? FloorChangePct,
    long FavoriteCount,
    long? PurchaseCount,
    int AssetType,
    bool IsEnriched,
    string EvidenceText,
    string ReasonText);

public sealed record RobloxOfficialLimitedScanResult(
    IReadOnlyList<RobloxOfficialLimitedMarketItem> Items,
    int DiscoveredCount,
    int EnrichedCount,
    DateTimeOffset ObservedAtUtc,
    string? Warning);

/// <summary>
/// Discovers Roblox-published Limiteds separately from UGC Hunter. Discovery is broad and cheap;
/// deeper resale enrichment is bounded to the strongest candidates so the app does not hammer Roblox.
/// </summary>
public sealed class RobloxOfficialLimitedMarketService
{
    // Live Roblox currently rejects Category=2 here with "Category subcategory selection not supported".
    // Category=1 + salesTypeFilter=2 is the verified Limited feed; the parser still hard-requires
    // creatorTargetId=1/User and a Limited/Collectible restriction before anything reaches the board.
    private static readonly string[] DiscoveryQueries =
    [
        "Category=1&salesTypeFilter=2&CreatorTargetId=1&CreatorType=User&SortType=2&SortAggregation=5&Limit=30",
        "Category=1&salesTypeFilter=2&CreatorTargetId=1&CreatorType=User&SortType=2&SortAggregation=3&Limit=30",
        "Category=1&salesTypeFilter=2&CreatorTargetId=1&CreatorType=User&SortType=2&SortAggregation=1&Limit=30"
    ];

    private const int PagesPerFeed = 3;
    private const int MaxDiscoveredItems = 180;
    private const int MaxEnrichedItems = 36;
    private const int MaxCatalogAttempts = 3;
    private static readonly TimeSpan InterRequestDelay = TimeSpan.FromMilliseconds(900);

    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly RobloxResaleDataService _resaleDataService;
    private readonly string? _statePath;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Dictionary<long, FloorHistoryEntry> _history = new();
    private bool _historyLoaded;

    public RobloxOfficialLimitedMarketService(
        HttpClient httpClient,
        AppLogger logger,
        RobloxResaleDataService resaleDataService,
        string? statePath = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _resaleDataService = resaleDataService;
        _statePath = statePath;
    }

    public async Task<RobloxOfficialLimitedScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureHistoryLoaded();
            var observedAt = DateTimeOffset.UtcNow;
            var discovered = new Dictionary<long, MutableCandidate>();
            string? warning = null;

            for (var feedIndex = 0; feedIndex < DiscoveryQueries.Length && discovered.Count < MaxDiscoveredItems; feedIndex++)
            {
                string? cursor = null;
                for (var page = 0; page < PagesPerFeed && discovered.Count < MaxDiscoveredItems; page++)
                {
                    try
                    {
                        var query = DiscoveryQueries[feedIndex];
                        if (!string.IsNullOrWhiteSpace(cursor))
                            query += "&Cursor=" + Uri.EscapeDataString(cursor);

                        using var document = await FetchSearchAsync(query, cancellationToken).ConfigureAwait(false);
                        var parsed = RobloxOfficialLimitedCatalogParser.Parse(document.RootElement);
                        var rank = 0;
                        foreach (var item in parsed.Items)
                        {
                            rank++;
                            var strength = Math.Max(1, 40 - rank) + Math.Max(0, 3 - feedIndex) * 5;
                            if (discovered.TryGetValue(item.AssetId, out var existing))
                            {
                                existing.DiscoveryStrength += strength;
                                if (item.FloorPrice is > 0) existing.FloorPrice = item.FloorPrice;
                                existing.FavoriteCount = Math.Max(existing.FavoriteCount, item.FavoriteCount);
                                existing.PurchaseCount = MaxNullable(existing.PurchaseCount, item.PurchaseCount);
                            }
                            else
                            {
                                discovered[item.AssetId] = new MutableCandidate(item with { DiscoveryStrength = strength });
                            }
                            if (discovered.Count >= MaxDiscoveredItems) break;
                        }

                        cursor = parsed.NextPageCursor;
                        if (string.IsNullOrWhiteSpace(cursor)) break;
                        await Task.Delay(InterRequestDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warning ??= "Some official Limited discovery pages could not be refreshed.";
                        _logger.Info($"Official Limited discovery feed {feedIndex + 1} page {page + 1} stopped: {ex.Message}");
                        break;
                    }
                }

                if (feedIndex + 1 < DiscoveryQueries.Length && discovered.Count < MaxDiscoveredItems)
                    await Task.Delay(InterRequestDelay, cancellationToken).ConfigureAwait(false);
            }

            var catalogItems = discovered.Values
                .Select(x => x.ToRecord())
                .OrderByDescending(x => x.DiscoveryStrength)
                .ThenByDescending(x => x.FavoriteCount)
                .ToArray();

            if (catalogItems.Length == 0)
            {
                warning = warning is null
                    ? "Roblox returned no official Limited rows. Try Refresh again in a moment."
                    : "Official Limited discovery is temporarily unavailable or rate-limited. Try Refresh again in a moment.";
            }

            var enrichmentTargets = catalogItems
                .Where(x => x.FloorPrice is > 0)
                .Take(MaxEnrichedItems)
                .ToArray();

            IReadOnlyDictionary<long, RobloxResaleMarketData> resale = new Dictionary<long, RobloxResaleMarketData>();
            if (enrichmentTargets.Length > 0)
            {
                try
                {
                    resale = await _resaleDataService.GetManyAsync(enrichmentTargets.Select(x => x.AssetId), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warning ??= "Official Limited resale enrichment is temporarily incomplete.";
                    _logger.Error($"Official Limited resale enrichment failed: {ex.Message}");
                }
            }

            var enrichedIds = enrichmentTargets.Select(x => x.AssetId).ToHashSet();
            var result = new List<RobloxOfficialLimitedMarketItem>(catalogItems.Length);
            foreach (var item in catalogItems)
            {
                _history.TryGetValue(item.AssetId, out var history);
                resale.TryGetValue(item.AssetId, out var market);
                var row = enrichedIds.Contains(item.AssetId)
                    ? OfficialLimitedHuntScoring.Evaluate(item, market, history?.LastFloor, history is null)
                    : OfficialLimitedHuntScoring.Unenriched(item, history?.LastFloor, history is null);
                result.Add(row);

                if (item.FloorPrice is > 0)
                {
                    _history[item.AssetId] = history is null
                        ? new FloorHistoryEntry(item.AssetId, item.FloorPrice.Value, item.FloorPrice.Value, observedAt, observedAt)
                        : history with
                        {
                            LastFloor = item.FloorPrice.Value,
                            LowestFloor = Math.Min(history.LowestFloor, item.FloorPrice.Value),
                            LastSeenUtc = observedAt
                        };
                }
            }

            PersistHistory();
            return new RobloxOfficialLimitedScanResult(
                result.OrderByDescending(x => x.HuntScore).ThenByDescending(x => x.EvidenceScore).ToArray(),
                catalogItems.Length,
                enrichedIds.Count,
                observedAt,
                warning);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<JsonDocument> FetchSearchAsync(string query, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://catalog.roblox.com/v1/search/items/details?" + query);

        for (var attempt = 0; attempt < MaxCatalogAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt + 1 < MaxCatalogAttempts)
            {
                var delay = GetRetryDelay(response.Headers.RetryAfter, attempt);
                _logger.Info($"Official Limited catalog rate-limited; retrying in {delay.TotalSeconds:0.#}s (attempt {attempt + 2}/{MaxCatalogAttempts}).");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException($"Roblox catalog search returned HTTP {(int)response.StatusCode}.");
        }

        throw new HttpRequestException("Roblox catalog search retry budget was exhausted.");
    }

    internal static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        TimeSpan delay;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(5);
        }
        else
        {
            delay = TimeSpan.FromSeconds(5);
        }

        // Add a small cushion and increase it on repeated throttles. Keep the UI bounded.
        var seconds = Math.Clamp(delay.TotalSeconds + 1 + attempt * 3, 2, 15);
        return TimeSpan.FromSeconds(seconds);
    }

    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        if (string.IsNullOrWhiteSpace(_statePath) || !File.Exists(_statePath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<FloorHistoryEntry>>(File.ReadAllText(_statePath)) ?? [];
            _history = saved.Where(x => x.AssetId > 0 && x.LastFloor > 0).ToDictionary(x => x.AssetId);
        }
        catch (Exception ex)
        {
            _logger.Info($"Official Limited floor history could not be loaded: {ex.Message}");
        }
    }

    private void PersistHistory()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(180);
            var values = _history.Values.Where(x => x.LastSeenUtc >= cutoff).OrderBy(x => x.AssetId).ToArray();
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(values));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Info($"Official Limited floor history could not be saved: {ex.Message}");
        }
    }

    private static long? MaxNullable(long? left, long? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private sealed class MutableCandidate
    {
        public MutableCandidate(RobloxOfficialLimitedCatalogItem item)
        {
            AssetId = item.AssetId;
            Name = item.Name;
            FloorPrice = item.FloorPrice;
            ListedPrice = item.ListedPrice;
            FavoriteCount = item.FavoriteCount;
            PurchaseCount = item.PurchaseCount;
            AssetType = item.AssetType;
            DiscoveryStrength = item.DiscoveryStrength;
        }

        public long AssetId { get; }
        public string Name { get; }
        public long? FloorPrice { get; set; }
        public long? ListedPrice { get; }
        public long FavoriteCount { get; set; }
        public long? PurchaseCount { get; set; }
        public int AssetType { get; }
        public int DiscoveryStrength { get; set; }

        public RobloxOfficialLimitedCatalogItem ToRecord() => new(AssetId, Name, FloorPrice, ListedPrice, FavoriteCount, PurchaseCount, AssetType, DiscoveryStrength);
    }

    public sealed record FloorHistoryEntry(long AssetId, long LastFloor, long LowestFloor, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc);
}

public sealed record RobloxOfficialLimitedCatalogParseResult(
    IReadOnlyList<RobloxOfficialLimitedCatalogItem> Items,
    string? NextPageCursor);

public static class RobloxOfficialLimitedCatalogParser
{
    private static readonly HashSet<string> LimitedMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Limited", "LimitedUnique", "Collectible"
    };

    public static RobloxOfficialLimitedCatalogParseResult Parse(JsonElement root)
    {
        var items = new List<RobloxOfficialLimitedCatalogItem>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new RobloxOfficialLimitedCatalogParseResult(items, ReadCursor(root));

        foreach (var node in data.EnumerateArray())
        {
            if (!TryInt64(node, "id", out var id) || id <= 0) continue;
            if (!string.Equals(ReadString(node, "itemType"), "Asset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryInt64(node, "creatorTargetId", out var creatorId) || creatorId != 1) continue;
            if (!string.Equals(ReadString(node, "creatorType"), "User", StringComparison.OrdinalIgnoreCase)) continue;
            var restrictions = ReadStrings(node, "itemRestrictions");
            if (!restrictions.Any(x => LimitedMarkers.Contains(x))) continue;

            long? floor = TryInt64(node, "lowestPrice", out var floorValue) && floorValue > 0 ? floorValue : null;
            long? listed = TryInt64(node, "price", out var priceValue) && priceValue > 0 ? priceValue : null;
            var favorites = TryInt64(node, "favoriteCount", out var favoriteValue) && favoriteValue > 0 ? favoriteValue : 0;
            long? purchases = TryInt64(node, "purchaseCount", out var purchaseValue) && purchaseValue >= 0 ? purchaseValue : null;
            var assetType = TryInt64(node, "assetType", out var assetTypeValue) ? (int)Math.Clamp(assetTypeValue, 0, int.MaxValue) : 0;
            items.Add(new RobloxOfficialLimitedCatalogItem(id, ReadString(node, "name") ?? $"Asset {id}", floor, listed, favorites, purchases, assetType, 0));
        }

        return new RobloxOfficialLimitedCatalogParseResult(items, ReadCursor(root));
    }

    private static string? ReadCursor(JsonElement root) =>
        root.TryGetProperty("nextPageCursor", out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static string? ReadString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryInt64(JsonElement node, string property, out long value)
    {
        value = 0;
        return node.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static string[] ReadStrings(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
    }
}

public static class OfficialLimitedHuntScoring
{
    public static RobloxOfficialLimitedMarketItem Unenriched(RobloxOfficialLimitedCatalogItem item, long? previousFloor = null, bool isFresh = false)
    {
        var floorChange = CalculateChange(item.FloorPrice, previousFloor);
        return new RobloxOfficialLimitedMarketItem(
            item.AssetId, item.Name, item.FloorPrice, null, 0, 0, null, null,
            0, 0, 0, 0, 10, 0, 75, "MARKET", "—", false, isFresh, floorChange,
            item.FavoriteCount, item.PurchaseCount, item.AssetType, false,
            "Catalog verified · resale analytics pending",
            "Available in the Official Market browser; deeper Hunt evidence is only fetched for a bounded candidate set.");
    }

    public static RobloxOfficialLimitedMarketItem Evaluate(
        RobloxOfficialLimitedCatalogItem item,
        RobloxResaleMarketData? market,
        long? previousFloor = null,
        bool isFresh = false)
    {
        var floor = item.FloorPrice;
        var rap = market?.RecentAveragePrice is > 0 ? market.RecentAveragePrice : null;
        var validPrices = market?.PriceDataPoints.Where(x => x.Value > 0).OrderBy(x => x.Date).ToArray() ?? [];
        var recentPrices = validPrices.Where(x => x.Date >= (validPrices.LastOrDefault()?.Date ?? DateTimeOffset.UtcNow) - TimeSpan.FromDays(30)).Select(x => x.Value).ToArray();
        var recentMedian = Median(recentPrices);
        var fair = ConservativeFairValue(rap, recentMedian);
        double? discount = floor is > 0 && fair is > 0 ? (fair.Value - floor.Value) / fair.Value : null;
        var sales7 = SumRecent(market?.VolumeDataPoints, 7.25);
        var sales30 = SumRecent(market?.VolumeDataPoints, 30.25);
        var evidence = EvidenceScore(market, rap, recentPrices.Length);
        var liquidity = LiquidityScore(sales7);
        var stability = StabilityScore(recentPrices);
        var value = ValueScore(discount);
        var upside = Clamp((int)Math.Round(value * .55 + evidence * .25 + liquidity * .20));
        var flip = Clamp((int)Math.Round(liquidity * .50 + evidence * .20 + stability * .15 + value * .15));
        var hunt = Clamp((int)Math.Round(value * .35 + liquidity * .25 + stability * .15 + evidence * .25));

        if (discount is null || discount <= 0) hunt = Math.Min(hunt, 35);
        if (sales7 < 1) hunt = Math.Min(hunt, 45);
        if (evidence < 50) hunt = Math.Min(hunt, 50);
        if (discount is > .50 && sales7 < 10) hunt = Math.Min(hunt, 55);

        var risk = Clamp(100 - (int)Math.Round(evidence * .35 + liquidity * .30 + stability * .35));
        var isCandidate = hunt >= 60 && discount is >= .05 && evidence >= 50 && sales7 >= 1;
        var floorChange = CalculateChange(floor, previousFloor);
        var trend = Trend(validPrices);
        var status = Status(hunt, isCandidate, isFresh, floorChange, floor, fair);
        var evidenceText = $"RAP {(rap is > 0 ? "yes" : "no")} · price series {recentPrices.Length} pts · 7d sales {sales7:0.#} · 30d sales {sales30:0.#}";
        var reason = BuildReason(discount, sales7, stability, evidence, floorChange);

        return new RobloxOfficialLimitedMarketItem(
            item.AssetId, item.Name, floor, rap, sales7, sales30, fair, discount,
            hunt, value, flip, stability, evidence, upside, risk, status, trend, isCandidate, isFresh, floorChange,
            item.FavoriteCount, item.PurchaseCount, item.AssetType, true, evidenceText, reason);
    }

    private static string Status(int hunt, bool candidate, bool fresh, double? floorChange, long? floor, double? fair)
    {
        if (candidate && floorChange is <= -.10) return "PRICE DROP";
        if (candidate && fresh) return "NEW DEAL";
        if (hunt >= 80) return "BEST BUY";
        if (hunt >= 70) return "BUY ZONE";
        if (hunt >= 55) return "WATCH";
        if (floor is > 0 && fair is > 0 && floor.Value > fair.Value * 1.08) return "OVERPRICED";
        return "MARKET";
    }

    private static string BuildReason(double? discount, double sales7, int stability, int evidence, double? floorChange)
    {
        var parts = new List<string>();
        if (discount is >= .10) parts.Add($"floor is {discount.Value:P0} below conservative fair value");
        else if (discount is > 0) parts.Add($"floor is {discount.Value:P0} below fair value");
        else parts.Add("no verified discount to fair value");
        parts.Add(sales7 >= 14 ? "strong weekly liquidity" : sales7 >= 4 ? "usable weekly liquidity" : "thin weekly liquidity");
        parts.Add(stability >= 70 ? "stable recent pricing" : "volatile recent pricing");
        if (evidence < 60) parts.Add("limited evidence");
        if (floorChange is <= -.10) parts.Add($"floor dropped {Math.Abs(floorChange.Value):P0} since the prior scan");
        return string.Join(" · ", parts);
    }

    private static double? ConservativeFairValue(double? rap, double? median)
    {
        if (rap is > 0 && median is > 0) return Math.Min(rap.Value, median.Value);
        return rap is > 0 ? rap : median is > 0 ? median : null;
    }

    private static int EvidenceScore(RobloxResaleMarketData? market, double? rap, int recentPriceCount)
    {
        var score = 10;
        if (market?.IsAvailable == true) score += 20;
        if (rap is > 0) score += 25;
        if (recentPriceCount >= 7) score += 25;
        else if (recentPriceCount >= 3) score += 15;
        if (market?.VolumeDataPoints.Count(x => x.Value >= 0) >= 7) score += 20;
        return Clamp(score);
    }

    private static int ValueScore(double? discount)
    {
        if (discount is null || discount <= 0) return 0;
        return Clamp((int)Math.Round(discount.Value * 320));
    }

    private static int LiquidityScore(double sales7) => sales7 switch
    {
        >= 70 => 100,
        >= 35 => 92,
        >= 14 => 80,
        >= 7 => 68,
        >= 3 => 52,
        >= 1 => 35,
        _ => 0
    };

    private static int StabilityScore(double[] values)
    {
        if (values.Length < 3) return 35;
        var mean = values.Average();
        if (mean <= 0) return 35;
        var variance = values.Sum(x => Math.Pow(x - mean, 2)) / values.Length;
        var cv = Math.Sqrt(variance) / mean;
        return Clamp((int)Math.Round(100 - cv * 260));
    }

    private static string Trend(IReadOnlyList<ResaleDataPoint> points)
    {
        if (points.Count < 6) return "—";
        var recent = points.TakeLast(Math.Min(7, points.Count)).Select(x => x.Value).Average();
        var priorSet = points.Skip(Math.Max(0, points.Count - 14)).Take(Math.Min(7, Math.Max(0, points.Count - 7))).Select(x => x.Value).ToArray();
        if (priorSet.Length == 0) return "—";
        var prior = priorSet.Average();
        if (prior <= 0) return "—";
        var change = (recent - prior) / prior;
        return change >= .05 ? "RISING" : change <= -.05 ? "FALLING" : "STABLE";
    }

    private static double SumRecent(IReadOnlyList<ResaleDataPoint>? points, double days)
    {
        if (points is null || points.Count == 0) return 0;
        var valid = points.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
        if (valid.Length == 0) return 0;
        var newest = valid[0].Date;
        return valid.Where(x => x.Date >= newest - TimeSpan.FromDays(days)).Sum(x => x.Value);
    }

    private static double? Median(IEnumerable<double> values)
    {
        var data = values.Where(x => x > 0 && !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToArray();
        if (data.Length == 0) return null;
        var middle = data.Length / 2;
        return data.Length % 2 == 1 ? data[middle] : (data[middle - 1] + data[middle]) / 2d;
    }

    private static double? CalculateChange(long? current, long? previous) =>
        current is > 0 && previous is > 0 ? (current.Value - previous.Value) / (double)previous.Value : null;

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);
}
