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
    public const int EmoteReserve = 10;
    public const int FeedDiversityReserve = 28;
    public const int PerFeedReserve = 4;

    public static IReadOnlyList<long> Select(
        IReadOnlyList<long> rankedMomentumIds,
        IReadOnlyList<IReadOnlyList<long>> feedIds,
        IReadOnlyList<long> recentIds,
        IReadOnlyList<long> emoteIds,
        int capacity)
    {
        if (capacity <= 0) return Array.Empty<long>();

        var selected = new List<long>(capacity);
        var seen = new HashSet<long>();
        var momentumCapacity = capacity - Math.Min(RecentReserve, Math.Max(1, capacity / 6));

        bool TryAdd(long id, int limit)
        {
            if (id <= 0 || selected.Count >= limit || !seen.Add(id)) return false;
            selected.Add(id);
            return true;
        }

        // Dedicated emote coverage remains limited and is never inferred from a name.
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

        foreach (var id in rankedMomentumIds)
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
        foreach (var id in rankedMomentumIds)
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
}
