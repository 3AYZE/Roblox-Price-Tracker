using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
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
              "favoriteCount": 12000,
              "purchaseCount": 5000,
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
        AssertEqual(1, parsed.Items.Count);
        AssertEqual(1001L, parsed.Items[0].AssetId);
        AssertEqual(950L, parsed.Items[0].FloorPrice!.Value);
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
