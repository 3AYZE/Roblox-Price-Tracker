namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Keep Roblox-published Limited assets separate from creator UGC assets and
/// animation bundles. UI source filters use this policy before category filters.
/// </summary>
public static class LiveDropScopePolicy
{
    public static bool Accepts(string scope, string productKind) =>
        scope switch
        {
            "Official" => productKind == "Official",
            "UGC" => productKind is "UGC" or "Bundle",
            _ => false
        };
}
