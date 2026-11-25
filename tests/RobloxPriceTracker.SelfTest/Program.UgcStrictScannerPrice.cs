using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcStrictScannerPriceAsync()
    {
        const string good = UgcCategoryBuyingPricePolicy.Good;
        const string review = UgcCategoryBuyingPricePolicy.Insufficient;
        const string above = UgcCategoryBuyingPricePolicy.Above;
        bool Show(string status, int price, int? ceiling, int? historicalLow = null,
            int type = 8, string material = "STANDARD", bool excluded = false) =>
            UgcCategoryBuyingPricePolicy.IsLiveScannerListing(status, price, ceiling,
                excluded, historicalLow, type, material);
        AssertTrue(!Show(good, 295, 500)); // stale or over-permissive cached classification
        AssertTrue(!Show(good, 266, 500));
        AssertTrue(!Show(review, 295, 500, material: "UNKNOWN"));
        AssertTrue(!Show(above, 295, 95, 95));
        AssertTrue(!Show(good, 95, 95, 65)); // increase still within category limit
        AssertTrue(Show(good, 95, 95));
        AssertTrue(Show(review, 65, 65, type: 41, material: "UNKNOWN"));
        AssertTrue(!Show(good, 65, 65, type: 47)); // no trusted category reference
        AssertTrue(!Show(review, 295, null, material: "PBR"));
        AssertTrue(Show(good, 200, 200, material: "PBR"));
        AssertTrue(!Show(good, 200, 200, historicalLow: 95, material: "PBR"));
        AssertTrue(!Show(good, 95, 95, excluded: true));
        var offer = new UgcPriceIntegrityPolicy.PrimaryPriceObservation(
            1001, 2001, 8, 295, 13, DateTimeOffset.UtcNow, true, "RobloxMarketplacePrimary");
        AssertEqual(above, UgcCategoryBuyingPricePolicy.Evaluate(offer, "UNKNOWN", []).Classification);
        return Task.CompletedTask;
    }
}
