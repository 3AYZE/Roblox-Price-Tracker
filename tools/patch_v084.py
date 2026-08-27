from pathlib import Path
import re


def replace_once(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"{label}: expected 1 match, found {n}")
    return text.replace(old, new, 1)


# Discovery: carry collectible IDs and verify primary-market data through Marketplace Items.
p = Path("src/RobloxPriceTracker.Infrastructure/RobloxUgcDiscoveryService.cs")
text = p.read_text(encoding="utf-8")
text = replace_once(
    text,
    """public sealed record RobloxUgcCatalogCandidate(
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
    long FavoriteCount);""",
    """public sealed record RobloxUgcCatalogCandidate(
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
    bool PrimaryMarketVerified = false);""",
    "candidate record",
)
text = replace_once(
    text,
    """    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;""",
    """    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private readonly RobloxMarketplaceItemService _marketplaceItems;""",
    "marketplace field",
)
text = replace_once(
    text,
    """        _httpClient = httpClient;
        _logger = logger;""",
    """        _httpClient = httpClient;
        _logger = logger;
        _marketplaceItems = new RobloxMarketplaceItemService(httpClient, logger);""",
    "marketplace init",
)
text = replace_once(
    text,
    """        var candidates = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(detailDocument.RootElement);

        _logger.Info($"UGC Hunter two-stage discovery: {ids.Length} Collectible IDs, {hydratedRows} hydrated rows, {candidates.Count} paid catalog-buyable UGC Limiteds.");
        return new RobloxUgcDiscoveryResult(candidates, ids.Length, hydratedRows);""",
    """        var catalogCandidates = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(detailDocument.RootElement);

        var collectibleIds = catalogCandidates
            .Select(x => x.CollectibleItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var marketplace = collectibleIds.Length == 0
            ? new Dictionary<string, RobloxMarketplaceItemData>(StringComparer.OrdinalIgnoreCase)
            : await _marketplaceItems.GetManyAsync(collectibleIds, cancellationToken).ConfigureAwait(false);

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

            // Preserve the catalog-hydrated candidate if Marketplace Items is temporarily unavailable.
            // The UI marks this as a catalog fallback instead of pretending it is fully verified.
            candidates.Add(candidate);
        }

        _logger.Info($"UGC Hunter authoritative discovery: {ids.Length} IDs, {hydratedRows} catalog rows, {verified} marketplace-verified, {rejectedUnavailable} unavailable rejected, {candidates.Count} active candidates.");
        return new RobloxUgcDiscoveryResult(candidates, ids.Length, hydratedRows);""",
    "authoritative discovery merge",
)
text = replace_once(
    text,
    """        var statuses = GetStringArray(row, "itemStatus");
        var hasSaleFlag = statuses.Any(x => x.Equals("Sale", StringComparison.OrdinalIgnoreCase) || x.Equals("SaleTimer", StringComparison.OrdinalIgnoreCase));
        if (unitsAvailable is not > 0 && !hasSaleFlag) return false;

        var purchaseCount""",
    """        var purchaseCount""",
    "defer availability check",
)
text = replace_once(
    text,
    """            totalQuantity,
            favorites);""",
    """            totalQuantity,
            favorites,
            GetString(row, "collectibleItemId"));""",
    "capture collectible id",
)
p.write_text(text, encoding="utf-8")


# Resale aggregates: direct modern endpoint when Hunter already knows collectibleItemId.
p = Path("src/RobloxPriceTracker.Infrastructure/RobloxResaleDataService.cs")
text = p.read_text(encoding="utf-8")
text = replace_once(
    text,
    """    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();
    private string? _anonymousCsrfToken;""",
    """    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _modernCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _anonymousCsrfToken;""",
    "modern cache",
)
marker = "    private async Task<RobloxResaleMarketData> FetchResaleDataAsync("
method = """    public async Task<RobloxResaleMarketData> GetModernAsync(
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

            _modernCache[key] = new CacheEntry(result, DateTimeOffset.UtcNow + (result.IsAvailable ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2)));
            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

"""
if marker not in text:
    raise SystemExit("modern method marker not found")
text = text.replace(marker, method + marker, 1)
p.write_text(text, encoding="utf-8")


