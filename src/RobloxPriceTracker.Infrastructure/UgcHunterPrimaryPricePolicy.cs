namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Prevents Hunter from treating a sharply repriced primary-market Limited as a fresh opportunity.
/// The reference price is the lowest primary price the local Hunter has actually observed for the asset.
/// </summary>
public static class UgcHunterPrimaryPricePolicy
{
    public const double LargeIncreaseMultiple = 1.75d;
    public const int MinimumLargeIncreaseRobux = 50;

    public static bool IsLargeIncrease(int currentPrice, int observedLowPrice)
    {
        if (currentPrice <= 0 || observedLowPrice <= 0 || currentPrice <= observedLowPrice)
            return false;

        var absoluteIncrease = currentPrice - observedLowPrice;
        var multiple = currentPrice / (double)observedLowPrice;
        return absoluteIncrease >= MinimumLargeIncreaseRobux && multiple >= LargeIncreaseMultiple;
    }
}
