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
        AssertEqual("mismatched asset or collectible ID",
            UgcActiveStockPolicy.MarketplaceRejectionReason(112, "collectible-111", good));
        AssertEqual("sold out original inventory", UgcActiveStockPolicy.MarketplaceRejectionReason(
            111, "collectible-111", good with { UnitsAvailable = 0 }));
        AssertEqual("unknown original remaining", UgcActiveStockPolicy.MarketplaceRejectionReason(
            111, "collectible-111", good with { UnitsAvailable = null }));
        AssertEqual("unknown or zero primary price", UgcActiveStockPolicy.MarketplaceRejectionReason(
            111, "collectible-111", good with { Price = 0 }));
        AssertEqual("not purchasable in Shop", UgcActiveStockPolicy.MarketplaceRejectionReason(
            111, "collectible-111", good with { SaleLocationType = "ExperiencesDevApiOnly" }));
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
          {"id":1104,"itemType":"Asset","creatorTargetId":25,"assetType":8},
          {"id":1105,"itemType":"Asset","creatorTargetId":26,"assetType":41},
          {"id":1106,"itemType":"Asset","creatorTargetId":27,"assetType":46},
          {"id":1107,"itemType":"Asset","creatorTargetId":28,"assetType":43}
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
           "collectibleItemId":"c1104","itemRestrictions":["Limited"]},
          {"id":1105,"itemType":"Asset","name":"Zero catalog price hair","creatorTargetId":26,
           "assetType":41,"creatorName":"Creator","price":0,
           "unitsAvailableForConsumption":10,
           "collectibleItemId":"c1105","itemRestrictions":["Limited"]},
          {"id":1106,"itemType":"Asset","name":"Missing catalog location back","creatorTargetId":27,
           "assetType":46,"creatorName":"Creator","price":95,
           "collectibleItemId":"c1106","itemRestrictions":["Limited"]},
          {"id":1107,"itemType":"Asset","name":"Missing original stock","creatorTargetId":28,
           "assetType":43,"creatorName":"Creator","price":95,
           "collectibleItemId":"c1107","itemRestrictions":["Limited"]}
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
           "saleLocationType":"ShopAndAllExperiences"},
          {"collectibleItemId":"c1105","itemTargetId":1105,"price":65,
           "unitsAvailableForConsumption":9,"assetStock":100,
           "saleLocationType":"ShopAndAllExperiences"},
          {"collectibleItemId":"c1106","itemTargetId":1106,"price":100,
           "unitsAvailableForConsumption":20,"assetStock":200,
           "saleLocationType":"ShopOnly"},
          {"collectibleItemId":"c1107","itemTargetId":1107,"price":100,
           "lowestResalePrice":140,"hasResellers":true,
           "saleLocationType":"ShopOnly"}
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
            AssertEqual(3, result.Items.Count);
            var item = result.Items.Single(x => x.AssetId == 1101);
            AssertEqual(1101L, item.AssetId);
            AssertTrue(item.PrimaryMarketVerified);
            AssertEqual(7L, item.UnitsAvailable);
            AssertEqual(95, item.Price);
            var hair = result.Items.Single(x => x.AssetId == 1105);
            AssertEqual("Hair", hair.Category);
            AssertEqual(65, hair.Price);
            AssertEqual(9L, hair.UnitsAvailable!.Value);
            AssertTrue(hair.PrimaryMarketVerified);
            var back = result.Items.Single(x => x.AssetId == 1106);
            AssertEqual("Back", back.Category);
            AssertEqual(100, back.Price);
            AssertTrue(result.Items.All(x => x.AssetId != 1107));
            AssertTrue(requests >= 3);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    static async Task TestMarketplaceEmptyBatchRecoveryAsync()
    {
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            var index = Interlocked.Increment(ref calls);
            var body = index == 2 ? """
                [{"collectibleItemId":"c901","itemTargetId":901,"price":65,
                  "unitsAvailableForConsumption":12,"assetStock":60,
                  "saleLocationType":"ShopAndAllExperiences"}]
                """ : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) });
        }));
        var dir = Path.Combine(Path.GetTempPath(), "rpt-empty-marketplace", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxMarketplaceItemService(client, new AppLogger(Path.Combine(dir, "test.log")));
            var rows = await service.GetManyAsync(["c901", "c902"]);
            AssertEqual(3, calls); // Initial empty batch, then two bounded recovery chunks.
            AssertTrue(UgcActiveStockPolicy.IsMarketplaceVerified(901, "c901", rows["c901"]));
            AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(902, "c902", rows["c902"]));
            AssertEqual(12L, rows["c901"].UnitsAvailable!.Value);
            AssertTrue(rows["c902"].UnitsAvailable is null);
            await service.GetManyAsync(["c901", "c902", "c901"]);
            AssertEqual(3, calls); // Cached valid and missing rows; duplicate ID does not refetch.
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    static async Task TestMarketplaceRateLimitCooldownAsync()
    {
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        }));
        var dir = Path.Combine(Path.GetTempPath(), "rpt-marketplace-429", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxMarketplaceItemService(client, new AppLogger(Path.Combine(dir, "test.log")));
            var first = await service.GetManyAsync(["c991"]);
            var second = await service.GetManyAsync(["c992"]);
            AssertEqual(1, calls); // Retry-After blocks fanout, including previously unseen IDs.
            AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(991, "c991", first["c991"]));
            AssertTrue(!UgcActiveStockPolicy.IsMarketplaceVerified(992, "c992", second["c992"]));
            AssertTrue(second["c992"].Error?.Contains("cooldown", StringComparison.Ordinal) == true);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
