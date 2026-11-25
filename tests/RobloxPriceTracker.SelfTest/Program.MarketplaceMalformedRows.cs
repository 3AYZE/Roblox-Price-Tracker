using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestMarketplaceMalformedRowsAsync()
    {
        const string json = """
        [
          null, 42, [], "invalid",
          {"itemTargetId":1001,"collectibleItemId":"good","price":95,"sales":7,
           "unitsAvailableForConsumption":3,"assetStock":10,"saleLocationType":"Shop"},
          {"itemTargetId":1002,"collectibleItemId":"negative","price":95,"sales":-1,
           "unitsAvailableForConsumption":-1,"assetStock":10,"saleLocationType":"Shop"},
          {"itemTargetId":1003,"collectibleItemId":"empty","price":95,"sales":0,
           "unitsAvailableForConsumption":0,"assetStock":10,"saleLocationType":"Shop"}
        ]
        """;
        using var document = JsonDocument.Parse(json);
        var rows = RobloxMarketplaceItemParser.Parse(document.RootElement);
        AssertEqual(3, rows.Count);
        AssertEqual(3L, rows[0].UnitsAvailable!.Value);
        AssertEqual(7L, rows[0].PrimarySales!.Value);
        AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(1001, "good", rows[0]));
        AssertTrue(rows[1].UnitsAvailable is null);
        AssertTrue(rows[1].PrimarySales is null);
        AssertEqual("unknown original remaining",
            UgcActiveStockPolicy.MarketplaceRejectionReason(1002, "negative", rows[1]));
        AssertEqual(0L, rows[2].UnitsAvailable!.Value);
        AssertEqual(0L, rows[2].PrimarySales!.Value);
        AssertEqual("sold out original inventory",
            UgcActiveStockPolicy.MarketplaceRejectionReason(1003, "empty", rows[2]));
        return Task.CompletedTask;
    }

    static async Task TestMarketplacePartialBatchRecoveryAsync()
    {
        var calls = 0;
        var requested = new List<string[]>();
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            requested.Add(body.RootElement.GetProperty("itemIds").EnumerateArray()
                .Select(x => x.GetString()!).ToArray());
            var index = Interlocked.Increment(ref calls);
            var json = index == 1 ? """
                [{"collectibleItemId":"a","itemTargetId":101,"price":95,
                  "unitsAvailableForConsumption":2,"assetStock":10,"saleLocationType":"Shop"}]
                """ : """
                [{"collectibleItemId":"b","itemTargetId":102,"price":65,
                  "unitsAvailableForConsumption":5,"assetStock":10,"saleLocationType":"Shop"},
                 {"collectibleItemId":"a","itemTargetId":101,"price":249,
                  "unitsAvailableForConsumption":1,"assetStock":10,"saleLocationType":"Shop"}]
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        var dir = Path.Combine(Path.GetTempPath(), "rpt-partial-marketplace", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxMarketplaceItemService(client, new AppLogger(Path.Combine(dir, "test.log")));
            var rows = await service.GetManyAsync(["a", "b", "c"]);
            AssertEqual(3, calls);
            AssertTrue(requested[0].SequenceEqual(new[] { "a", "b", "c" }));
            AssertTrue(requested[1].SequenceEqual(new[] { "b" }));
            AssertTrue(requested[2].SequenceEqual(new[] { "c" }));
            AssertEqual(95, rows["a"].Price!.Value);
            AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(101, "a", rows["a"]));
            AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(102, "b", rows["b"]));
            AssertTrue(rows["c"].UnitsAvailable is null);
            AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(103, "c", rows["c"]));
            AssertTrue(rows["a"].VerifiedAtUtc is not null && rows["b"].VerifiedAtUtc is not null);
            var cached = await service.GetManyAsync(["a", "b", "c"]);
            AssertEqual(rows["a"].VerifiedAtUtc, cached["a"].VerifiedAtUtc);
            AssertEqual(3, calls);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
