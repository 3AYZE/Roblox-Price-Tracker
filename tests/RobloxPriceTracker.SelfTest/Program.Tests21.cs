using System.Net;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestActiveUgcOriginalStockProofAsync()
    {
        var good = new RobloxMarketplaceItemData(
            111, "collectible-111", true, 95, 20, 12, 100, 350, true,
            "ShopAndAllExperiences", 1, null, null);
        AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111", good));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(112, "collectible-111", good));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "wrong-id", good));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, null, good));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111", null));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { UnitsAvailable = 0, LowestResalePrice = 50, HasResellers = true }));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { UnitsAvailable = null, HasResellers = true }));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { Price = null }));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { SaleLocationType = "ExperienceByPlaceId" }));
        AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { TotalStock = 5, UnitsAvailable = 12 }));
        AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(111, "collectible-111",
            good with { TotalStock = null })); // Remaining count is verified; total need not exist.
        return Task.CompletedTask;
    }

    static Task TestActiveUgcStockFreshnessAsync()
    {
        var now = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
        AssertTrue(UgcActiveStockPolicy.IsActiveSnapshot(true, 10, 95, now.AddSeconds(-30), now));
        AssertTrue(UgcActiveStockPolicy.IsActiveSnapshot(true, 1, 95, now.AddSeconds(-120), now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, 10, 95, now.AddSeconds(-121), now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, 10, 95, null, now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(false, 10, 95, now, now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, 0, 95, now, now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, null, 95, now, now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, 10, 0, now, now));
        AssertTrue(!UgcActiveStockPolicy.IsActiveSnapshot(true, 10, 95, now.AddSeconds(1), now));
        return Task.CompletedTask;
    }

    static async Task TestUgcDiscoveryRejectsUnverifiedOrResaleOnlyAsync()
    {
        const string search = """
        {"data":[
          {"id":1101,"itemType":"Asset","creatorTargetId":22,"assetType":8},
          {"id":1102,"itemType":"Asset","creatorTargetId":23,"assetType":8},
          {"id":1103,"itemType":"Asset","creatorTargetId":24,"assetType":8},
          {"id":1104,"itemType":"Asset","creatorTargetId":25,"assetType":8}
        ]}
        """;
        const string detail = """
        {"data":[
          {"id":1101,"itemType":"Asset","name":"Active hat","creatorTargetId":22,
           "assetType":8,"creatorName":"Creator","price":95,
           "unitsAvailableForConsumption":12,"totalQuantity":100,
           "saleLocationType":"ShopAndAllExperiences",
           "collectibleItemId":"c1101","itemRestrictions":["Limited"]},
          {"id":1102,"itemType":"Asset","name":"Resale only","creatorTargetId":23,
           "assetType":8,"creatorName":"Creator","price":95,
           "unitsAvailableForConsumption":12,
           "saleLocationType":"ShopAndAllExperiences",
           "collectibleItemId":"c1102","itemRestrictions":["Limited"]},
          {"id":1103,"itemType":"Asset","name":"Unknown stock","creatorTargetId":24,
           "assetType":8,"creatorName":"Creator","price":95,
           "unitsAvailableForConsumption":12,
           "saleLocationType":"ShopAndAllExperiences",
           "collectibleItemId":"c1103","itemRestrictions":["Limited"]},
          {"id":1104,"itemType":"Asset","name":"Mismatched ID","creatorTargetId":25,
           "assetType":8,"creatorName":"Creator","price":95,
           "unitsAvailableForConsumption":12,
           "saleLocationType":"ShopAndAllExperiences",
           "collectibleItemId":"c1104","itemRestrictions":["Limited"]}
        ]}
        """;
        const string marketplace = """
        [
          {"collectibleItemId":"c1101","itemTargetId":1101,"price":95,
           "unitsAvailableForConsumption":7,"assetStock":100,
           "saleLocationType":"ShopAndAllExperiences"},
          {"collectibleItemId":"c1102","itemTargetId":1102,"price":95,
           "unitsAvailableForConsumption":0,"lowestResalePrice":500,
           "hasResellers":true,"saleLocationType":"ShopAndAllExperiences"},
          {"collectibleItemId":"c1103","itemTargetId":1103,"price":95,
           "lowestResalePrice":150,"hasResellers":true,
           "saleLocationType":"ShopAndAllExperiences"},
          {"collectibleItemId":"c1104","itemTargetId":9999,"price":95,
           "unitsAvailableForConsumption":30,"assetStock":100,
           "saleLocationType":"ShopAndAllExperiences"}
        ]
        """;
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            var uri = request.RequestUri?.AbsolutePath ?? "";
            var body = uri.Contains("/search/items/details", StringComparison.Ordinal) ? search :
                uri.Contains("/catalog/items/details", StringComparison.Ordinal) ? detail :
                uri.Contains("/marketplace-items/v1/items/details", StringComparison.Ordinal) ? marketplace :
                throw new InvalidOperationException("Unexpected UGC endpoint " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }));
        var directory = Path.Combine(Path.GetTempPath(), "rpt-active-stock", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxUgcDiscoveryService(
                client, new AppLogger(Path.Combine(directory, "test.log")), expandDiscovery: false);
            var result = await service.DiscoverAsync();
            AssertEqual(1, result.Items.Count);
            var item = result.Items.Single();
            AssertEqual(1101L, item.AssetId);
            AssertTrue(item.PrimaryMarketVerified);
            AssertEqual(7L, item.UnitsAvailable);
            AssertEqual(95, item.Price);
            AssertTrue(requests >= 3);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
