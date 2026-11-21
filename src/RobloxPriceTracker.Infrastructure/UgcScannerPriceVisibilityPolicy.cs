namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Presentation-only guardrails for the UGC discovery table. Catalog prices
/// are unverified quotes, never independently confirmed creator-stock costs.
/// An adjustable display ceiling hides implausibly expensive quotes by default;
/// it is not an estimate of the market's true minimum or fair value.
/// </summary>
public static class UgcScannerPriceVisibilityPolicy
{
    public const int DefaultMaximumRobux = 500;

    public static bool Include(int? quotedPrice, bool primaryVerified, int? maxPriceRobux,
        bool verifiedOnly)
    {
        if (verifiedOnly && !primaryVerified) return false;
        if (maxPriceRobux is null)
            return true; // Explicit Any price mode may include unpriced research rows.
        return quotedPrice is > 0 && quotedPrice <= maxPriceRobux;
    }

    public static string DisplayQuote(int? verifiedOriginalPrice, int? catalogQuote)
    {
        if (verifiedOriginalPrice is > 0)
            return $"{verifiedOriginalPrice:N0} R$ · verified";
        if (catalogQuote is > 0)
            return $"{catalogQuote:N0} R$ · catalog";
        return "—";
    }

    public static long SortQuote(int? quotedPrice, bool descending = false)
    {
        if (quotedPrice is not > 0) return long.MaxValue; // Unknown prices sort last.
        return descending ? -(long)quotedPrice.Value : quotedPrice.Value;
    }
}
