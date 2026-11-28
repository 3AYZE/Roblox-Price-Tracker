namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Official resale discovery must never reinterpret a historical week as the
/// current seven days. Scan a bounded number of fresh or never-enriched items
/// each pass to expand coverage without increasing Roblox request fan-out.
/// </summary>
public static class OfficialResaleCoveragePolicy
{
    public static readonly TimeSpan MaximumVolumeLag = TimeSpan.FromHours(48);
    public static readonly TimeSpan MaximumLegacySeriesLag = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaximumCachedMarketAge = TimeSpan.FromMinutes(40);

    public static bool HasRecentVolume(
        IReadOnlyList<ResaleDataPoint>? series,
        DateTimeOffset nowUtc) =>
        series?.Any(p => p.Value >= 0 && double.IsFinite(p.Value) &&
            p.Date <= nowUtc && nowUtc - p.Date <= MaximumVolumeLag) == true;

    // Official totals cover complete UTC reporting days, not an arbitrary
    // handful of recent points. Missing/invalid/conflicting buckets are unknown.
    // The current UTC day is still in progress and is not counted.
    public static double? CompletedDailyVolume(
        IReadOnlyList<ResaleDataPoint>? series, int days, DateTimeOffset nowUtc)
    {
        if (series is null || days <= 0 || days > 366) return null;
        var end = nowUtc.UtcDateTime.Date;
        var start = end.AddDays(-days);
        var buckets = series.Where(p => p.Date <= nowUtc &&
            p.Date.UtcDateTime.Date >= start && p.Date.UtcDateTime.Date < end)
            .GroupBy(p => p.Date.UtcDateTime.Date).ToArray();
        if (buckets.Length != days) return null;
        double total = 0;
        foreach (var bucket in buckets)
        {
            var values = bucket.Select(x => x.Value).Distinct().ToArray();
            if (values.Length != 1 || !double.IsFinite(values[0]) ||
                values[0] < 0 || Math.Truncate(values[0]) != values[0])
                return null;
            total += values[0];
        }
        return double.IsFinite(total) ? total : null;
    }

    public static bool IsDemonstrablyStaleLegacy(
        RobloxResaleMarketData data,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(data.Source, "Roblox Economy", StringComparison.Ordinal))
            return false;

        var dates = data.PriceDataPoints.Select(x => x.Date)
            .Concat(data.VolumeDataPoints.Select(x => x.Date))
            .Where(date => date <= nowUtc).ToArray();
        return dates.Length > 0 && nowUtc - dates.Max() > MaximumLegacySeriesLag;
    }

    public static IReadOnlyList<long> SelectNext(
        IEnumerable<long> discoveredIds,
        IReadOnlyDictionary<long, DateTimeOffset> lastAttemptedUtc,
        int maxPerScan, long? preferredId = null)
    {
        if (maxPerScan <= 0) return Array.Empty<long>();

        return discoveredIds.Where(id => id > 0).Distinct()
            .Select((id, rank) => new
            {
                Id = id,
                Rank = rank,
                LastAttempted = lastAttemptedUtc.TryGetValue(id, out var date)
                    ? date : DateTimeOffset.MinValue
            })
            // New/unattempted assets first, otherwise revisit the oldest
            // observed assets and retain original discovery rank as the tie-break.
            .OrderByDescending(x => x.Id == preferredId)
            .ThenBy(x => x.LastAttempted)
            .ThenBy(x => x.Rank)
            .Take(maxPerScan)
            .Select(x => x.Id)
            .ToArray();
    }
}
