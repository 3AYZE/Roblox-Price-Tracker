namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Prevents Hunter from treating a sharply repriced primary-market Limited as a fresh opportunity.
/// Hunter can use both its own locally observed primary-price low and Roblox market references such
/// as RAP / resale floor, so first-seen 95 -> 300 or 95 -> 68,000,000 style repricings are rejected.
/// </summary>
public static class UgcHunterPrimaryPricePolicy
{
    public const double LargeIncreaseMultiple = 1.75d;
    public const int MinimumLargeIncreaseRobux = 50;

    public static bool IsLargeIncrease(int currentPrice, int observedLowPrice) =>
        IsLargeIncreaseAgainstReference(currentPrice, observedLowPrice);

    public static bool IsMarketReferenceIncrease(
        int currentPrice,
        double? recentAveragePrice,
        long? currentResaleFloor)
    {
        if (currentPrice <= 0) return false;

        if (recentAveragePrice is > 0 &&
            IsLargeIncreaseAgainstReference(currentPrice, recentAveragePrice.Value))
            return true;

        if (currentResaleFloor is > 0 &&
            IsLargeIncreaseAgainstReference(currentPrice, currentResaleFloor.Value))
            return true;

        return false;
    }

    public static bool IsClearlyNonUgcPublisher(long creatorId, string? creatorName) =>
        creatorId == 1 ||
        string.Equals(creatorName?.Trim(), "Roblox", StringComparison.OrdinalIgnoreCase);

    private static bool IsLargeIncreaseAgainstReference(double currentPrice, double referencePrice)
    {
        if (currentPrice <= 0 || referencePrice <= 0 || currentPrice <= referencePrice)
            return false;

        var absoluteIncrease = currentPrice - referencePrice;
        var multiple = currentPrice / referencePrice;
        return absoluteIncrease >= MinimumLargeIncreaseRobux && multiple >= LargeIncreaseMultiple;
    }
}
