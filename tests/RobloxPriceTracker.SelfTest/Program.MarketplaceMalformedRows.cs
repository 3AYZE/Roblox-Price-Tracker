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
        AssertEqual(3L, rows[0].UnitsAvailable);
        AssertEqual(7L, rows[0].PrimarySales);
        AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(1001, "good", rows[0]));
        AssertTrue(rows[1].UnitsAvailable is null);
        AssertTrue(rows[1].PrimarySales is null);
        AssertEqual("unknown original remaining",
            UgcActiveStockPolicy.MarketplaceRejectionReason(1002, "negative", rows[1]));
        AssertEqual(0L, rows[2].UnitsAvailable);
        AssertEqual(0L, rows[2].PrimarySales);
        AssertEqual("sold out original inventory",
            UgcActiveStockPolicy.MarketplaceRejectionReason(1003, "empty", rows[2]));
        return Task.CompletedTask;
    }
}
