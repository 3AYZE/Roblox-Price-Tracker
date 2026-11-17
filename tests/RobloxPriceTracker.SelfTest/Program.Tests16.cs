using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcMaterialClassSeparationAsync()
    {
        var offers = new[]
        {
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(1, 8, 95, true, 20, false, false),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(2, 8, 500, true, 20, true, true),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(3, 8, 350, true, 20, null, null),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(4, 8, 120, true, 20, false, true),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(5, 8, 50, false, 20, false, false),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(6, 8, 25, true, 0, false, false),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(7, 41, 65, true, 20, false, false)
        };
        var lows = UgcCategoryPrimaryPricePolicy.FindLowestObservedOriginalPricesByMaterial(offers);
        AssertEqual(5, lows.Count);
        AssertEqual(95, lows[new(8, UgcCategoryPrimaryPricePolicy.Standard)]);
        AssertEqual(500, lows[new(8, UgcCategoryPrimaryPricePolicy.Emissive)]);
        AssertEqual(350, lows[new(8, UgcCategoryPrimaryPricePolicy.Unknown)]);
        AssertEqual(120, lows[new(8, UgcCategoryPrimaryPricePolicy.Pbr)]);
        AssertEqual(65, lows[new(41, UgcCategoryPrimaryPricePolicy.Standard)]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown,
            UgcCategoryPrimaryPricePolicy.MaterialClass(null, false));
        AssertEqual(UgcCategoryPrimaryPricePolicy.Emissive,
            UgcCategoryPrimaryPricePolicy.MaterialClass(true, null));
        AssertEqual(95, UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(8,
            UgcCategoryPrimaryPricePolicy.Standard));
        AssertEqual(500, UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(8,
            UgcCategoryPrimaryPricePolicy.Emissive));
        AssertTrue(UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(8,
            UgcCategoryPrimaryPricePolicy.Unknown) is null);
        return Task.CompletedTask;
    }

    static Task TestUgcVerifiedEmissiveMetadataAsync()
    {
        const string json = """
        {"data":[
            {"id":601,"itemType":"Asset","name":"Glowing But Standard","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":95,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],
             "isEmissive":false,"isAdvancedMaterials":false},
            {"id":602,"itemType":"Asset","name":"Plain Title","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":500,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],
             "isEmissive":true,"isAdvancedMaterials":true},
            {"id":603,"itemType":"Asset","name":"Neon Glow Unknown","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":500,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"]}
        ]}
        """;
        using var doc = JsonDocument.Parse(json);
        var items = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(doc.RootElement);
        AssertEqual(3, items.Count);
        AssertEqual(false, items[0].IsEmissive);
        AssertEqual(true, items[1].IsEmissive);
        AssertTrue(items[2].IsEmissive is null);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown,
            UgcCategoryPrimaryPricePolicy.MaterialClass(items[2].IsEmissive, items[2].IsPbr));
        return Task.CompletedTask;
    }

    static Task TestLimitedAnimationBundleDiscoveryParserAsync()
    {
        const string json = """
        {"data":[
            {"id":101,"itemType":"Bundle","bundleType":2,"creatorTargetId":123,
             "creatorName":"Creator","name":"Flying Pack","price":0,
             "itemRestrictions":["Collectible"],"unitsAvailableForConsumption":10,
             "collectibleItemId":"bundle-reference"},
            {"id":102,"itemType":"Bundle","bundleType":"Animations","creatorTargetId":123,
             "name":"Rolling Pack","price":300,"itemRestrictions":["Limited"]},
            {"id":103,"itemType":"Bundle","bundleType":1,"creatorTargetId":123,
             "name":"Body Bundle","price":300,"itemRestrictions":["Limited"]},
            {"id":104,"itemType":"Asset","assetType":61,"creatorTargetId":123,
             "name":"Emote Clip","price":50,"itemRestrictions":["Limited"]},
            {"id":105,"itemType":"Bundle","bundleType":2,"creatorTargetId":123,
             "name":"Not Limited","price":300,"itemRestrictions":[]},
            {"id":106,"itemType":"Bundle","bundleType":2,"creatorTargetId":123,
             "name":"Sold Out Pack","price":300,"itemRestrictions":["Limited"],
             "unitsAvailableForConsumption":0},
            {"id":107,"itemType":"Bundle","bundleType":2,"creatorTargetId":1,
             "name":"Roblox Official","price":300,"itemRestrictions":["Limited"]}
        ]}
        """;
        using var doc = JsonDocument.Parse(json);
        var found = RobloxAnimationBundleResearchParser.ParseSearch(doc.RootElement);
        AssertEqual(2, found.Count);
        AssertEqual(101L, found[0].BundleId);
        AssertTrue(found[0].CatalogQuotedPrice is null);
        AssertTrue(!found[0].PrimaryMarketVerified);
        AssertEqual("—", found[0].OriginalPriceText);
        AssertTrue(found[0].RobloxUrl.EndsWith("/bundles/101", StringComparison.Ordinal));
        AssertEqual(102L, found[1].BundleId);
        return Task.CompletedTask;
    }

    static Task TestLimitedBundleCatalogIsNotOriginalProofAsync()
    {
        const string search = """
        {"data":[{"id":201,"itemType":"Bundle","bundleType":2,"name":"Quoted",
        "creatorTargetId":123,"price":1200,"unitsAvailableForConsumption":50,
        "itemRestrictions":["Collectible"],"collectibleItemId":"reference"}]}
        """;
        const string details = """
        {"id":201,"itemType":"Bundle","bundleType":2,"name":"Quoted",
        "creatorTargetId":123,"price":1200,"unitsAvailableForConsumption":50,
        "itemRestrictions":["Collectible"],"collectibleItemId":"reference"}
        """;
        using var a = JsonDocument.Parse(search);
        using var b = JsonDocument.Parse(details);
        var initial = RobloxAnimationBundleResearchParser.ParseSearch(a.RootElement).Single();
        var hydrated = RobloxAnimationBundleResearchParser.ApplyBundleDetails(initial, b.RootElement)!;
        AssertEqual(1200, hydrated.CatalogQuotedPrice);
        AssertTrue(!hydrated.PrimaryMarketVerified);
        AssertTrue(hydrated.VerifiedOriginalPrice is null);
        AssertEqual("—", hydrated.OriginalPriceText);
        return Task.CompletedTask;
    }

    static async Task TestBundleMarketplacePrimaryVerificationAsync()
    {
        const string search = """
        {"data":[{"id":301,"itemType":"Bundle","bundleType":2,"name":"Verified Pack",
        "creatorName":"Creator","creatorTargetId":123,"price":0,
        "unitsAvailableForConsumption":12,"itemRestrictions":["Collectible"],
        "collectibleItemId":"valid-bundle"}]}
        """;
        const string details = """
        {"id":301,"itemType":"Bundle","bundleType":2,"name":"Verified Pack",
        "creatorTargetId":123,"price":300,"unitsAvailableForConsumption":12,
        "itemRestrictions":["Collectible"],"collectibleItemId":"valid-bundle"}
        """;
        const string market = """
        [{"collectibleItemId":"valid-bundle","itemTargetId":301,"price":300,
        "sales":88,"unitsAvailableForConsumption":12,"assetStock":100,
        "lowestResalePrice":700,"hasResellers":true,
        "saleLocationType":"ShopAndAllExperiences","productSaleStatus":1}]
        """;
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            var uri = request.RequestUri?.AbsolutePath ?? "";
            string result = uri.Contains("/search/items/details", StringComparison.Ordinal) ? search :
                uri.Contains("/bundles/301/details", StringComparison.Ordinal) ? details :
                uri.Contains("/marketplace-items/v1/items/details", StringComparison.Ordinal) ? market : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(result)
            });
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-bundle-research", Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(Path.Combine(root, "test.log"));
            var service = new RobloxAnimationBundleResearchService(client, logger);
            var data = await service.DiscoverAsync();
            AssertEqual(1, data.Count);
            AssertEqual(301L, data[0].BundleId);
            AssertEqual(300, data[0].VerifiedOriginalPrice);
            AssertEqual(700L, data[0].LowestResaleAsk);
            AssertTrue(data[0].PrimaryMarketVerified);
            AssertEqual(3, requests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
