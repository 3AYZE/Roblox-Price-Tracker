using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestOfficialAndUgcSourcesAreSeparateAsync()
    {
        AssertTrue(LiveDropScopePolicy.Accepts("Official", "Official"));
        AssertTrue(!LiveDropScopePolicy.Accepts("Official", "UGC"));
        AssertTrue(!LiveDropScopePolicy.Accepts("Official", "Bundle"));
        AssertTrue(LiveDropScopePolicy.Accepts("UGC", "UGC"));
        AssertTrue(LiveDropScopePolicy.Accepts("UGC", "Bundle"));
        AssertTrue(!LiveDropScopePolicy.Accepts("UGC", "Official"));
        AssertTrue(!LiveDropScopePolicy.Accepts("Unknown", "Official"));
        AssertTrue(!LiveDropScopePolicy.Accepts("Unknown", "Bundle"));
        return Task.CompletedTask;
    }

    static Task TestLimitedSourceSpecificRefreshAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var official = LiveDropScopePolicy.DueSources("Official", true, now, null, null, null);
        AssertTrue(official.Official && !official.Ugc && !official.Bundles);

        var ugc = LiveDropScopePolicy.DueSources("UGC", true, now, null, null, null);
        AssertTrue(!ugc.Official && ugc.Ugc && ugc.Bundles);

        var officialFresh = LiveDropScopePolicy.DueSources("Official", false, now,
            now - TimeSpan.FromMinutes(2), null, null);
        AssertTrue(!officialFresh.Official && !officialFresh.Ugc && !officialFresh.Bundles);

        var onlyUgcExpired = LiveDropScopePolicy.DueSources("UGC", false, now,
            now - TimeSpan.FromDays(1), now - TimeSpan.FromMinutes(4),
            now - TimeSpan.FromMinutes(1));
        AssertTrue(!onlyUgcExpired.Official && onlyUgcExpired.Ugc && !onlyUgcExpired.Bundles);

        var onlyBundlesExpired = LiveDropScopePolicy.DueSources("UGC", false, now,
            now - TimeSpan.FromDays(1), now - TimeSpan.FromSeconds(59),
            now - TimeSpan.FromMinutes(6));
        AssertTrue(!onlyBundlesExpired.Official && !onlyBundlesExpired.Ugc && onlyBundlesExpired.Bundles);

        var earlyRecheck = LiveDropScopePolicy.DueSources("UGC", false, now,
            null, now.AddSeconds(-60), now.AddSeconds(-60));
        AssertTrue(earlyRecheck.Ugc && !earlyRecheck.Bundles);
        AssertTrue(UgcActiveStockPolicy.IsActiveSnapshot(true, 12, 95, now.AddSeconds(-60), now));

        var unknown = LiveDropScopePolicy.DueSources("Other", true, now, null, null, null);
        AssertTrue(!unknown.Official && !unknown.Ugc && !unknown.Bundles);
        return Task.CompletedTask;
    }
}
