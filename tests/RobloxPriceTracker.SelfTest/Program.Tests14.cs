using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcLimitedEmoteHydrationAsync()
    {
        AssertTrue(RobloxUgcCatalogDiscoveryParser.IsSupportedUgcAssetType(61));
        AssertEqual("Emote", RobloxUgcCatalogDiscoveryParser.CategoryName(61));
        AssertEqual("Shoulder", RobloxUgcCatalogDiscoveryParser.CategoryName(44));
        AssertTrue(!RobloxUgcCatalogDiscoveryParser.IsSupportedUgcAssetType(999));

        // Both a lower-price shoulder accessory and a Limited dance emote must
        // pass the same paid original-stock hydrator that formerly filtered 95 R$.
        const string json = """
        {"data":[
            {"id":601,"itemType":"Asset","name":"Dance Shuffle","creatorName":"UGC Creator",
             "creatorTargetId":234,"assetType":61,"price":50,"unitsAvailableForConsumption":15,
             "totalQuantity":100,"saleLocationType":"ShopAndAllExperiences",
             "itemRestrictions":["Collectible"]},
            {"id":602,"itemType":"Asset","name":"Glowing Shoulder Moon","creatorName":"UGC Creator",
             "creatorTargetId":235,"assetType":44,"price":45,"unitsAvailableForConsumption":10,
             "totalQuantity":55,"saleLocationType":"ShopAndAllExperiences",
             "itemRestrictions":["LimitedUnique"]},
            {"id":603,"itemType":"Asset","name":"Sold Out Dance","creatorName":"UGC Creator",
             "creatorTargetId":236,"assetType":61,"price":50,"unitsAvailableForConsumption":0,
             "totalQuantity":20,"saleLocationType":"ShopAndAllExperiences",
             "itemRestrictions":["Collectible"]},
            {"id":604,"itemType":"Asset","name":"Experience Only Emote","creatorName":"UGC Creator",
             "creatorTargetId":236,"assetType":61,"price":50,"unitsAvailableForConsumption":15,
             "totalQuantity":30,"saleLocationType":"ExperienceByPlaceId",
             "itemRestrictions":["Collectible"]},
            {"id":605,"itemType":"Asset","name":"Non Limited Emote","creatorName":"UGC Creator",
             "creatorTargetId":236,"assetType":61,"price":50,"unitsAvailableForConsumption":15,
             "totalQuantity":30,"saleLocationType":"ShopAndAllExperiences",
             "itemRestrictions":[]}
        ]}
        """;
        using var document = JsonDocument.Parse(json);
        var hydrated = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(document.RootElement);
        AssertEqual(2, hydrated.Count);
        AssertEqual(61, hydrated[0].AssetType);
        AssertEqual(50, hydrated[0].Price);
        AssertEqual(44, hydrated[1].AssetType);
        AssertEqual(45, hydrated[1].Price);
        return Task.CompletedTask;
    }

    static Task TestUgcObservedOriginalCategoryLowsAsync()
    {
        var offers = new[]
        {
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(1, 8, 95, true, 10),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(2, 8, 130, true, 10),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(3, 44, 50, true, 10),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(4, 44, 80, true, 2),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(5, 61, 55, true, 10),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(6, 61, 70, true, 8),
        };
        var mins = UgcCategoryPrimaryPricePolicy.FindLowestObservedOriginalPrices(offers);
        AssertEqual(3, mins.Count);
        AssertEqual(95, mins[8]);
        AssertEqual(50, mins[44]);
        AssertEqual(55, mins[61]);
        AssertTrue(UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[0], mins));
        AssertTrue(!UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[1], mins));
        AssertTrue(UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[2], mins));
        AssertTrue(UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[4], mins));
        return Task.CompletedTask;
    }

    static Task TestUgcObservedPrimaryRejectsResaleAndUnverifiedAsync()
    {
        var offers = new[]
        {
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(1, 44, 35, false, 5),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(2, 44, 20, true, 0),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(3, 44, 50, true, 5),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(4, 44, 70, true, 5),
            new UgcCategoryPrimaryPricePolicy.OriginalOffer(5, 8, 0, true, 5),
        };
        var mins = UgcCategoryPrimaryPricePolicy.FindLowestObservedOriginalPrices(offers);
        AssertEqual(1, mins.Count);
        AssertEqual(50, mins[44]);
        AssertTrue(!UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[0], mins));
        AssertTrue(!UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[1], mins));
        AssertTrue(UgcCategoryPrimaryPricePolicy.IsAtObservedLow(offers[2], mins));
        AssertTrue(!mins.ContainsKey(8));
        return Task.CompletedTask;
    }

    static Task TestUgcAppearanceHintsAsync()
    {
        AssertTrue(UgcCategoryPrimaryPricePolicy.HasEmissiveNameHint("Glowing Shoulder Moon"));
        AssertTrue(UgcCategoryPrimaryPricePolicy.HasEmissiveNameHint("Emissive Neon Hat"));
        AssertTrue(!UgcCategoryPrimaryPricePolicy.HasEmissiveNameHint("Illuminati Helm"));
        AssertTrue(UgcCategoryPrimaryPricePolicy.HasDanceNameHint("Dance Shuffle"));
        AssertTrue(!UgcCategoryPrimaryPricePolicy.HasDanceNameHint("Happy Gesture"));
        AssertTrue(!UgcCategoryPrimaryPricePolicy.HasDanceNameHint("Grand Chancellor"));
        return Task.CompletedTask;
    }
}