# Hunter: preserve authoritative primary data/floor and use modern resale directly.
p = Path("src/RobloxPriceTracker.Gui/UgcHunterServiceV080.cs")
text = p.read_text(encoding="utf-8")
text = replace_once(
    text,
    """            item.FavoriteCount,
            observedAtUtc)).ToList();""",
    """            item.FavoriteCount,
            observedAtUtc,
            item.CollectibleItemId,
            item.LowestResalePrice,
            item.HasResellers,
            item.PrimaryMarketVerified)).ToList();""",
    "raw candidate mapping",
)
text = replace_once(
    text,
    """                var market = await _resaleDataService.GetAsync(item.AssetId, cancellationToken).ConfigureAwait(false);
                RobloxResellerMarketData? book = null;
                if (!string.IsNullOrWhiteSpace(market.CollectibleItemId))
                    book = await _resellerDataService.GetAsync(item.AssetId, market.CollectibleItemId, cancellationToken).ConfigureAwait(false);""",
    """                var market = !string.IsNullOrWhiteSpace(item.CollectibleItemId)
                    ? await _resaleDataService.GetModernAsync(item.AssetId, item.CollectibleItemId, cancellationToken).ConfigureAwait(false)
                    : await _resaleDataService.GetAsync(item.AssetId, cancellationToken).ConfigureAwait(false);
                RobloxResellerMarketData? book = null;
                var collectibleId = market.CollectibleItemId ?? item.CollectibleItemId;
                if (!string.IsNullOrWhiteSpace(collectibleId))
                    book = await _resellerDataService.GetAsync(item.AssetId, collectibleId, cancellationToken).ConfigureAwait(false);""",
    "direct modern resale",
)
text = replace_once(
    text,
    """        return CreateHunterItem(item, thumbnail, velocity1, velocity5, velocity15, acceleration, absorptionPerHour,
            totalSupply, score, entry, phase, velocityLabel, eta, history.Count, null, null);""",
    """        var analyzed = CreateHunterItem(item, thumbnail, velocity1, velocity5, velocity15, acceleration, absorptionPerHour,
            totalSupply, score, entry, phase, velocityLabel, eta, history.Count, null, null);
        return analyzed with
        {
            CollectibleItemId = item.CollectibleItemId,
            CurrentResaleFloor = item.CurrentResaleFloor,
            HasLiveResaleMarket = item.HasResellers && item.CurrentResaleFloor is > 0,
            PrimaryMarketVerified = item.PrimaryMarketVerified
        };""",
    "baseline authoritative fields",
)
text = replace_once(
    text,
    """        var hasMarket = market.IsAvailable || book is { IsAvailable: true };
        var sales30 = SalesLast30d(market);""",
    """        var currentFloor = book?.LowestPrice ?? item.CurrentResaleFloor;
        var hasMarket = market.IsAvailable || book is { IsAvailable: true } || currentFloor is > 0;
        var sales30 = SalesLast30d(market);""",
    "market floor fallback",
)
text = replace_once(
    text,
    """            book?.LowestPrice,
            book?.ObservedListings ?? 0,""",
    """            currentFloor,
            book?.ObservedListings ?? 0,""",
    "score floor fallback",
)
text = replace_once(
    text,
    """            CollectibleItemId = market.CollectibleItemId,
            ResalePotentialScore = score.ResalePotential,""",
    """            CollectibleItemId = market.CollectibleItemId ?? item.CollectibleItemId,
            ResalePotentialScore = score.ResalePotential,""",
    "preserve collectible id",
)
text = replace_once(
    text,
    """            CurrentResaleFloor = book?.LowestPrice,
            RecentAveragePrice = market.RecentAveragePrice,""",
    """            CurrentResaleFloor = currentFloor,
            RecentAveragePrice = market.RecentAveragePrice,""",
    "preserve current floor",
)
text = replace_once(
    text,
    """            Recommendation = score.Recommendation,
            HasLiveResaleMarket = hasMarket
        };""",
    """            Recommendation = score.Recommendation,
            HasLiveResaleMarket = hasMarket,
            PrimaryMarketVerified = item.PrimaryMarketVerified
        };""",
    "preserve verification",
)
text = replace_once(
    text,
    """        long FavoriteCount,
        DateTimeOffset ObservedAtUtc);""",
    """        long FavoriteCount,
        DateTimeOffset ObservedAtUtc,
        string? CollectibleItemId = null,
        long? CurrentResaleFloor = null,
        bool HasResellers = false,
        bool PrimaryMarketVerified = false);""",
    "raw record fields",
)
text = replace_once(
    text,
    """    public int ObservationCount { get; init; }

    public double VelocityPerMinute""",
    """    public int ObservationCount { get; init; }
    public bool PrimaryMarketVerified { get; init; }

    public double VelocityPerMinute""",
    "item verification field",
)
text = replace_once(
    text,
    """    public string LiquidityText => $"{LiquidityLabel} · {LiquidityScore:0}";
    public string RobloxUrl""",
    """    public string LiquidityText => $"{LiquidityLabel} · {LiquidityScore:0}";
    public string VerificationText => PrimaryMarketVerified ? "ROBLOX VERIFIED" : "CATALOG FALLBACK";
    public string RobloxUrl""",
    "verification display",
)
p.write_text(text, encoding="utf-8")


# UI: prioritize actual market numbers instead of opaque model-only columns.
p = Path("src/RobloxPriceTracker.Gui/MainWindow.TerminalUi.cs")
text = p.read_text(encoding="utf-8")
text = text.replace('"70+ potential", "80+ potential", "90+ potential"', '"70+ resale", "80+ resale", "90+ resale"')
text = replace_once(text,
    '        _hunterInspectorOpportunity = AddScore(scoreGrid, 0, "OPPORTUNITY", "—", TerminalGreen);',
    '        _hunterInspectorOpportunity = AddScore(scoreGrid, 0, "RESALE", "—", TerminalGreen);',
    "inspector resale label")
