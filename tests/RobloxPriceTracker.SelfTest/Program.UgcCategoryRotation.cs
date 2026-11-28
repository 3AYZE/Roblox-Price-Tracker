using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcCategoryKeywordRotationAsync()
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 9; i++)
        {
            var pair = UgcDiscoverySelectionPolicy.CategoryKeywordsForScan(i);
            AssertEqual(2, pair.Count);
            foreach (var keyword in pair) AssertTrue(all.Add(keyword));
        }
        AssertEqual(18, all.Count);
        AssertTrue(all.Contains("Emote") && all.Contains("Waist") && all.Contains("PBR"));
        AssertTrue(UgcDiscoverySelectionPolicy.CategoryKeywordsForScan(0)
            .SequenceEqual(UgcDiscoverySelectionPolicy.CategoryKeywordsForScan(9)));
        AssertTrue(UgcDiscoverySelectionPolicy.CategoryKeywordsForScan(8)
            .SequenceEqual(UgcDiscoverySelectionPolicy.CategoryKeywordsForScan(-1)));
        return Task.CompletedTask;
    }

    static Task TestUgcDiscoveryMomentumRotationAsync()
    {
        var ranked = Enumerable.Range(1, 240).Select(x => (long)x).ToArray();
        var first = UgcDiscoverySelectionPolicy.Select(ranked, [], [], [], 120);
        var next = UgcDiscoverySelectionPolicy.Select(ranked, [], [], [], 120, rotation: 1);
        AssertEqual(120, first.Count);
        AssertEqual(120, next.Count);
        AssertEqual(120, next.Distinct().Count());
        AssertTrue(first.Take(32).SequenceEqual(next.Take(32)));
        AssertTrue(next.Any(id => !first.Contains(id)));
        var covered = new HashSet<long>();
        for (var i = 0; i < 8; i++)
            covered.UnionWith(UgcDiscoverySelectionPolicy.Select(ranked, [], [], [], 120, rotation: i));
        AssertEqual(240, covered.Count);
        return Task.CompletedTask;
    }
}
