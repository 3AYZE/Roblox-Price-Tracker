using System.Net;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestVerifiedStockDepletionAsync()
    {
        var now = DateTimeOffset.UtcNow;
        UgcActiveStockPolicy.DepletionObservation Point(long? left, int seconds, bool verified = true, long? total = 100) =>
            new(left, total, now.AddSeconds(seconds), verified);
        var window = TimeSpan.FromMinutes(5);
        double Rate(params UgcActiveStockPolicy.DepletionObservation[] points) =>
            UgcActiveStockPolicy.DepletionPerMinute(points, window);
        AssertEqual(10d, Rate(Point(30, -60), Point(20, 0)));
        AssertEqual(0d, Rate(Point(30, -60, false), Point(20, 0)));
        AssertEqual(0d, Rate(Point(30, -60), Point(20, 0, false)));
        AssertEqual(0d, Rate(Point(30, -60), Point(20, 0, total: 80)));
        AssertEqual(0d, Rate(Point(30, -600), Point(20, 0)));
        AssertEqual(0d, Rate(Point(30, -60), Point(null, 0)));
        AssertEqual(0d, Rate(Point(10, -120), Point(5, -60), Point(20, 0)));
        AssertEqual(0d, Rate(Point(30, -5), Point(20, 0)));
        return Task.CompletedTask;
    }

    static async Task TestPermanentPriceAnchorsAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpt-price-anchors", Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new UgcPriceIntegrityLedger(dir);
            await ledger.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var first = new UgcPriceIntegrityPolicy.PrimaryPriceObservation(
                7001, 8001, 8, 95, 20, now.AddDays(-200), true, "RobloxMarketplacePrimary");
            ledger.Observe(first);
            for (var i = 0; i < 45; i++)
                ledger.Observe(first with { PrimaryPrice = 150 + i, ObservedAtUtc = now.AddDays(-45 + i) });
            await ledger.SaveIfChangedAsync();
            var restored = new UgcPriceIntegrityLedger(dir);
            await restored.InitializeAsync();
            AssertTrue(restored.History(7001).Contains(first));
            var current = first with { PrimaryPrice = 249, ObservedAtUtc = now };
            var decision = UgcCategoryBuyingPricePolicy.Evaluate(current, "STANDARD", restored.History(7001));
            AssertEqual(UgcCategoryBuyingPricePolicy.Above, decision.Classification);
            AssertEqual(95, decision.EarlierVerifiedPrice!.Value);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static async Task TestMarketplaceConflictingRowsAsync()
    {
        const string json = """
        [
          {"collectibleItemId":"conflict","itemTargetId":1,"price":95,"unitsAvailableForConsumption":2,"assetStock":10,"saleLocationType":"Shop"},
          {"collectibleItemId":"conflict","itemTargetId":1,"price":249,"unitsAvailableForConsumption":1,"assetStock":10,"saleLocationType":"Shop"},
          {"collectibleItemId":"valid","itemTargetId":2,"price":65,"unitsAvailableForConsumption":3,"assetStock":10,"saleLocationType":"Shop"}
        ]
        """;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));
        var dir = Path.Combine(Path.GetTempPath(), "rpt-conflicting-marketplace", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxMarketplaceItemService(client, new AppLogger(Path.Combine(dir, "test.log")));
            var rows = await service.GetManyAsync(["conflict", "valid"]);
            AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(1, "conflict", rows["conflict"]));
            AssertTrue(rows["conflict"].UnitsAvailable is null);
            AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(2, "valid", rows["valid"]));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
