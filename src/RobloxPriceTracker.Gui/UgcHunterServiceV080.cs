namespace RobloxPriceTracker.Gui;

/// <summary>
/// v0.8 Hunter implementation. The scan is still anonymous/catalog based, but ranking is
/// driven by estimated post-sellout resale economics. Only the strongest modeled candidates
/// are enriched with Roblox resale aggregates and reseller-book depth so normal refreshes do
/// not fan out into unbounded marketplace requests.
/// </summary>
public sealed class UgcHunterService
{
    private static readonly string[] SearchEndpoints =
    [
        // Collectibles is the authoritative marketplace bucket for Limited / collectible items.
        // Starting here avoids losing current UGC Limiteds behind the much broader Accessories feed.
        "https://catalog.roblox.com/v1/search/items/details?Category=2&SortType=3&Limit=30",
        "https://catalog.roblox.com/v1/search/items/details?Category=2&SortType=2&SortAggregation=1&Limit=30",
        // Broader community/accessory/clothing routes are fallbacks for Roblox catalog drift.
        "https://catalog.roblox.com/v1/search/items/details?Category=13&SortType=3&Limit=30",
        "https://catalog.roblox.com/v1/search/items/details?Category=11&Subcategory=19&SortType=3&Limit=30",
        "https://catalog.roblox.com/v1/search/items/details?Category=3&SortType=3&Limit=30",
        "https://catalog.roblox.com/v1/search/items/details?Category=1&SortType=3&Limit=30"
    ];

    private const int MaxPagesPerScan = 3;
    private const int ResaleEnrichmentLimit = 8;
    private const int PreferredCandidateCount = 8;
    private readonly HttpClient _httpClient;
    private readonly RobloxUgcDiscoveryService _discoveryService;
    private readonly RobloxThumbnailService _thumbnailService;
    private readonly RobloxResaleDataService _resaleDataService;
    private readonly RobloxResellerDataService _resellerDataService;
    private readonly AppLogger _logger;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, List<UgcHunterObservation>> _history = new();
    private bool _initialized;

