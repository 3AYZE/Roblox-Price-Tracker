namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Selects a bounded set of UGC candidates for resale enrichment. Verified,
/// in-ceiling original stock gets the full request budget first, with category
/// diversity and rotation. Research candidates fill only unused capacity.
/// </summary>
public static class UgcResaleEnrichmentSelectionPolicy
{
    public sealed record Candidate(
        long AssetId,
        int AssetType,
        bool PrimaryMarketVerified,
        bool IsGoodBuyingPrice,
        long? UnitsAvailable,
        long? TotalSupply,
        int ObservationCount,
        double VelocityPerMinute,
        double ResalePotentialScore,
        double EntryScore)
    {
        public bool IsBuyingCandidate =>
            IsGoodBuyingPrice && UgcActiveStockPolicy.HasMinimumSellThrough(
                PrimaryMarketVerified, UnitsAvailable, TotalSupply);

        public bool HasVerifiedScarcity =>
            PrimaryMarketVerified && TotalSupply is > 0 &&
            UnitsAvailable is > 0 and <= 300 &&
            UnitsAvailable <= TotalSupply * 0.25d &&
            ObservationCount >= 2 && double.IsFinite(VelocityPerMinute) && VelocityPerMinute > 0;
    }

    private sealed class CategoryQueue(IEnumerable<Candidate> candidates, long candidateRotation)
    {
        private readonly Queue<Candidate> _items = new(RotateCandidates(candidates, candidateRotation));

        public bool TryTakeNext(out Candidate candidate)
        {
            if (_items.TryDequeue(out candidate!)) return true;
            candidate = null!;
            return false;
        }
    }

    public static IReadOnlyList<Candidate> Select(
        IEnumerable<Candidate> candidates,
        int limit,
        long categoryRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (limit <= 0) return Array.Empty<Candidate>();

        var unique = candidates
            .Where(x => x.AssetId > 0 && x.AssetType > 0)
            .GroupBy(x => x.AssetId)
            .Select(group => group
                .OrderByDescending(x => x.IsBuyingCandidate)
                .ThenByDescending(x => x.HasVerifiedScarcity)
                .First())
            .ToArray();
        if (unique.Length == 0) return Array.Empty<Candidate>();

        var buyingQueues = CreateQueues(unique.Where(x => x.IsBuyingCandidate), categoryRotation);
        var researchQueues = CreateQueues(unique.Where(x => !x.IsBuyingCandidate), categoryRotation);

        // Affordable verified original stock gets first use of every request.
        // Keep category turns and candidate rotation within that pool so one
        // category or asset cannot monopolize a bounded scan.
        var selected = TakeRoundRobin(buyingQueues, limit, categoryRotation);
        var remaining = limit - selected.Count;
        if (remaining > 0)
            selected.AddRange(TakeRoundRobin(researchQueues, remaining, categoryRotation));
        return selected;
    }

    private static CategoryQueue[] CreateQueues(IEnumerable<Candidate> candidates, long candidateRotation) => candidates
        .GroupBy(x => x.AssetType)
        .OrderBy(group => group.Key)
        .Select(group => new CategoryQueue(group, candidateRotation))
        .ToArray();

    private static Candidate[] RotateCandidates(IEnumerable<Candidate> candidates, long rotation)
    {
        var ordered = candidates
            .OrderByDescending(x => x.HasVerifiedScarcity)
            .ThenBy(x => x.UnitsAvailable ?? long.MaxValue)
            .ThenByDescending(x => ScoreOrMinimum(x.ResalePotentialScore))
            .ThenByDescending(x => ScoreOrMinimum(x.EntryScore))
            .ThenBy(x => x.AssetId)
            .ToArray();
        if (ordered.Length <= 1) return ordered;

        var start = RotationOffset(rotation, ordered.Length);
        return ordered.Skip(start).Concat(ordered.Take(start)).ToArray();
    }

    private static List<Candidate> TakeRoundRobin(CategoryQueue[] queues, int limit, long rotation)
    {
        var selected = new List<Candidate>();
        var orderedQueues = Rotate(queues, rotation);
        while (selected.Count < limit)
        {
            var progressed = false;
            foreach (var queue in orderedQueues)
            {
                if (!queue.TryTakeNext(out var candidate)) continue;
                selected.Add(candidate);
                progressed = true;
                if (selected.Count >= limit) break;
            }

            if (!progressed) break;
        }

        return selected;
    }

    private static CategoryQueue[] Rotate(CategoryQueue[] queues, long rotation)
    {
        if (queues.Length <= 1) return queues;
        var start = RotationOffset(rotation, queues.Length);
        return queues.Skip(start).Concat(queues.Take(start)).ToArray();
    }

    private static int RotationOffset(long rotation, int length)
    {
        var offset = (int)(rotation % length);
        return offset < 0 ? offset + length : offset;
    }

    private static double ScoreOrMinimum(double value) =>
        double.IsFinite(value) ? value : double.MinValue;
}