text = replace_once(text, 'stack.Children.Add(MakeSmallLabel("LIVE MARKET"));', 'stack.Children.Add(MakeSmallLabel("REAL DROP DATA"));', "drop data label")
text = replace_once(text, '_hunterInspectorVelocity = AddInspectorMetric(stack, "Velocity", "—");', '_hunterInspectorVelocity = AddInspectorMetric(stack, "Primary sales", "—");', "primary sales label")
text = replace_once(text, '_hunterInspectorEta = AddInspectorMetric(stack, "Sellout ETA", "—");', '_hunterInspectorEta = AddInspectorMetric(stack, "Demand / ETA", "—");', "demand label")
text = replace_once(text, '_hunterInspectorSupply = AddInspectorMetric(stack, "Supply", "—");', '_hunterInspectorSupply = AddInspectorMetric(stack, "Supply / source", "—");', "supply label")
text = replace_once(text, 'stack.Children.Add(MakeSmallLabel("RESALE OUTLOOK"));', 'stack.Children.Add(MakeSmallLabel("RESALE MARKET + MODEL"));', "resale section label")
text = replace_once(
    text,
    '        stack.Children.Add(new TextBlock { Text = "Statistical scenario range, not a guaranteed resale price.", Foreground = TerminalMuted, FontSize = 8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });',
    '        stack.Children.Add(new TextBlock { Text = "Floor, RAP, sellers and resale volume are Roblox data. Model range and ROI are estimates.", Foreground = TerminalMuted, FontSize = 8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });',
    "resale note",
)
pattern = re.compile(r"    private void AddHunterColumns\(DataGrid grid\)\n    \{.*?\n    \}\n\n    private void AddPortfolioColumns", re.S)
replacement = """    private void AddHunterColumns(DataGrid grid)
    {
        grid.Columns.Add(CreateHunterAssetColumn());
        grid.Columns.Add(HunterTextColumn("PRICE", nameof(UgcHunterItem.PriceText), 0.68, TerminalText, true));
        grid.Columns.Add(HunterTextColumn("SOLD", nameof(UgcHunterItem.PurchaseCountText), 0.62, TerminalMuted));
        grid.Columns.Add(HunterTextColumn("LEFT", nameof(UgcHunterItem.RemainingText), 0.88, TerminalMuted));
        grid.Columns.Add(HunterTextColumn("FLOOR", nameof(UgcHunterItem.CurrentResaleText), 0.70, TerminalGreen, true));
        grid.Columns.Add(HunterTextColumn("RAP", nameof(UgcHunterItem.RapText), 0.66, TerminalText));
        grid.Columns.Add(HunterTextColumn("RESALES", nameof(UgcHunterItem.SalesText), 1.00, TerminalMuted));
        grid.Columns.Add(HunterTextColumn("NET ROI", nameof(UgcHunterItem.NetRoiText), 0.66, TerminalAmber, true));
        grid.Columns.Add(HunterTextColumn("RESALE", nameof(UgcHunterItem.ResalePotentialText), 0.60, TerminalGreen, true));
        grid.Columns.Add(HunterTextColumn("REC", nameof(UgcHunterItem.Recommendation), 0.82, TerminalBlue, true));
    }

    private void AddPortfolioColumns"""
text, n = pattern.subn(replacement, text, count=1)
if n != 1:
    raise SystemExit(f"AddHunterColumns: expected 1 match, found {n}")
text = replace_once(
    text,
    """        if (_hunterInspectorVelocity is not null) _hunterInspectorVelocity.Text = $"{item.VelocityText} · {item.VelocityLabel} · {item.AccelerationText}";
        if (_hunterInspectorEta is not null) _hunterInspectorEta.Text = item.EtaText;
        if (_hunterInspectorSupply is not null) _hunterInspectorSupply.Text = $"{item.RemainingText} remaining / {item.SupplyText} total";
        if (_hunterInspectorForecast is not null) _hunterInspectorForecast.Text = $"{item.ForecastText}  ·  BASE {item.BaseValueText}";""",
    """        if (_hunterInspectorVelocity is not null) _hunterInspectorVelocity.Text = $"{item.PurchaseCountText} sold · {item.FavoriteCountText} favorites";
        if (_hunterInspectorEta is not null) _hunterInspectorEta.Text = $"{item.VelocityText} · {item.VelocityLabel} · ETA {item.EtaText}";
        if (_hunterInspectorSupply is not null) _hunterInspectorSupply.Text = $"{item.RemainingText} remaining / {item.SupplyText} total · {item.VerificationText}";
        if (_hunterInspectorForecast is not null) _hunterInspectorForecast.Text = $"FLOOR {item.CurrentResaleText} · RAP {item.RapText} · SELLERS {item.ResellersText}\nSALES {item.SalesText} · BREAK-EVEN {item.BreakEvenText}\nMODEL {item.ForecastText} · BASE {item.BaseValueText} · NET ROI {item.NetRoiText}";""",
    "inspector real data",
)
p.write_text(text, encoding="utf-8")

print("v0.8.4 authoritative UGC market patch applied")
