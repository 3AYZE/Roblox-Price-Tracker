using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static async Task TestOfficialDiscoveryFailurePreservesRowsAsync()
    {
        var failed = false;
        using var handler = new DelegateHandler((request, _) => Task.FromResult(
            failed
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported route") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":[{"id":71001,"itemType":"Asset","name":"Live Official Limited",
                        "creatorTargetId":1,"creatorType":"User","assetType":8,
                        "favoriteCount":100,"itemRestrictions":["Limited"]}]}
                        """)
                }));
        using var http = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "rpt-official-refresh", Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(Path.Combine(root, "test.log"));
            var service = new RobloxOfficialLimitedMarketService(http, logger,
                new RobloxResaleDataService(http, logger), Path.Combine(root, "state.json"));
            var fresh = await service.ScanAsync();
            AssertTrue(fresh.DiscoveryComplete);
            AssertEqual(1, fresh.Items.Count);
            AssertTrue(OfficialLimitedRefreshPolicy.ShouldReplaceRows(fresh));

            failed = true;
            var unavailable = await service.ScanAsync();
            AssertTrue(!unavailable.DiscoveryComplete);
            AssertEqual(0, unavailable.Items.Count);
            AssertTrue(!OfficialLimitedRefreshPolicy.ShouldReplaceRows(unavailable));

            // The selection policy also permits an authoritative empty success.
            var emptySuccess = unavailable with { DiscoveryComplete = true };
            AssertTrue(OfficialLimitedRefreshPolicy.ShouldReplaceRows(emptySuccess));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static Task TestOfficialLimitedParserKeepsRobloxLimitedsAsync()
    {
        const string json = """
        {
          "nextPageCursor": "next-page",
          "data": [
            {
              "id": 1001,
              "itemType": "Asset",
              "name": "Official Limited",
              "creatorTargetId": 1,
              "creatorType": "User",
              "assetType": 8,
              "lowestPrice": 950,
              "collectibleItemId": "6fc33531-3a2d-4c27-9d47-74f4bdc2d6f0",
              "favoriteCount": 12000,
              "purchaseCount": 5000,
              "itemRestrictions": ["Limited"]
            },
            {
              "id": 1004,
              "itemType": "Asset",
              "name": "Official Invalid GUID",
              "creatorTargetId": 1,
              "creatorType": "User",
              "assetType": 8,
              "lowestPrice": 800,
              "collectibleItemId": "not-a-valid-guid",
              "itemRestrictions": ["Limited"]
            },
            {
              "id": 1002,
              "itemType": "Asset",
              "name": "UGC Limited",
              "creatorTargetId": 12345,
              "creatorType": "Group",
              "assetType": 8,
              "lowestPrice": 100,
              "itemRestrictions": ["Limited"]
            },
            {
              "id": 1003,
              "itemType": "Asset",
              "name": "Roblox Regular",
              "creatorTargetId": 1,
              "creatorType": "User",
              "assetType": 8,
              "price": 50,
              "itemRestrictions": []
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var parsed = RobloxOfficialLimitedCatalogParser.Parse(doc.RootElement);
        AssertEqual(2, parsed.Items.Count);
        AssertEqual(1001L, parsed.Items[0].AssetId);
        AssertEqual(950L, parsed.Items[0].FloorPrice!.Value);
        AssertEqual("6fc33531-3a2d-4c27-9d47-74f4bdc2d6f0", parsed.Items[0].CollectibleItemId);
        AssertTrue(parsed.Items[1].CollectibleItemId is null);
        AssertEqual("next-page", parsed.NextPageCursor);
        return Task.CompletedTask;
    }

    static Task TestOfficialHuntRanksLiquidDiscountAsync()
    {
        var item = new RobloxOfficialLimitedCatalogItem(2001, "Liquid Deal", 800, null, 25000, 10000, 8, 80);
        var market = MakeOfficialMarket(2001, 1100, dailySales: 6, price: 1050);
        var score = OfficialLimitedHuntScoring.Evaluate(item, market, previousFloor: 800, isFresh: false);

        AssertTrue(score.IsHuntCandidate);
        AssertTrue(score.HuntScore >= 70);
        AssertTrue(score.DiscountPct is > 0.20);
        AssertTrue(score.EvidenceScore >= 80);
        AssertTrue(score.Status is "BUY ZONE" or "BEST BUY");
        return Task.CompletedTask;
    }

    static Task TestOfficialHuntRejectsFakeDeepDiscountWithoutLiquidityAsync()
    {
        var item = new RobloxOfficialLimitedCatalogItem(2002, "Thin Fake Deal", 200, null, 2000, 100, 8, 70);
        var pricePoints = Enumerable.Range(0, 14)
            .Select(i => new ResaleDataPoint(DateTimeOffset.UtcNow.Date.AddDays(-13 + i), 950))
            .ToArray();
        var market = new RobloxResaleMarketData(
            2002,
            true,
            "test",
            100,
            1000,
            pricePoints,
            Array.Empty<ResaleDataPoint>(),
            null,
            null);

        var score = OfficialLimitedHuntScoring.Evaluate(item, market, previousFloor: 200, isFresh: false);
        AssertTrue(score.DiscountPct is > 0.70);
        AssertTrue(!score.IsHuntCandidate);
        AssertTrue(score.HuntScore <= 45);
        return Task.CompletedTask;
    }

    static Task TestOfficialHuntDetectsPriceDropAsync()
    {
        var item = new RobloxOfficialLimitedCatalogItem(2003, "Fresh Floor Drop", 800, null, 18000, 9000, 8, 80);
        var market = MakeOfficialMarket(2003, 1100, dailySales: 6, price: 1050);
        var score = OfficialLimitedHuntScoring.Evaluate(item, market, previousFloor: 1000, isFresh: false);

        AssertTrue(score.IsHuntCandidate);
        AssertTrue(score.FloorChangePct is <= -0.19);
        AssertEqual("PRICE DROP", score.Status);
        return Task.CompletedTask;
    }

    private static RobloxResaleMarketData MakeOfficialMarket(long assetId, double rap, double dailySales, double price)
    {
        var now = DateTimeOffset.UtcNow.Date;
        var pricePoints = Enumerable.Range(0, 30)
            .Select(i => new ResaleDataPoint(now.AddDays(-29 + i), price + (i % 3 - 1) * 5))
            .ToArray();
        var volumePoints = Enumerable.Range(0, 30)
            .Select(i => new ResaleDataPoint(now.AddDays(-29 + i), dailySales))
            .ToArray();
        return new RobloxResaleMarketData(
            assetId,
            true,
            "test",
            5000,
            rap,
            pricePoints,
            volumePoints,
            null,
            null);
    }
}