    public UgcHunterService(HttpClient httpClient, RobloxThumbnailService thumbnailService, AppLogger logger, string dataDirectory)
    {
        _httpClient = httpClient;
        _discoveryService = new RobloxUgcDiscoveryService(httpClient, logger);
        _thumbnailService = thumbnailService;
        _resaleDataService = new RobloxResaleDataService(httpClient, logger);
        _resellerDataService = new RobloxResellerDataService(httpClient, logger);
        _logger = logger;
        _historyPath = Path.Combine(dataDirectory, "ugc-hunter-history.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            if (File.Exists(_historyPath))
            {
                try
                {
                    await using var stream = File.OpenRead(_historyPath);
                    var persisted = await JsonSerializer.DeserializeAsync<List<UgcHunterObservation>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                        ?? new List<UgcHunterObservation>();
                    foreach (var group in persisted.GroupBy(x => x.AssetId))
                        _history[group.Key] = group.OrderBy(x => x.ObservedAtUtc).TakeLast(360).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Error($"UGC Hunter history could not be loaded: {ex.Message}");
                }
            }
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UgcHunterMarketSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var raw = await FetchCandidatesAsync(now, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<long, string> thumbnails;
        try
        {
            thumbnails = await _thumbnailService.GetAssetThumbnailUrlsAsync(raw.Select(x => x.AssetId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"UGC Hunter thumbnails could not be loaded: {ex.Message}");
            thumbnails = new Dictionary<long, string>();
        }

        var baseline = new List<UgcHunterItem>(raw.Count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var candidate in raw)
            {
                if (!_history.TryGetValue(candidate.AssetId, out var observations))
                {
                    observations = new List<UgcHunterObservation>();
                    _history[candidate.AssetId] = observations;
                }

                var observation = new UgcHunterObservation(
                    candidate.AssetId,
                    candidate.PurchaseCount,
                    candidate.UnitsAvailable,
                    candidate.Price,
                    candidate.FavoriteCount,
                    now);

                if (observations.Count == 0 || now - observations[^1].ObservedAtUtc >= TimeSpan.FromSeconds(20))
                    observations.Add(observation);
                else
                    observations[^1] = observation;

                observations.RemoveAll(x => now - x.ObservedAtUtc > TimeSpan.FromHours(24));
                if (observations.Count > 360) observations.RemoveRange(0, observations.Count - 360);

                thumbnails.TryGetValue(candidate.AssetId, out var thumbnail);
                baseline.Add(Analyze(candidate, observations, thumbnail));
            }

            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var enriched = await EnrichResaleEvidenceAsync(baseline, cancellationToken).ConfigureAwait(false);
        var ranked = enriched
            .OrderByDescending(x => x.ResalePotentialScore)
            .ThenByDescending(x => x.EntryScore)
            .ThenBy(x => x.SelloutEta ?? TimeSpan.MaxValue)
            .ToArray();

        return new UgcHunterMarketSnapshot(ranked, BuildMarketState(ranked), now);
    }

    private async Task<IReadOnlyList<UgcHunterItem>> EnrichResaleEvidenceAsync(
        IReadOnlyList<UgcHunterItem> baseline,
        CancellationToken cancellationToken)
    {
        if (baseline.Count == 0) return baseline;

        var selected = baseline
            .OrderByDescending(x => x.ResalePotentialScore)
            .ThenByDescending(x => x.EntryScore)
            .Take(ResaleEnrichmentLimit)
            .ToArray();

        var tasks = selected.Select(async item =>
        {
            try
            {
                var market = !string.IsNullOrWhiteSpace(item.CollectibleItemId)
                    ? await _resaleDataService.GetModernAsync(item.AssetId, item.CollectibleItemId, cancellationToken).ConfigureAwait(false)
                    : await _resaleDataService.GetAsync(item.AssetId, cancellationToken).ConfigureAwait(false);
                RobloxResellerMarketData? book = null;
                var collectibleId = market.CollectibleItemId ?? item.CollectibleItemId;
                if (!string.IsNullOrWhiteSpace(collectibleId))
                    book = await _resellerDataService.GetAsync(item.AssetId, collectibleId, cancellationToken).ConfigureAwait(false);
                return ApplyResaleEvidence(item, market, book);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"UGC Hunter resale enrichment failed for asset {item.AssetId}: {ex.Message}");
                return item;
            }
        });

        var enriched = await Task.WhenAll(tasks).ConfigureAwait(false);
        var byId = enriched.ToDictionary(x => x.AssetId);
        return baseline.Select(x => byId.TryGetValue(x.AssetId, out var replacement) ? replacement : x).ToArray();
    }

    private async Task<List<UgcRawCatalogItem>> FetchCandidatesAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        // Roblox search is discovery-only: current Collectible rows can contain placeholder
        // price/sales fields. Hydrate IDs through batch details before filtering/scoring.
        var discovery = await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return discovery.Items.Select(item => new UgcRawCatalogItem(
            item.AssetId,
            item.Name,
            item.CreatorName,
            item.CreatorId,
            item.AssetType,
            item.Category,
            item.Price,
            item.PurchaseCount,
            item.UnitsAvailable,
            item.TotalQuantity,
            item.FavoriteCount,
            observedAtUtc,
            item.CollectibleItemId,
            item.LowestResalePrice,
            item.HasResellers,
            item.PrimaryMarketVerified)).ToList();
    }
    private async Task<HttpResponseMessage> SendSearchRequestWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == maxAttempts - 1)
                return response;

            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(900);
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 500, 5000));
            response.Dispose();
            _logger.Info($"UGC Hunter rate limited by Roblox; retrying after {delay.TotalMilliseconds:N0} ms.");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("UGC Hunter retry loop ended unexpectedly.");
    }

    private static async Task<string?> ReadResponseDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (text.Length == 0) return null;
            return text.Length <= 220 ? text : text[..220] + "…";
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseCandidate(JsonElement element, DateTimeOffset observedAtUtc, out UgcRawCatalogItem item)
    {
        item = default!;
        if (!TryGetInt64(element, "id", out var id) || id <= 0) return false;
        if (!TryGetInt32(element, "price", out var price) || price <= 0) return false;
        if (!string.Equals(GetString(element, "itemType"), "Asset", StringComparison.OrdinalIgnoreCase)) return false;

        var creatorId = TryGetInt64(element, "creatorTargetId", out var parsedCreator) ? parsedCreator : 0;
        if (creatorId == 1) return false;

        var restrictions = GetStringArray(element, "itemRestrictions");
        if (!restrictions.Any(x =>
                x.Equals("Limited", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("LimitedUnique", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("Collectible", StringComparison.OrdinalIgnoreCase))) return false;

        var assetType = TryGetInt32(element, "assetType", out var parsedAssetType) ? parsedAssetType : 0;
        if (!IsAvatarAccessory(assetType)) return false;
        if (TryGetBoolean(element, "isOffSale", out var isOffSale) && isOffSale) return false;

        var priceStatus = GetString(element, "priceStatus");
        if (priceStatus is not null &&
            (priceStatus.Equals("OffSale", StringComparison.OrdinalIgnoreCase) ||
             priceStatus.Equals("Off Sale", StringComparison.OrdinalIgnoreCase) ||
             priceStatus.Equals("Free", StringComparison.OrdinalIgnoreCase))) return false;

        var saleLocationType = GetString(element, "saleLocationType");
        if (!string.IsNullOrWhiteSpace(saleLocationType) && !saleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase)) return false;

        // TimedOptions describes rental-duration purchase choices for supported avatar items.
        // It is not an experience-only acquisition signal, so it must not suppress a valid Limited.

        long? unitsAvailable = TryGetInt64(element, "unitsAvailableForConsumption", out var units) ? Math.Max(0, units) : null;
        long? totalQuantity = TryGetInt64(element, "totalQuantity", out var total) && total > 0 ? total : null;
        var statuses = GetStringArray(element, "itemStatus");
        var hasSaleFlag = statuses.Any(x => x.Equals("Sale", StringComparison.OrdinalIgnoreCase) || x.Equals("SaleTimer", StringComparison.OrdinalIgnoreCase));
        if (unitsAvailable is not > 0 && !hasSaleFlag) return false;

        var purchaseCount = TryGetInt64(element, "purchaseCount", out var purchases) ? Math.Max(0, purchases) : 0;
        if (totalQuantity is { } knownTotal && unitsAvailable is { } remaining)
            purchaseCount = Math.Max(purchaseCount, Math.Max(0, knownTotal - remaining));

        var favorites = TryGetInt64(element, "favoriteCount", out var favs) ? Math.Max(0, favs) : 0;
        item = new UgcRawCatalogItem(
            id,
            GetString(element, "name") ?? $"Asset {id}",
            GetString(element, "creatorName") ?? "Unknown creator",
            creatorId,
            assetType,
            CategoryName(assetType),
            price,
            purchaseCount,
            unitsAvailable,
            totalQuantity,
            favorites,
            observedAtUtc);
        return true;
    }

    private static UgcHunterItem Analyze(UgcRawCatalogItem item, IReadOnlyList<UgcHunterObservation> history, string? thumbnail)
    {
        var velocity1 = Velocity(history, TimeSpan.FromMinutes(1));
        var velocity5 = Velocity(history, TimeSpan.FromMinutes(5));
        var velocity15 = Velocity(history, TimeSpan.FromMinutes(15));
        var bestVelocity = velocity1 > 0 ? velocity1 : velocity5 > 0 ? velocity5 : velocity15;
        var acceleration = velocity5 > 0 && velocity1 > 0
            ? Math.Clamp((velocity1 - velocity5) / velocity5, -2.0, 4.0)
            : 0.0;

        long? totalSupply = item.TotalQuantity is > 0
            ? item.TotalQuantity
            : item.UnitsAvailable is { } remaining
                ? Math.Max(1, item.PurchaseCount + remaining)
                : null;
        var remainingPct = totalSupply is { } supply && item.UnitsAvailable is { } left
            ? Math.Clamp((double)left / supply, 0d, 1d)
            : (double?)null;
        var absorptionPerHour = totalSupply is { } supply2 && bestVelocity > 0
            ? bestVelocity * 60.0 / supply2
            : 0d;

        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            item.Price,
            totalSupply,
            item.PurchaseCount,
            item.UnitsAvailable,
            bestVelocity,
            acceleration,
            item.FavoriteCount,
            history.Count));

        var entry = CalculateEntryScore(score, remainingPct, acceleration);
        var velocityLabel = bestVelocity <= 0.01 ? "CALIBRATING"
            : acceleration >= 0.25 ? "ACCELERATING"
            : acceleration <= -0.45 ? "COOLING FAST"
            : acceleration <= -0.15 ? "COOLING"
            : "SUSTAINED";
        var phase = remainingPct switch
        {
            >= 0.80 => "JUST DROPPED",
            >= 0.55 => acceleration >= 0.15 ? "LAUNCH SURGE" : "EARLY MARKET",
            >= 0.25 => acceleration < -0.2 ? "SLOWING" : "SUSTAINED DEMAND",
            > 0 => "SCARCITY PHASE",
            0 => "SOLD OUT",
            null => history.Count < 3 ? "DISCOVERED" : "LIVE",
            _ => "LIVE"
        };
        TimeSpan? eta = item.UnitsAvailable is { } unitsLeft && bestVelocity > 0.01
            ? TimeSpan.FromMinutes(unitsLeft / bestVelocity)
            : null;
        if (eta > TimeSpan.FromDays(7)) eta = null;

        var analyzed = CreateHunterItem(item, thumbnail, velocity1, velocity5, velocity15, acceleration, absorptionPerHour,
            totalSupply, score, entry, phase, velocityLabel, eta, history.Count, null, null);
        return analyzed with
        {
            CollectibleItemId = item.CollectibleItemId,
            CurrentResaleFloor = item.CurrentResaleFloor,
            HasLiveResaleMarket = item.HasResellers && item.CurrentResaleFloor is > 0,
            PrimaryMarketVerified = item.PrimaryMarketVerified
        };
    }

    private static UgcHunterItem ApplyResaleEvidence(UgcHunterItem item, RobloxResaleMarketData market, RobloxResellerMarketData? book)
    {
        var currentFloor = book?.LowestPrice ?? item.CurrentResaleFloor;
        var hasMarket = market.IsAvailable || book is { IsAvailable: true } || currentFloor is > 0;
        var sales30 = SalesLast30d(market);
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            item.Price,
            item.TotalSupply,
            item.PurchaseCount,
            item.UnitsAvailable,
            item.VelocityPerMinute,
            item.Acceleration,
            item.FavoriteCount,
            item.ObservationCount,
            market.RecentAveragePrice,
            currentFloor,
            book?.ObservedListings ?? 0,
            book?.HasMoreListings ?? false,
            book?.ListingsWithin10Pct ?? 0,
            book?.ListingsWithin20Pct ?? 0,
            market.SalesLast7d,
            sales30,
            book?.MedianTop10,
            hasMarket));

        var remainingPct = item.TotalSupply is { } supply && item.UnitsAvailable is { } left
            ? Math.Clamp((double)left / supply, 0d, 1d)
            : (double?)null;
        var entry = CalculateEntryScore(score, remainingPct, item.Acceleration);
        var entryWindow = EntryWindow(score.Recommendation, entry);

        return item with
        {
            OpportunityScore = score.ResalePotential,
            EntryScore = entry,
            RiskScore = score.RiskScore,
            Confidence = score.Confidence,
            EntryWindow = entryWindow,
            BearValue = ToIntPrice(score.BearResale),
            BaseValue = ToIntPrice(score.BaseResale),
            BullValue = ToIntPrice(score.BullResale),
            Reasons = score.Reasons,
            Risks = score.Risks,
            CollectibleItemId = market.CollectibleItemId ?? item.CollectibleItemId,
            ResalePotentialScore = score.ResalePotential,
            LiquidityScore = score.LiquidityScore,
            ProfitabilityScore = score.ProfitabilityScore,
            ProjectedNetRoi = score.BaseNetRoi,
            BreakEvenResalePrice = score.BreakEvenResale,
            CurrentResaleFloor = currentFloor,
            RecentAveragePrice = market.RecentAveragePrice,
            ObservedResellers = book?.ObservedListings ?? 0,
            ResellerBookTruncated = book?.HasMoreListings ?? false,
            SalesLast7d = market.SalesLast7d,
            SalesLast30d = sales30,
            LiquidityLabel = score.LiquidityLabel,
            Recommendation = score.Recommendation,
            HasLiveResaleMarket = hasMarket,
            PrimaryMarketVerified = item.PrimaryMarketVerified
        };
    }

    private static UgcHunterItem CreateHunterItem(
        UgcRawCatalogItem item,
        string? thumbnail,
        double velocity1,
        double velocity5,
        double velocity15,
        double acceleration,
        double absorptionPerHour,
        long? totalSupply,
        UgcResaleScore score,
        double entry,
        string phase,
        string velocityLabel,
        TimeSpan? eta,
        int observationCount,
        RobloxResaleMarketData? market,
        RobloxResellerMarketData? book)
    {
        var entryWindow = EntryWindow(score.Recommendation, entry);
        return new UgcHunterItem(
            item.AssetId,
            item.Name,
            item.CreatorName,
            item.CreatorId,
            item.Category,
            thumbnail,
            item.Price,
            item.PurchaseCount,
            item.UnitsAvailable,
            totalSupply,
            item.FavoriteCount,
            velocity1,
            velocity5,
            velocity15,
            acceleration,
            absorptionPerHour,
            score.ResalePotential,
            entry,
            score.RiskScore,
            score.Confidence,
            phase,
            velocityLabel,
            entryWindow,
            eta,
            ToIntPrice(score.BearResale),
            ToIntPrice(score.BaseResale),
            ToIntPrice(score.BullResale),
            score.Reasons,
            score.Risks,
            item.ObservedAtUtc)
        {
            CollectibleItemId = market?.CollectibleItemId,
            ResalePotentialScore = score.ResalePotential,
            LiquidityScore = score.LiquidityScore,
            ProfitabilityScore = score.ProfitabilityScore,
            ProjectedNetRoi = score.BaseNetRoi,
            BreakEvenResalePrice = score.BreakEvenResale,
            CurrentResaleFloor = book?.LowestPrice,
            RecentAveragePrice = market?.RecentAveragePrice,
            ObservedResellers = book?.ObservedListings ?? 0,
            ResellerBookTruncated = book?.HasMoreListings ?? false,
            SalesLast7d = market?.SalesLast7d ?? 0,
            SalesLast30d = market is null ? 0 : SalesLast30d(market),
            LiquidityLabel = score.LiquidityLabel,
            Recommendation = score.Recommendation,
            HasLiveResaleMarket = market?.IsAvailable == true || book?.IsAvailable == true,
            ObservationCount = observationCount
        };
    }

    private static double CalculateEntryScore(UgcResaleScore score, double? remainingPct, double acceleration)
    {
        // Being near sellout is useful when resale economics are strong. It is not a reason to
        // buy by itself, and the profitability hard gates in UgcResaleScoring always win.
        double timing = remainingPct switch
        {
            >= 0.75 => 45d,
            >= 0.50 => 55d,
            >= 0.30 => 66d,
            >= 0.15 => 78d,
            >= 0.05 => 90d,
            > 0 => 96d,
            0 => 20d,
            null => 58d,
            _ => 58d
        };
        var entry = ClampScore(score.ResalePotential * 0.60d + timing * 0.25d + score.DemandScore * 0.15d);
        if (score.Recommendation == "AVOID") entry = Math.Min(entry, 30d);
        if (acceleration < -0.45d) entry = ClampScore(entry - 16d);
        return entry;
    }

    private static string EntryWindow(string recommendation, double entry) => recommendation == "AVOID" ? "AVOID"
        : entry >= 82 ? "STRONG"
        : entry >= 65 ? "OPEN"
        : entry >= 45 ? "CLOSING"
        : "LATE";

    private static double Velocity(IReadOnlyList<UgcHunterObservation> history, TimeSpan window)
    {
        if (history.Count < 2) return 0;
        var latest = history[^1];
        var cutoff = latest.ObservedAtUtc - window;
        UgcHunterObservation? oldest = null;
        for (var i = history.Count - 2; i >= 0; i--)
        {
            oldest = history[i];
            if (oldest.Value.ObservedAtUtc <= cutoff) break;
        }
        if (oldest is null) return 0;
        var elapsed = latest.ObservedAtUtc - oldest.Value.ObservedAtUtc;
        if (elapsed.TotalSeconds < 10) return 0;
        var delta = Math.Max(0, latest.PurchaseCount - oldest.Value.PurchaseCount);
        return delta / elapsed.TotalMinutes;
    }

    private static double SalesLast30d(RobloxResaleMarketData market)
    {
        var valid = market.VolumeDataPoints.Where(x => x.Value >= 0).OrderByDescending(x => x.Date).ToArray();
        if (valid.Length == 0) return 0;
        var newest = valid[0].Date;
        return valid.Where(x => x.Date >= newest - TimeSpan.FromDays(30.5)).Sum(x => x.Value);
    }

    private static UgcMarketState BuildMarketState(IReadOnlyList<UgcHunterItem> items)
    {
        if (items.Count == 0)
            return new UgcMarketState("QUIET", 0, 0, 0, "No qualifying paid catalog-buyable UGC Limiteds found in the current scan.");

        var average = items.Average(x => x.ResalePotentialScore);
        var averageVelocity = items.Average(x => x.VelocityPerMinute);
        var strong = items.Count(x => x.ResalePotentialScore >= 75 && x.EntryScore >= 55 && x.Recommendation != "AVOID");
        var regime = average >= 76 || strong >= 5 ? "VERY HOT"
            : average >= 66 || strong >= 3 ? "HOT"
            : average >= 54 ? "NORMAL"
            : average >= 42 ? "COOLING"
            : "WEAK";
        var detail = $"{strong} high-resale drop{(strong == 1 ? string.Empty : "s")} · avg resale {average:0} · avg velocity {averageVelocity:0.0}/m";
        return new UgcMarketState(regime, items.Count, strong, average, detail);
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _historyPath + ".tmp";
            var all = _history.Values.SelectMany(x => x).OrderBy(x => x.ObservedAtUtc).ToArray();
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = false }), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"UGC Hunter history could not be saved: {ex.Message}");
        }
    }

    private static bool IsAvatarAccessory(int assetType) =>
        assetType == 8 ||
        assetType is >= 41 and <= 47 ||
        assetType is >= 64 and <= 72 ||
        assetType is 76 or 77 or 79 ||
        assetType is >= 88 and <= 90;

    private static string CategoryName(int assetType) => assetType switch
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

    private static int ToIntPrice(long value) => (int)Math.Clamp(value, 1L, int.MaxValue);
    private static double ClampScore(double value) => Math.Clamp(Math.Round(value), 0, 100);

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

    private sealed record UgcRawCatalogItem(
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
        DateTimeOffset ObservedAtUtc,
        string? CollectibleItemId = null,
        long? CurrentResaleFloor = null,
        bool HasResellers = false,
        bool PrimaryMarketVerified = false);
}

