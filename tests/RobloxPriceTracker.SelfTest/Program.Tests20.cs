using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcScanPriceVisibilityAsync()
    {
        const int cap = UgcScannerPriceVisibilityPolicy.DefaultMaximumRobux;
        AssertEqual(500, cap);
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(750, true, cap, true));
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(754759847, true, cap, false));
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(5001, true, cap, false));
        AssertTrue(UgcScannerPriceVisibilityPolicy.Include(95, true, cap, false));
        AssertTrue(UgcScannerPriceVisibilityPolicy.Include(350, false, cap, false));
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(null, false, cap, false));
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(95, false, cap, true));
        AssertTrue(UgcScannerPriceVisibilityPolicy.Include(95, true, cap, true));
        AssertTrue(UgcScannerPriceVisibilityPolicy.Include(754759847, true, null, false));
        AssertTrue(UgcScannerPriceVisibilityPolicy.Include(null, false, null, false));
        AssertTrue(!UgcScannerPriceVisibilityPolicy.Include(null, false, null, true));
        return Task.CompletedTask;
    }

    static Task TestUgcPriceQuoteProvenanceAsync()
    {
        AssertEqual("95 R$ · verified",
            UgcScannerPriceVisibilityPolicy.DisplayQuote(95, 25));
        AssertEqual("65 R$ · catalog",
            UgcScannerPriceVisibilityPolicy.DisplayQuote(null, 65));
        AssertEqual("—", UgcScannerPriceVisibilityPolicy.DisplayQuote(null, null));
        AssertEqual("—", UgcScannerPriceVisibilityPolicy.DisplayQuote(0, 0));
        AssertEqual(95L, UgcScannerPriceVisibilityPolicy.SortQuote(95));
        AssertEqual(-95L, UgcScannerPriceVisibilityPolicy.SortQuote(95, descending: true));
        AssertEqual(long.MaxValue, UgcScannerPriceVisibilityPolicy.SortQuote(null));
        AssertEqual(long.MaxValue, UgcScannerPriceVisibilityPolicy.SortQuote(0, descending: true));
        return Task.CompletedTask;
    }
}
