namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Selects IDs already found by existing public catalog feeds. Reserving a small
/// number from each feed prevents a single popular momentum window from filling
/// the entire scanner, without adding hydration requests or trusting catalog
/// prices/stock as purchase evidence.
/// </summary>
public static class UgcDiscoverySelectionPolicy
{
    public const int RecentReserve = 20;
    public const int EmoteReserve = 4;
    public const int MaximumEmotes = 8;
    public const int FeedDiversityReserve = 28;
    public const int PerFeedReserve = 4;

    // Two category keyword requests per uncached scan; the hydration budget stays
    // unchanged. Search keywords are hints only, never proof of category/material.
    private static readonly string[] CategoryKeywords =
        ["Hair", "Hat", "Face", "Neck", "Shoulder", "Back", "Front", "Waist",
         "Emote", "Jacket", "Shirt", "Pants", "Sweater", "Dress", "Shoes", "Emissive", "PBR", "Accessory"];

    public static IReadOnlyList<string> CategoryKeywordsForScan(long rotation)
    {
        var pairs = CategoryKeywords.Length / 2;
        var pair = (int)(rotation % pairs);
        if (pair < 0) pair += pairs;
        return new[] { CategoryKeywords[pair * 2], CategoryKeywords[pair * 2 + 1] };
    }

    public static IReadOnlyList<long> Select(
        IReadOnlyList<long> rankedMomentumIds,
        IReadOnlyList<IReadOnlyList<long>> feedIds,
        IReadOnlyList<long> recentIds,
        IReadOnlyList<long> emoteIds,
        int capacity,
        IReadOnlyDictionary<long, int>? assetTypes = null, long rotation = 0)
    {
        if (capacity <= 0) return Array.Empty<long>();

        var selected = new List<long>(capacity);
        var seen = new HashSet<long>();
        var typeCounts = new Dictionary<int, int>();
        var momentumCapacity = capacity - Math.Min(RecentReserve, Math.Max(1, capacity / 6));

        bool TryAdd(long id, int limit)
        {
            if (id <= 0 || selected.Count >= limit || seen.Contains(id)) return false;
            var type = assetTypes is not null && assetTypes.TryGetValue(id, out var knownType)
                ? knownType : 0;
            if (type == 61 && typeCounts.GetValueOrDefault(61) >= MaximumEmotes) return false;
            seen.Add(id);
            selected.Add(id);
            if (type > 0) typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
            return true;
        }

        // Keep the strongest 32 discovery IDs and rotate the remaining
        // momentum tail on new scans; otherwise a popular but overpriced item
        // can occupy the same hydration slot indefinitely. No new requests.
        var momentumOrder = RotateMomentumTail(rankedMomentumIds, rotation);

        // Give distinct known accessory types first access to the same ID budget.
        // Search type is a discovery hint; hydration and Marketplace still verify stock.
        if (assetTypes is not null)
        {
            var distinctTypes = rankedMomentumIds
                .Where(id => assetTypes.TryGetValue(id, out var type) && type is > 0 and not 61)
                .Select(id => assetTypes[id]).Distinct().ToArray();
            foreach (var type in distinctTypes)
            {
                foreach (var id in rankedMomentumIds.Where(id => assetTypes.TryGetValue(id, out var t) && t == type).Take(4))
                    TryAdd(id, momentumCapacity);
            }
        }

        // Keep only a small typed emote reserve; never infer emotes from names.
        foreach (var id in emoteIds.Take(EmoteReserve))
            TryAdd(id, momentumCapacity);

        // Each source contributes up to four distinct IDs, subject to a shared
        // 28-ID reserve. A source that repeats existing IDs cannot consume slots.
        var diverseCount = 0;
        foreach (var feed in feedIds)
        {
            var addedFromFeed = 0;
            foreach (var id in feed)
            {
                if (diverseCount >= FeedDiversityReserve ||
                    addedFromFeed >= PerFeedReserve || selected.Count >= momentumCapacity)
                    break;
                if (!TryAdd(id, momentumCapacity)) continue;
                diverseCount++;
                addedFromFeed++;
            }

            if (diverseCount >= FeedDiversityReserve || selected.Count >= momentumCapacity)
                break;
        }

        foreach (var id in momentumOrder)
        {
            if (selected.Count >= momentumCapacity) break;
            TryAdd(id, momentumCapacity);
        }

        // Guarantee room for newer listings, even when older listings dominate sales.
        foreach (var id in recentIds)
        {
            if (selected.Count >= capacity) break;
            TryAdd(id, capacity);
        }

        // Backfill any unused reserve from scored candidates, then each feed.
        foreach (var id in momentumOrder)
        {
            if (selected.Count >= capacity) break;
            TryAdd(id, capacity);
        }

        foreach (var feed in feedIds)
        {
            foreach (var id in feed)
            {
                if (selected.Count >= capacity) break;
                TryAdd(id, capacity);
            }
            if (selected.Count >= capacity) break;
        }

        return selected;
    }

    private static IReadOnlyList<long> RotateMomentumTail(IReadOnlyList<long> ranked, long rotation)
    {
        const int stablePrefix = 32;
        if (rotation == 0 || ranked.Count <= stablePrefix) return ranked;
        var tail = ranked.Skip(stablePrefix).ToArray();
        var offset = (int)(((rotation % tail.Length) * 32L) % tail.Length);
        if (offset < 0) offset += tail.Length;
        return ranked.Take(stablePrefix).Concat(tail.Skip(offset))
            .Concat(tail.Take(offset)).ToArray();
    }
}
