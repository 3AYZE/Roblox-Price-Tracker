using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcSellThroughAndAgeAsync()
    {
        bool Show(long? left, long? total, bool verified = true) =>
            UgcActiveStockPolicy.HasMinimumSellThrough(verified, left, total);
        AssertTrue(!Show(75, 75));
        AssertTrue(!Show(50, 75));
        AssertTrue(!Show(38, 75));
        AssertTrue(Show(37, 75));
        AssertTrue(Show(50, 100));
        AssertTrue(!Show(51, 100));
        AssertTrue(Show(1500, 3000));
        AssertTrue(Show(1, 75));
        AssertTrue(!Show(0, 75));
        AssertTrue(!Show(5, null));
        AssertTrue(!Show(5, 0));
        AssertTrue(!Show(80, 75));
        AssertTrue(!Show(5, 75, false));
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        AssertTrue(UgcActiveStockPolicy.IsWithinScannerAge(now, now));
        AssertTrue(UgcActiveStockPolicy.IsWithinScannerAge(now.AddMonths(-11), now));
        AssertTrue(!UgcActiveStockPolicy.IsWithinScannerAge(now.AddYears(-1), now));
        AssertTrue(!UgcActiveStockPolicy.IsWithinScannerAge(now.AddYears(-2), now));
        AssertTrue(UgcActiveStockPolicy.IsWithinScannerAge(null, now));
        return Task.CompletedTask;
    }
}
