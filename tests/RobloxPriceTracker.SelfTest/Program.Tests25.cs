using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcCategoryEntryPricesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        UgcPriceIntegrityPolicy.PrimaryPriceObservation Offer(int type, int price) =>
            new(9001, 2002, type, price, 25, now, true, "RobloxMarketplacePrimary");
        UgcCategoryBuyingPricePolicy.Decision Check(int type, int price, string material = "STANDARD") =>
            UgcCategoryBuyingPricePolicy.Evaluate(Offer(type, price), material, []);

        AssertEqual(UgcCategoryBuyingPricePolicy.Good, Check(8, 95).Classification);
        AssertEqual(95, Check(8, 95).CeilingRobux);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(8, 225).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(8, 249).Classification);
        AssertEqual(95, Check(8, 249).CeilingRobux);
        AssertEqual(UgcCategoryBuyingPricePolicy.Good, Check(41, 65).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(41, 66).Classification);
        foreach (var type in new[] { 42, 43, 44 })
        {
            AssertEqual(55, Check(type, 55).CeilingRobux);
            AssertEqual(UgcCategoryBuyingPricePolicy.Good, Check(type, 55).Classification);
            AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(type, 56).Classification);
        }
        AssertEqual(140, Check(46, 140).CeilingRobux);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(46, 141).Classification);
        AssertEqual(50, Check(61, 50).CeilingRobux);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, Check(61, 51).Classification);
        foreach (var type in new[] { 45, 47, 64, 65, 66, 67, 68, 69, 70, 71, 72, 76, 77, 79, 88, 89, 90 })
            AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient, Check(type, 1).Classification);

        AssertEqual(55, UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(
            42, UgcCategoryPrimaryPricePolicy.Standard));
        AssertTrue(UgcCategoryPrimaryPricePolicy.October2026TypicalOriginalReference(
            47, UgcCategoryPrimaryPricePolicy.Standard) is null);
        return Task.CompletedTask;
    }

    static Task TestUgcRepricingBlocksBuyIndependentlyAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new UgcPriceIntegrityPolicy.PrimaryPriceObservation(
            126317830302283, 900, 8, 249, 491, now, true, "RobloxMarketplacePrimary");
        var original = current with { PrimaryPrice = 95, ObservedAtUtc = now.AddHours(-1) };

        var integrity = UgcPriceIntegrityPolicy.Evaluate(current, [original],
            new UgcPriceIntegrityPolicy.ResaleEvidence(null, null, null, null, 0));
        AssertEqual(UgcPriceIntegrityPolicy.Passed, integrity.Classification);
        var entry = UgcCategoryBuyingPricePolicy.Evaluate(current, "STANDARD", [original]);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, entry.Classification);
        AssertEqual("ABOVE ENTRY PRICE — CREATOR REPRICED", entry.StatusText);
        AssertEqual(95, entry.EarlierVerifiedPrice);
        AssertTrue(entry.Explanation.Contains("249 R$ vs 95 R$", StringComparison.Ordinal));

        // A clean integrity result cannot bypass the independent category
        // entry ceiling when every other opportunity condition is satisfied.
        var opportunity = UgcQualifiedOpportunityPolicy.Evaluate(
            new UgcQualifiedOpportunityPolicy.Evidence(
                true, now, 249, 10, 100, 2, 1d, true, 5d, 7,
                1000d, 7, 1000, 10d, false, entry.IsGoodBuyingPrice),
            now);
        AssertTrue(!opportunity.Qualified);
        AssertEqual("ENTRY_PRICE_NOT_APPROVED", opportunity.Reason);

        // A regional quote, a resale ask, and a different creator's history
        // must never be mistaken for the earlier global creator price.
        var otherEvidence = new[]
        {
            original with { Source = "RobloxRegionalDisplay" },
            original with { Source = "RobloxResaleAsk" },
            original with { CreatorId = 901 },
            original with { AssetType = 41 },
            original with { ObservedAtUtc = now }
        };
        var unproven = UgcCategoryBuyingPricePolicy.Evaluate(current, "STANDARD", otherEvidence);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above, unproven.Classification);
        AssertTrue(unproven.EarlierVerifiedPrice is null);
        AssertEqual("ABOVE ENTRY PRICE", unproven.StatusText);
        return Task.CompletedTask;
    }

    static Task TestUgcEntryPriceEvidenceAndMaterialsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new UgcPriceIntegrityPolicy.PrimaryPriceObservation(
            9001, 2002, 8, 95, 15, now, true, "RobloxMarketplacePrimary");
        UgcCategoryBuyingPricePolicy.Decision Check(
            UgcPriceIntegrityPolicy.PrimaryPriceObservation point, string material,
            UgcCategoryBuyingPricePolicy.Configuration? config = null) =>
            UgcCategoryBuyingPricePolicy.Evaluate(point, material, [], config);

        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient,
            Check(current with { MarketplaceVerified = false }, "STANDARD").Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient,
            Check(current with { Source = "RobloxCatalogQuote" }, "STANDARD").Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient,
            Check(current with { OriginalUnitsRemaining = 0 }, "STANDARD").Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient, Check(current, "UNKNOWN").Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient, Check(current, "PBR").Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient, Check(current, "EMISSIVE").Classification);

        var rules = new UgcCategoryBuyingPricePolicy.Configuration
        {
            SpecialMaterialBenchmarks = new()
            {
                ["8:EMISSIVE"] = new()
                {
                    CeilingRobux = 200,
                    EvidenceSource = "Independent verified category study",
                    VerifiedAtUtc = now.AddDays(-1)
                },
                ["8:PBR"] = new()
                {
                    CeilingRobux = 275,
                    EvidenceSource = "Independent verified PBR category study",
                    VerifiedAtUtc = now.AddDays(-1)
                }
            }
        };
        AssertTrue(rules.IsValid);
        AssertEqual(UgcCategoryBuyingPricePolicy.Good,
            Check(current with { PrimaryPrice = 200 }, "EMISSIVE", rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above,
            Check(current with { PrimaryPrice = 201 }, "EMISSIVE", rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Good,
            Check(current with { PrimaryPrice = 275 }, "PBR", rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Above,
            Check(current with { PrimaryPrice = 276 }, "PBR", rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient,
            Check(current with { PrimaryPrice = 249 }, "UNKNOWN", rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Good, Check(current, "STANDARD", rules).Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcMaterialMetadataRequiresBothFlagsAsync()
    {
        const string json = """
        {"data":[
            {"id":9101,"itemType":"Asset","name":"Emissive Flag Only","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":200,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":true},
            {"id":9102,"itemType":"Asset","name":"PBR Flag Only","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":200,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isAdvancedMaterials":true},
            {"id":9103,"itemType":"Asset","name":"Standard Flag Only","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":95,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":false},
            {"id":9104,"itemType":"Asset","name":"Verified Standard","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":95,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":false,"isPbr":false},
            {"id":9105,"itemType":"Asset","name":"Verified Emissive","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":200,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":true,"isPbr":false},
            {"id":9106,"itemType":"Asset","name":"Verified PBR","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":275,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":false,"isPbr":true},
            {"id":9107,"itemType":"Asset","name":"Conflicting Premium Flags","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":200,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":true,"isPbr":true},
            {"id":9108,"itemType":"Asset","name":"Conflicting PBR Aliases","creatorName":"Test",
             "creatorTargetId":123,"assetType":8,"price":200,"unitsAvailableForConsumption":10,
             "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Limited"],"isEmissive":false,"isPbr":false,"isAdvancedMaterials":true}
        ]}
        """;
        using var document = JsonDocument.Parse(json);
        var items = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(document.RootElement);
        AssertEqual(8, items.Count);

        var classes = items.Select(item => UgcCategoryPrimaryPricePolicy.MaterialClass(
            item.IsEmissive, item.IsPbr)).ToArray();
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown, classes[0]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown, classes[1]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown, classes[2]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Standard, classes[3]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Emissive, classes[4]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Pbr, classes[5]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown, classes[6]);
        AssertEqual(UgcCategoryPrimaryPricePolicy.Unknown, classes[7]);

        var now = DateTimeOffset.UtcNow;
        var rules = new UgcCategoryBuyingPricePolicy.Configuration
        {
            SpecialMaterialBenchmarks = new()
            {
                ["8:EMISSIVE"] = new()
                {
                    CeilingRobux = 200,
                    EvidenceSource = "Verified emissive category benchmark",
                    VerifiedAtUtc = now.AddMinutes(-1)
                },
                ["8:PBR"] = new()
                {
                    CeilingRobux = 275,
                    EvidenceSource = "Verified PBR category benchmark",
                    VerifiedAtUtc = now.AddMinutes(-1)
                }
            }
        };
        UgcPriceIntegrityPolicy.PrimaryPriceObservation Offer(int index) => new(
            items[index].AssetId, 1, 8, items[index].Price, 10, now, true, "RobloxMarketplacePrimary");
        for (var index = 0; index < 3; index++)
        {
            var decision = UgcCategoryBuyingPricePolicy.Evaluate(
                Offer(index), classes[index], [], rules);
            AssertEqual(UgcCategoryBuyingPricePolicy.Insufficient, decision.Classification);
            AssertTrue(decision.CeilingRobux is null);
        }
        AssertEqual(UgcCategoryBuyingPricePolicy.Good,
            UgcCategoryBuyingPricePolicy.Evaluate(Offer(4), classes[4], [], rules).Classification);
        AssertEqual(UgcCategoryBuyingPricePolicy.Good,
            UgcCategoryBuyingPricePolicy.Evaluate(Offer(5), classes[5], [], rules).Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcResaleEnrichmentSelectionPolicyAsync()
    {
        static UgcResaleEnrichmentSelectionPolicy.Candidate Candidate(
            long id, int type, bool verified, bool good, long remaining = 10, double score = 50) =>
            new(id, type, verified, good, remaining, 100, 2, 1, score, score);

        var candidates = new[]
        {
            Candidate(801, 8, true, true, score: 90),
            Candidate(802, 8, true, false, score: 100),
            Candidate(803, 8, true, true, score: 80),
            Candidate(841, 41, true, true, score: 70),
            Candidate(421, 42, true, false, score: 99),
            Candidate(431, 43, false, false, score: 98)
        };

        var selected = UgcResaleEnrichmentSelectionPolicy.Select(candidates, 6);
        AssertEqual(6, selected.Count);
        AssertEqual(801L, selected[0].AssetId);
        AssertEqual(841L, selected[1].AssetId);
        AssertEqual(803L, selected[2].AssetId);
        AssertEqual(802L, selected[3].AssetId);
        AssertEqual(421L, selected[4].AssetId);
        AssertEqual(431L, selected[5].AssetId);

        // A single category always exhausts its verified affordable queue
        // before selecting overpriced research candidates.
        var oneCategory = UgcResaleEnrichmentSelectionPolicy.Select(
            [candidates[0], candidates[1], candidates[2]], 3);
        AssertEqual(801L, oneCategory[0].AssetId);
        AssertEqual(803L, oneCategory[1].AssetId);
        AssertEqual(802L, oneCategory[2].AssetId);

        // Candidate order rotates within a category across scans so a fixed
        // top-ranked asset cannot monopolize a small enrichment budget.
        var sameCategoryBuying = new[]
        {
            Candidate(811, 8, true, true),
            Candidate(812, 8, true, true),
            Candidate(813, 8, true, true)
        };
        AssertEqual(811L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryBuying, 1, 0)[0].AssetId);
        AssertEqual(812L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryBuying, 1, 1)[0].AssetId);
        AssertEqual(813L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryBuying, 1, 2)[0].AssetId);

        var sameCategoryResearch = new[]
        {
            Candidate(821, 8, true, false),
            Candidate(822, 8, true, false),
            Candidate(823, 8, true, false)
        };
        AssertEqual(821L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryResearch, 1, 0)[0].AssetId);
        AssertEqual(822L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryResearch, 1, 1)[0].AssetId);
        AssertEqual(823L, UgcResaleEnrichmentSelectionPolicy.Select(sameCategoryResearch, 1, 2)[0].AssetId);

        // When categories exceed the request budget, the starting category
        // rotates so later categories are not permanently starved.
        var researchOnly = new[]
        {
            Candidate(901, 8, true, false),
            Candidate(941, 41, true, false),
            Candidate(942, 42, true, false),
            Candidate(943, 43, true, false)
        };
        AssertEqual(901L, UgcResaleEnrichmentSelectionPolicy.Select(researchOnly, 2, 0)[0].AssetId);
        AssertEqual(941L, UgcResaleEnrichmentSelectionPolicy.Select(researchOnly, 2, 1)[0].AssetId);
        AssertEqual(942L, UgcResaleEnrichmentSelectionPolicy.Select(researchOnly, 2, 2)[0].AssetId);
        AssertEqual(0, UgcResaleEnrichmentSelectionPolicy.Select(researchOnly, 0).Count);

        // Every request goes to affordable candidates while that pool can fill
        // the budget. Research candidates only use capacity left over.
        var mixedCategories = new[]
        {
            Candidate(1001, 8, true, true),
            Candidate(1041, 41, true, true),
            Candidate(1042, 42, true, true),
            Candidate(1043, 43, true, true),
            Candidate(1044, 44, true, true),
            Candidate(1046, 46, true, true),
            Candidate(1061, 61, true, true),
            Candidate(1045, 45, true, false),
            Candidate(1047, 47, true, false),
            Candidate(1064, 64, true, false)
        };
        var mixedFirst = UgcResaleEnrichmentSelectionPolicy.Select(mixedCategories, 8, 0);
        AssertEqual(8, mixedFirst.Count);
        AssertEqual(7, mixedFirst.Count(x => x.IsBuyingCandidate));
        AssertEqual(1, mixedFirst.Count(x => !x.IsBuyingCandidate));
        AssertTrue(mixedFirst.Any(x => x.AssetId == 1045));
        AssertTrue(!mixedFirst.Any(x => x.AssetId == 1047));
        var mixedNext = UgcResaleEnrichmentSelectionPolicy.Select(mixedCategories, 8, 1);
        AssertTrue(mixedNext.Any(x => x.AssetId == 1047));

        // A one-request budget always goes to the affordable pool when one
        // exists, regardless of the scan rotation.
        var oneRequestMixed = new[]
        {
            Candidate(1081, 8, true, true),
            Candidate(1082, 41, true, false)
        };
        AssertTrue(UgcResaleEnrichmentSelectionPolicy.Select(oneRequestMixed, 1, 0)[0].IsBuyingCandidate);
        AssertTrue(UgcResaleEnrichmentSelectionPolicy.Select(oneRequestMixed, 1, 1)[0].IsBuyingCandidate);
        AssertTrue(UgcResaleEnrichmentSelectionPolicy.Select(oneRequestMixed, 1, 2)[0].IsBuyingCandidate);

        var buyingOnlyBudget = UgcResaleEnrichmentSelectionPolicy.Select(mixedCategories, 4, 3);
        AssertEqual(4, buyingOnlyBudget.Count);
        AssertTrue(buyingOnlyBudget.All(x => x.IsBuyingCandidate));
        AssertEqual(4, buyingOnlyBudget.Select(x => x.AssetType).Distinct().Count());

        // Overpriced research candidates in an otherwise buying-ready category
        // also receive a research turn after the affordable pool is ranked.
        var sameCategory = new[]
        {
            Candidate(1101, 8, true, true, score: 70),
            Candidate(1102, 8, true, false, score: 100),
            Candidate(1141, 41, true, true, score: 60)
        };
        var sameCategorySelected = UgcResaleEnrichmentSelectionPolicy.Select(sameCategory, 3);
        AssertEqual(1101L, sameCategorySelected[0].AssetId);
        AssertEqual(1141L, sameCategorySelected[1].AssetId);
        AssertEqual(1102L, sameCategorySelected[2].AssetId);
        return Task.CompletedTask;
    }
}
