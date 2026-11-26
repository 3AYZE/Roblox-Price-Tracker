namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Keep Roblox-published Limited assets separate from creator UGC assets and
/// animation bundles. UI source filters use this policy before category filters.
/// </summary>
public static class LiveDropScopePolicy
{
    public readonly record struct SourcePlan(bool Official, bool Ugc, bool Bundles);

    public static SourcePlan DueSources(
        string scope, bool force, DateTimeOffset now,
        DateTimeOffset? lastOfficial, DateTimeOffset? lastUgc, DateTimeOffset? lastBundles)
    {
        if (scope == "Official")
            return new SourcePlan(
                force || lastOfficial is null || now - lastOfficial > TimeSpan.FromMinutes(5),
                false, false);
        if (scope == "UGC")
            return new SourcePlan(
                false,
                force || lastUgc is null || now - lastUgc >= TimeSpan.FromSeconds(60),
                force || lastBundles is null || now - lastBundles > TimeSpan.FromSeconds(105));
        return default;
    }

    // Animation packs are research-only and visible in their dedicated filter.
    // Do not consume accessory refresh budget or report bundle failures on other filters.
    public static SourcePlan DueVisibleSources(string scope, bool animationPacksSelected, bool force,
        DateTimeOffset now, DateTimeOffset? lastOfficial, DateTimeOffset? lastUgc,
        DateTimeOffset? lastBundles)
    {
        var due = DueSources(scope, force, now, lastOfficial, lastUgc, lastBundles);
        return scope != "UGC" ? due :
            animationPacksSelected ? new SourcePlan(false, false, due.Bundles) :
            new SourcePlan(false, due.Ugc, false);
    }

    public static bool Accepts(string scope, string productKind) =>
        scope switch
        {
            "Official" => productKind == "Official",
            "UGC" => productKind is "UGC" or "Bundle",
            _ => false
        };
}
