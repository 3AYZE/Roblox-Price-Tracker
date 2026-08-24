namespace RobloxPriceTracker.Core;

public static class PriceValidation
{
    // Deliberately generous guardrail. A malformed API number should never become an alertable price.
    public const long MaxRobux = 1_000_000_000_000;

    public static bool IsValidResalePrice(long? price) =>
        price is > 0 and <= MaxRobux;

    public static void ThrowIfInvalidTarget(long target)
    {
        if (!IsValidResalePrice(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), $"Target price must be between 1 and {MaxRobux:N0} Robux.");
        }
    }
}
