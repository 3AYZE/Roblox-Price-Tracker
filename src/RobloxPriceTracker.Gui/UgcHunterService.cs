namespace RobloxPriceTracker.Gui;

public sealed class UgcHunterService
{
    private const string SearchEndpoint = "https://catalog.roblox.com/v1/search/items/details?Category=2&Subcategory=2&SortType=3&SortAggregation=1&Limit=30";
    private const int MaxPagesPerScan = 3;
    private readonly HttpClient _httpClient;
    private readonly RobloxThumbnailService _thumbnailService;
    private readonly AppLogger _logger;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, List<UgcHunterObservation>> _history = new();
    private bool _initialized;

    public UgcHunterService(HttpClient httpClient, RobloxThumbnailService thumbnailService, AppLogger logger, string dataDirectory)
    {
        _httpClient = httpClient;
        _thumbnailService = thumbnailService;
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
                    {
                        _history[group.Key] = group.OrderBy(x => x.ObservedAtUtc).TakeLast(360).ToList();
                    }
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

        var analyzed = new List<UgcHunterItem>(raw.Count);
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
                {
                    observations.Add(observation);
                }
                else
                {
                    observations[^1] = observation;
                }

                observations.RemoveAll(x => now - x.ObservedAtUtc > TimeSpan.FromHours(24));
                if (observations.Count > 360) observations.RemoveRange(0, observations.Count - 360);

                thumbnails.TryGetValue(candidate.AssetId, out var thumbnail);
                analyzed.Add(Analyze(candidate, observations, thumbnail));
            }

            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        analyzed = analyzed
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.EntryScore)
            .ToList();

        var market = BuildMarketState(analyzed);
        return new UgcHunterMarketSnapshot(analyzed, market, now);
    }

    private async Task<List<UgcRawCatalogItem>> FetchCandidatesAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<long, UgcRawCatalogItem>();
        string? cursor = null;

        for (var page = 0; page < MaxPagesPerScan; page++)
        {
            var url = cursor is null ? SearchEndpoint : $"{SearchEndpoint}&Cursor={Uri.EscapeDataString(cursor)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (page == 0)
                {
                    throw new HttpRequestException($"Roblox UGC search returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                }
                _logger.Error($"UGC Hunter stopped paging after HTTP {(int)response.StatusCode} on page {page + 1}.");
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                if (page == 0) throw new InvalidDataException("Roblox UGC search response did not contain a data array.");
                break;
            }

            foreach (var element in data.EnumerateArray())
            {
                if (TryParseCandidate(element, observedAtUtc, out var candidate)) candidates[candidate.AssetId] = candidate;
            }

            cursor = document.RootElement.TryGetProperty("nextPageCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(cursor)) break;
        }

        return candidates.Values.ToList();
    }

    private static bool TryParseCandidate(JsonElement element, DateTimeOffset observedAtUtc, out UgcRawCatalogItem item)
    {
        item = default!;
        if (!TryGetInt64(element, "id", out var id) || id <= 0) return false;
        if (!TryGetInt32(element, "price", out var price) || price <= 0) return false;

        var itemType = GetString(element, "itemType");
        if (!string.Equals(itemType, "Asset", StringComparison.OrdinalIgnoreCase)) return false;

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

        // Challenge/experience-only collectibles are intentionally excluded. Hunter is for items a user can
        // purchase directly through the catalog shop without completing an experience-specific requirement.
        var saleLocationType = GetString(element, "saleLocationType");
        if (!string.IsNullOrWhiteSpace(saleLocationType) && !saleLocationType.StartsWith("Shop", StringComparison.OrdinalIgnoreCase)) return false;

        if (element.TryGetProperty("timedOptions", out var timedOptions) && timedOptions.ValueKind == JsonValueKind.Array && timedOptions.GetArrayLength() > 0) return false;

        long? unitsAvailable = TryGetInt64(element, "unitsAvailableForConsumption", out var units) ? Math.Max(0, units) : null;
        long? totalQuantity = TryGetInt64(element, "totalQuantity", out var total) && total > 0 ? total : null;
        var statuses = GetStringArray(element, "itemStatus");
        var hasSaleFlag = statuses.Any(x => x.Equals("Sale", StringComparison.OrdinalIgnoreCase) || x.Equals("SaleTimer", StringComparison.OrdinalIgnoreCase));

        if (unitsAvailable is not > 0 && !hasSaleFlag) return false;

        var purchaseCount = TryGetInt64(element, "purchaseCount", out var purchases) ? Math.Max(0, purchases) : 0;
        if (totalQuantity is { } knownTotal && unitsAvailable is { } remaining)
        {
            purchaseCount = Math.Max(purchaseCount, Math.Max(0, knownTotal - remaining));
        }

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
        var now = item.ObservedAtUtc;
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
        var soldPct = totalSupply is { } supply
            ? Math.Clamp((double)item.PurchaseCount / supply, 0, 1)
            : 0;
        var remainingPct = totalSupply is { } supply2 && item.UnitsAvailable is { } remaining2
            ? Math.Clamp((double)remaining2 / supply2, 0, 1)
            : (double?)null;

        var absorptionPerHour = totalSupply is { } supply3 && bestVelocity > 0
            ? bestVelocity * 60.0 / supply3
            : 0;

        var velocityScore = ClampScore(18 + absorptionPerHour * 310 + Math.Log10(1 + bestVelocity) * 18);
        if (acceleration > 0) velocityScore = ClampScore(velocityScore + acceleration * 10);
        if (acceleration < -0.35) velocityScore = ClampScore(velocityScore + acceleration * 18);

        double scarcityScore = totalSupply switch
        {
            <= 500 => 98d,
            <= 1000 => 92d,
            <= 2500 => 82d,
            <= 5000 => 69d,
            <= 10000 => 54d,
            <= 20000 => 38d,
            null => 50d,
            _ => 24d
        };
        scarcityScore = ClampScore(scarcityScore + soldPct * 12);

        double priceScore = item.Price switch
        {
            <= 50 => 96d,
            <= 75 => 93d,
            <= 100 => 89d,
            <= 150 => 80d,
            <= 250 => 67d,
            <= 500 => 48d,
            <= 1000 => 32d,
            _ => 18d
        };

        var favoritePerPurchase = item.PurchaseCount > 0 ? (double)item.FavoriteCount / item.PurchaseCount : 0;
        var engagementScore = ClampScore(38 + Math.Log10(1 + item.FavoriteCount) * 8 + Math.Min(25, favoritePerPurchase * 8));

        double persistenceScore = history.Count switch
        {
            >= 10 => 82d,
            >= 6 => 70d,
            >= 3 => 58d,
            _ => 42d
        };
        if (velocity5 > 0 && velocity15 > 0)
        {
            var persistenceRatio = Math.Min(2, velocity5 / Math.Max(0.01, velocity15));
            persistenceScore = ClampScore(persistenceScore + (persistenceRatio - 1) * 18);
        }

        var opportunity = ClampScore(
            velocityScore * 0.34 +
            scarcityScore * 0.24 +
            priceScore * 0.15 +
            engagementScore * 0.14 +
            persistenceScore * 0.13);

        double entryTiming = remainingPct switch
        {
            >= 0.75 => 84d,
            >= 0.50 => 96d,
            >= 0.30 => 91d,
            >= 0.18 => 76d,
            >= 0.10 => 55d,
            >= 0.04 => 34d,
            < 0.04 => 18d,
            null => 62d,
            _ => 62d
        };
        var entry = ClampScore(entryTiming * 0.55 + priceScore * 0.25 + velocityScore * 0.20);
        if (acceleration < -0.45) entry = ClampScore(entry - 18);

        double dataConfidence = Math.Min(100d, 22d + history.Count * 8d);
        if (totalSupply is not null) dataConfidence += 8;
        if (bestVelocity > 0) dataConfidence += 8;
        dataConfidence = ClampScore(dataConfidence);

        var highPriceRisk = Math.Clamp((item.Price - 100) / 12.0, 0, 28);
        var oversupplyRisk = totalSupply is { } s ? Math.Clamp((s - 2500) / 300.0, 0, 30) : 12;
        var slowdownRisk = acceleration < 0 ? Math.Min(30, -acceleration * 38) : 0;
        var lowEvidenceRisk = (100 - dataConfidence) * 0.32;
        var risk = ClampScore(14 + highPriceRisk + oversupplyRisk + slowdownRisk + lowEvidenceRisk);

        var confidence = ClampScore(dataConfidence - risk * 0.18 + persistenceScore * 0.12);

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

        TimeSpan? eta = item.UnitsAvailable is { } left && bestVelocity > 0.01
            ? TimeSpan.FromMinutes(left / bestVelocity)
            : null;
        if (eta > TimeSpan.FromDays(7)) eta = null;

        var strength = Math.Clamp((opportunity - 45) / 55.0, 0, 1);
        var baseMultiplier = 1.05 + strength * 1.25;
        if (risk > 65) baseMultiplier *= 0.82;
        var baseValue = Math.Max(item.Price, (int)Math.Round(item.Price * baseMultiplier));
        var bearValue = Math.Max(1, (int)Math.Round(baseValue * (0.68 + confidence / 100.0 * 0.08)));
        var bullValue = Math.Max(baseValue, (int)Math.Round(baseValue * (1.28 + strength * 0.32)));

        var entryWindow = entry switch
        {
            >= 82 => "STRONG",
            >= 65 => "OPEN",
            >= 45 => "CLOSING",
            _ => "LATE"
        };

        var reasons = BuildReasons(velocityScore, scarcityScore, priceScore, engagementScore, acceleration, totalSupply);
        var risks = BuildRisks(item, acceleration, totalSupply, confidence);

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
            opportunity,
            entry,
            risk,
            confidence,
            phase,
            velocityLabel,
            entryWindow,
            eta,
            bearValue,
            baseValue,
            bullValue,
            reasons,
            risks,
            now);
    }

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

    private static IReadOnlyList<string> BuildReasons(double velocity, double scarcity, double price, double engagement, double acceleration, long? totalSupply)
    {
        var reasons = new List<(double score, string text)>
        {
            (velocity, velocity >= 75 ? "Strong sales velocity relative to supply" : "Sales velocity is developing"),
            (scarcity, totalSupply is { } s && s <= 2500 ? "Low supply improves scarcity" : "Supply is within a tradable range"),
            (price, price >= 80 ? "Low entry price improves capital efficiency" : "Entry price is manageable"),
            (engagement, engagement >= 72 ? "Favorites and purchase activity show healthy demand" : "Demand engagement is measurable")
        };
        if (acceleration >= 0.2) reasons.Add((92, "Sales velocity is accelerating"));
        return reasons.OrderByDescending(x => x.score).Take(3).Select(x => x.text).ToArray();
    }

    private static IReadOnlyList<string> BuildRisks(UgcRawCatalogItem item, double acceleration, long? totalSupply, double confidence)
    {
        var risks = new List<string>();
        if (acceleration <= -0.3) risks.Add("Launch velocity is slowing");
        if (totalSupply is > 10000) risks.Add("Large supply can pressure resale value");
        if (item.Price >= 500) risks.Add("High entry price reduces capital efficiency");
        if (confidence < 55) risks.Add("Limited observation history lowers confidence");
        if (risks.Count == 0) risks.Add("No major quantitative risk signal detected yet");
        return risks.Take(3).ToArray();
    }

    private static UgcMarketState BuildMarketState(IReadOnlyList<UgcHunterItem> items)
    {
        if (items.Count == 0) return new UgcMarketState("QUIET", 0, 0, 0, "No qualifying buyable UGC Limiteds found in the current scan.");
        var avgOpp = items.Average(x => x.OpportunityScore);
        var avgVelocity = items.Average(x => x.VelocityPerMinute);
        var strong = items.Count(x => x.OpportunityScore >= 75 && x.EntryScore >= 60);
        var regime = avgOpp >= 76 || strong >= 5 ? "VERY HOT"
            : avgOpp >= 66 || strong >= 3 ? "HOT"
            : avgOpp >= 54 ? "NORMAL"
            : avgOpp >= 42 ? "COOLING"
            : "WEAK";
        var detail = $"{strong} high-conviction drop{(strong == 1 ? string.Empty : "s")} · avg opportunity {avgOpp:0} · avg velocity {avgVelocity:0.0}/m";
        return new UgcMarketState(regime, items.Count, strong, avgOpp, detail);
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

    private static bool IsAvatarAccessory(int assetType) => assetType == 8 || assetType is >= 41 and <= 47;

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
        _ => "Accessory"
    };

    private static double ClampScore(double value) => Math.Clamp(Math.Round(value), 0, 100);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
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
        DateTimeOffset ObservedAtUtc);
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
    public double VelocityPerMinute => Velocity1m > 0 ? Velocity1m : Velocity5m > 0 ? Velocity5m : Velocity15m;
    public string PriceText => $"{Price:N0} R$";
    public string RemainingText => UnitsAvailable is { } left && TotalSupply is { } total
        ? $"{left:N0} · {(double)left / total:P0}"
        : UnitsAvailable is { } only ? only.ToString("N0") : "—";
    public string VelocityText => VelocityPerMinute > 0 ? $"{VelocityPerMinute:0.0}/m" : "CAL";
    public string AccelerationText => VelocityPerMinute <= 0 ? "—" : $"{Acceleration:+0%;-0%;0%}";
    public string EtaText => SelloutEta is null ? "—" : SelloutEta.Value.TotalHours >= 1 ? $"{SelloutEta.Value.TotalHours:0.0}h" : $"{Math.Max(1, SelloutEta.Value.TotalMinutes):0}m";
    public string OpportunityText => OpportunityScore.ToString("0");
    public string EntryText => EntryScore.ToString("0");
    public string RiskText => RiskScore.ToString("0");
    public string ConfidenceText => $"{Confidence:0}%";
    public string ForecastText => $"{BearValue:N0}–{BullValue:N0} R$";
    public string BaseValueText => $"{BaseValue:N0} R$";
    public string SupplyText => TotalSupply is { } supply ? supply.ToString("N0") : "—";
    public string PurchaseCountText => PurchaseCount.ToString("N0");
    public string FavoriteCountText => FavoriteCount.ToString("N0");
    public string RobloxUrl => $"https://www.roblox.com/catalog/{AssetId}";
}
