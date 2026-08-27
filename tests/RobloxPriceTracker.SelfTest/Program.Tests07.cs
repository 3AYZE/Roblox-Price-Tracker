using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestMarketplaceItemParserUsesWhiteHeartAuraReferenceAsync()
    {
        // Frozen from Roblox Marketplace Items on 2026-08-27 for asset 129868485129975.
        const string json = """
        [{
          "collectibleItemId":"248c3ecd-12df-4fc6-8485-92c4d05bcfbf",
          "collectibleProductId":"3b87985b-560e-4d72-abb5-bdcece434bf7",
          "itemTargetId":129868485129975,
          "price":1000,
          "lowestPrice":999,
          "hasResellers":true,
          "unitsAvailableForConsumption":0,
          "assetStock":3000,
          "errorCode":null,
          "saleLocationType":"ShopAndAllExperiences",
          "sales":3000,
          "lowestResalePrice":999,
          "quantityLimitPerUser":0,
          "resaleRestriction":1,
          "productSaleStatus":3,
          "productTargetId":3251477180428675
        }]
        """;

        using var document = JsonDocument.Parse(json);
        var rows = RobloxMarketplaceItemParser.Parse(document.RootElement);
        AssertEqual(1, rows.Count);
        var item = rows[0];
        AssertEqual(129868485129975L, item.AssetId);
        AssertEqual("248c3ecd-12df-4fc6-8485-92c4d05bcfbf", item.CollectibleItemId);
        AssertEqual(1000, item.Price!.Value);
        AssertEqual(3000L, item.PrimarySales!.Value);
        AssertEqual(0L, item.UnitsAvailable!.Value);
        AssertEqual(3000L, item.TotalStock!.Value);
        AssertEqual(999L, item.LowestResalePrice!.Value);
        AssertTrue(item.HasResellers);
        AssertTrue(!item.IsPrimaryPurchasable);
        return Task.CompletedTask;
    }

    static Task TestWhiteHeartAuraReferenceIsUnprofitableAsync()
    {
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            OriginalPrice: 1000,
            TotalSupply: 3000,
            PurchaseCount: 3000,
            UnitsAvailable: 0,
            VelocityPerMinute: 0,
            Acceleration: 0,
            FavoriteCount: 1234,
            ObservationCount: 4,
            RecentAveragePrice: 706,
            LowestResalePrice: 999,
            ObservedResellers: 15,
            ResellerBookTruncated: false,
            ListingsWithin10Pct: 2,
            ListingsWithin20Pct: 2,
            SalesLast7d: 11,
            SalesLast30d: 129,
            MedianTop10: 2100,
            HasLiveResaleMarket: true));

        AssertEqual(2000L, score.BreakEvenResale);
        AssertTrue(score.BaseNetRoi < 0);
        AssertEqual("AVOID", score.Recommendation);
        AssertTrue(score.ResalePotential <= 35);
        return Task.CompletedTask;
    }
}
