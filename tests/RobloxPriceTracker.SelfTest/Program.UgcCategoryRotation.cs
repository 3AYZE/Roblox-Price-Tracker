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
}
