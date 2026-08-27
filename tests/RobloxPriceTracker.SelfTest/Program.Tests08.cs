using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcAnalyzerCatalogParserAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 129868485129975,
              "itemType": "Asset",
              "name": "[ANIMATED] White Heart Aura",
              "creatorName": "Reference Creator",
              "creatorTargetId": 123456,
              "creatorType": "Group",
              "assetType": 46,
              "price": 1000,
              "purchaseCount": 3000,
              "unitsAvailableForConsumption": 0,
              "totalQuantity": 3000,
              "favoriteCount": 5000,
              "collectibleItemId": "reference-collectible",
              "saleLocationType": "ShopOnly",
              "itemRestrictions": ["Collectible"]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var items = RobloxCatalogAssetDetailParser.ParseMany(doc.RootElement);
        AssertEqual(1, items.Count);
        var item = items[0];
        AssertEqual(129868485129975L, item.AssetId);
        AssertEqual("[ANIMATED] White Heart Aura", item.Name);
        AssertEqual("Group", item.CreatorType);
        AssertEqual(1000, item.Price!.Value);
        AssertEqual(3000L, item.PurchaseCount!.Value);
        AssertEqual(0L, item.UnitsAvailable!.Value);
        AssertTrue(item.IsCollectible);
        AssertTrue(!item.IsShopPurchasable);
        return Task.CompletedTask;
    }

    static Task TestUgcAnalyzerDataQualityAsync()
    {
        var catalog = new RobloxCatalogAssetDetail(
            129868485129975L,
            "[ANIMATED] White Heart Aura",
            "Reference Creator",
            123456,
            "Group",
            46,
            1000,
            3000,
            0,
            3000,
            5000,
            "reference-collectible",
            "ShopOnly",
            false,
            true);
        var marketplace = new RobloxMarketplaceItemData(
            catalog.AssetId,
            "reference-collectible",
            true,
            1000,
            3000,
            0,
            3000,
            999,
            true,
            "ShopOnly",
            0,
            "reference-product",
            null);
        var resale = new RobloxResaleMarketData(
            catalog.AssetId,
            true,
            "Roblox Marketplace Sales",
            3000,
            706,
            Array.Empty<ResaleDataPoint>(),
            new[] { new ResaleDataPoint(DateTimeOffset.UtcNow, 11) },
            "reference-collectible",
            null);
        var resellers = new RobloxResellerMarketData(
            catalog.AssetId,
            true,
            "reference-collectible",
            15,
            false,
            999,
            1050,
            1100,
            5,
            9,
            null);

        var quality = UgcDataQualityEvaluator.Evaluate(catalog, marketplace, resale, resellers, 999);
        AssertEqual(100, quality.Score);
        AssertEqual("VERIFIED", quality.Label);
        AssertEqual(0, quality.Missing.Count);
        return Task.CompletedTask;
    }

    static Task TestCreatorIntelligenceTrackRecordAsync()
    {
        var items = new[]
        {
            new RobloxCreatorLimitedItem(1, "A", 100, 100, 0, 100, 300, true),
            new RobloxCreatorLimitedItem(2, "B", 100, 100, 0, 100, 250, true),
            new RobloxCreatorLimitedItem(3, "C", 100, 80, 20, 100, 150, true),
            new RobloxCreatorLimitedItem(4, "D", 100, 20, 80, 100, null, true)
        };

        var creator = RobloxCreatorIntelligenceCalculator.Build(123, "Creator", "Group", items);
        AssertEqual(4, creator.SampleSize);
        AssertEqual(3, creator.FloorSampleCount);
        AssertEqual(2, creator.ProfitableAtFloorCount);
        AssertEqual("STRONG", creator.TrackRecord);
        AssertTrue(creator.AverageSellThrough is > 0.70 and < 0.80);
        return Task.CompletedTask;
    }
}
