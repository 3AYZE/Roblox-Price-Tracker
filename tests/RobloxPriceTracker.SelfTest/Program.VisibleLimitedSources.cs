using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestVisibleLimitedSourceIsolationAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var ugc = LiveDropScopePolicy.DueVisibleSources("UGC", false, true, now, null, null, null);
        AssertTrue(ugc.Ugc && !ugc.Bundles && !ugc.Official);
        var packs = LiveDropScopePolicy.DueVisibleSources("UGC", true, true, now, null, null, null);
        AssertTrue(packs.Bundles && !packs.Ugc && !packs.Official);
        var official = LiveDropScopePolicy.DueVisibleSources("Official", true, true, now, null, null, null);
        AssertTrue(official.Official && !official.Ugc && !official.Bundles);
        var fresh = LiveDropScopePolicy.DueVisibleSources("UGC", false, false, now, null, now, null);
        AssertTrue(!fresh.Official && !fresh.Ugc && !fresh.Bundles);
        var due = LiveDropScopePolicy.DueVisibleSources("UGC", true, false, now, null, null, now.AddMinutes(-2));
        AssertTrue(due.Bundles && !due.Ugc);
        return Task.CompletedTask;
    }
}
