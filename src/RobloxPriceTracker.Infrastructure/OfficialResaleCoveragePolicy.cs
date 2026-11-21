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
    public static readonly TimeSpan MaximumCachedMarketAge = TimeSpan.FromMinutes(20);

    public static bool HasRecentVolume(
        IReadOnlyList<ResaleDataPoint>? series,
        DateTimeOffset nowUtc) =>
        series?.Any(p => p.Value >= 0 && double.IsFinite(p.Value) &&
            p.Date <= nowUtc && nowUtc - p.Date <= MaximumVolumeLag) == true;

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
        int maxPerScan)
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
            .OrderBy(x => x.LastAttempted)
            .ThenBy(x => x.Rank)
            .Take(maxPerScan)
            .Select(x => x.Id)
            .ToArray();
    }
}
