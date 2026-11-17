namespace RobloxPriceTracker.Infrastructure;

public static class UgcHunterEntryPricePolicy
{
    // There is no universal 95 R$ floor. The official minimum varies by asset
    // type and can be affected by Roblox's regional/category experiments.
    // Eligibility is a paid, positive original-stock price; minimum-price
    // comparisons are separate and must be backed by verified evidence.
    public static bool IsEligiblePrimaryPrice(int price) => price > 0;
}
