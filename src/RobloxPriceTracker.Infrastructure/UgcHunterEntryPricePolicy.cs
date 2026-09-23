namespace RobloxPriceTracker.Infrastructure;

public static class UgcHunterEntryPricePolicy
{
    // Roblox's current minimum paid UGC Limited primary-market entry tier.
    // Hunter only surfaces this exact tier; higher-priced drops do not bypass the gate.
    public const int MinimumPrimaryPrice = 95;

    public static bool IsEligiblePrimaryPrice(int price) =>
        price == MinimumPrimaryPrice;
}