public readonly record struct UgcHunterObservation(
    long AssetId,
    long PurchaseCount,
    long? UnitsAvailable,
    int Price,
    long FavoriteCount,
    DateTimeOffset ObservedAtUtc);

public sealed record UgcHunterMarketSnapshot(
    IReadOnlyList<UgcHunterItem> Items,
    UgcMarketState Market,
    DateTimeOffset ObservedAtUtc);

public sealed record UgcMarketState(
    string Regime,
    int LiveDrops,
    int StrongDrops,
    double AverageOpportunity,
    string Detail);

public sealed record UgcHunterItem(
    long AssetId,
    string Name,
    string CreatorName,
    long CreatorId,
    string Category,
    string? ThumbnailUrl,
    int Price,
    long PurchaseCount,
    long? UnitsAvailable,
    long? TotalSupply,
    long FavoriteCount,
    double Velocity1m,
    double Velocity5m,
    double Velocity15m,
    double Acceleration,
    double AbsorptionPerHour,
    double OpportunityScore,
    double EntryScore,
    double RiskScore,
    double Confidence,
    string Phase,
    string VelocityLabel,
    string EntryWindow,
    TimeSpan? SelloutEta,
    int BearValue,
    int BaseValue,
    int BullValue,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Risks,
    DateTimeOffset ObservedAtUtc)
{
    public string? CollectibleItemId { get; init; }
    public double ResalePotentialScore { get; init; }
    public double LiquidityScore { get; init; }
    public double ProfitabilityScore { get; init; }
    public double ProjectedNetRoi { get; init; }
    public long BreakEvenResalePrice { get; init; }
    public long? CurrentResaleFloor { get; init; }
    public double? RecentAveragePrice { get; init; }
    public int ObservedResellers { get; init; }
    public bool ResellerBookTruncated { get; init; }
    public double SalesLast7d { get; init; }
    public double SalesLast30d { get; init; }
    public string LiquidityLabel { get; init; } = "MODELED";
    public string Recommendation { get; init; } = "WATCH";
    public bool HasLiveResaleMarket { get; init; }
    public int ObservationCount { get; init; }
    public bool PrimaryMarketVerified { get; init; }

    public int DataQualityScore
    {
        get
        {
            var score = 0;
            if (PrimaryMarketVerified) score += 25;
            if (Price > 0) score += 10;
            if (TotalSupply is > 0) score += 10;
            if (UnitsAvailable is not null) score += 10;
            if (CurrentResaleFloor is > 0) score += 15;
            if (RecentAveragePrice is > 0) score += 10;
            if (ObservedResellers > 0) score += 10;
            if (SalesLast7d > 0 || SalesLast30d > 0) score += 10;
            return Math.Clamp(score, 0, 100);
        }
    }
    public string DataQualityLabel => DataQualityScore switch
    {
        >= 85 => "VERIFIED",
        >= 70 => "STRONG",
        >= 50 => "PARTIAL",
        _ => "LOW"
    };
    public string DataQualityText => $"{DataQualityScore}% {DataQualityLabel}";
    public string FreshnessText
    {
        get
        {
            var age = DateTimeOffset.UtcNow - ObservedAtUtc;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            return age switch
            {
                { TotalMinutes: < 2 } => "LIVE",
                { TotalMinutes: < 10 } => $"{Math.Max(1, age.TotalMinutes):0}m old",
                { TotalMinutes: < 60 } => $"{age.TotalMinutes:0}m old",
                _ => $"{age.TotalHours:0.0}h old"
            };
        }
    }
    public string EvidenceText
    {
        get
        {
            var evidence = new List<string>();
            evidence.Add(PrimaryMarketVerified ? "✓ Roblox primary market" : "— catalog fallback");
            evidence.Add(TotalSupply is > 0 && UnitsAvailable is not null ? "✓ supply" : "— supply missing");
            evidence.Add(CurrentResaleFloor is > 0 ? "✓ floor" : "— floor unavailable");
            evidence.Add(RecentAveragePrice is > 0 ? "✓ RAP" : "— RAP unavailable");
            evidence.Add(ObservedResellers > 0 ? "✓ reseller book" : "— reseller book unavailable");
            evidence.Add(SalesLast7d > 0 || SalesLast30d > 0 ? "✓ resale volume" : "— resale volume unavailable");
            return string.Join(" · ", evidence);
        }
    }

    public double VelocityPerMinute => Velocity1m > 0 ? Velocity1m : Velocity5m > 0 ? Velocity5m : Velocity15m;
    public string PriceText => $"{Price:N0} R$";
    public string RemainingText => UnitsAvailable is { } left && TotalSupply is { } total
        ? $"{left:N0} · {(double)left / total:P0}"
        : UnitsAvailable is { } only ? only.ToString("N0") : "—";
    public string VelocityText => VelocityPerMinute > 0 ? $"{VelocityPerMinute:0.0}/m" : "CAL";
    public string AccelerationText => VelocityPerMinute <= 0 ? "—" : $"{Acceleration:+0%;-0%;0%}";
    public string EtaText => SelloutEta is null ? "—" : SelloutEta.Value.TotalHours >= 1 ? $"{SelloutEta.Value.TotalHours:0.0}h" : $"{Math.Max(1, SelloutEta.Value.TotalMinutes):0}m";
    public string OpportunityText => ResalePotentialScore.ToString("0");
    public string ResalePotentialText => ResalePotentialScore.ToString("0");
    public string EntryText => EntryScore.ToString("0");
    public string RiskText => RiskScore.ToString("0");
    public string ConfidenceText => $"{Confidence:0}%";
    public string ForecastText => $"{BearValue:N0}–{BullValue:N0} R$";
    public string BaseValueText => $"{BaseValue:N0} R$";
    public string SupplyText => TotalSupply is { } supply ? supply.ToString("N0") : "—";
    public string PurchaseCountText => PurchaseCount.ToString("N0");
    public string FavoriteCountText => FavoriteCount.ToString("N0");
    public string NetRoiText => $"{ProjectedNetRoi:+0%;-0%;0%}";
    public string BreakEvenText => BreakEvenResalePrice > 0 ? $"{BreakEvenResalePrice:N0} R$" : "—";
    public string CurrentResaleText => CurrentResaleFloor is > 0 ? $"{CurrentResaleFloor:N0} R$" : "—";
    public string RapText => RecentAveragePrice is > 0 ? $"{RecentAveragePrice.Value:N0} R$" : "—";
    public string ResellersText => ObservedResellers <= 0 ? "—" : ResellerBookTruncated ? $"{ObservedResellers:N0}+" : ObservedResellers.ToString("N0");
    public string SalesText => HasLiveResaleMarket ? $"{SalesLast7d:0.#}/7d · {SalesLast30d:0.#}/30d" : "MODELED";
    public string LiquidityText => $"{LiquidityLabel} · {LiquidityScore:0}";
    public string VerificationText => PrimaryMarketVerified ? "ROBLOX VERIFIED" : "CATALOG FALLBACK";
    public string RobloxUrl => $"https://www.roblox.com/catalog/{AssetId}";
}