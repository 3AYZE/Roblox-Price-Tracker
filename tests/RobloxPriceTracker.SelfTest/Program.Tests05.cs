using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcResaleBreakEvenAsync()
    {
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            OriginalPrice: 100,
            TotalSupply: 500,
            PurchaseCount: 250,
            UnitsAvailable: 250,
            VelocityPerMinute: 1,
            Acceleration: 0,
            FavoriteCount: 100,
            ObservationCount: 5));
        AssertEqual(200L, score.BreakEvenResale);
        return Task.CompletedTask;
    }

    static Task TestUgcFastExpensiveSelloutIsNotAutomaticBuyAsync()
    {
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            OriginalPrice: 2000,
            TotalSupply: 500,
            PurchaseCount: 492,
            UnitsAvailable: 8,
            VelocityPerMinute: 15,
            Acceleration: 0.45,
            FavoriteCount: 5000,
            ObservationCount: 10));

        AssertTrue(score.ResalePotential <= 50d);
        AssertTrue(score.Recommendation != "HIGH RESALE");
        return Task.CompletedTask;
    }

    static Task TestUgcProfitableLiquidMarketRanksHighAsync()
    {
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            OriginalPrice: 100,
            TotalSupply: 500,
            PurchaseCount: 480,
            UnitsAvailable: 20,
            VelocityPerMinute: 6,
            Acceleration: 0.20,
            FavoriteCount: 2200,
            ObservationCount: 12,
            RecentAveragePrice: 350,
            LowestResalePrice: 360,
            ObservedResellers: 15,
            ListingsWithin10Pct: 8,
            ListingsWithin20Pct: 12,
            SalesLast7d: 30,
            SalesLast30d: 90,
            MedianTop10: 370,
            HasLiveResaleMarket: true));

        AssertTrue(score.BaseNetRoi > 0.50d);
        AssertTrue(score.ResalePotential >= 75d);
        AssertTrue(score.Recommendation is "HIGH RESALE" or "STRONG");
        return Task.CompletedTask;
    }

    static Task TestUgcUnprofitableLiveResaleHardGateAsync()
    {
        var score = UgcResaleScoring.Evaluate(new UgcResaleScoringInput(
            OriginalPrice: 500,
            TotalSupply: 1000,
            PurchaseCount: 980,
            UnitsAvailable: 20,
            VelocityPerMinute: 8,
            Acceleration: 0.25,
            FavoriteCount: 5000,
            ObservationCount: 12,
            RecentAveragePrice: 680,
            LowestResalePrice: 700,
            ObservedResellers: 10,
            ListingsWithin10Pct: 6,
            ListingsWithin20Pct: 9,
            SalesLast7d: 12,
            SalesLast30d: 35,
            MedianTop10: 720,
            HasLiveResaleMarket: true));

        AssertEqual(1000L, score.BreakEvenResale);
        AssertTrue(score.ResalePotential <= 35d);
        AssertEqual("AVOID", score.Recommendation);
        return Task.CompletedTask;
    }

    static Task TestUgcResellerBookParserAsync()
    {
        const string json = """
        {
          "data": [
            { "price": 100 },
            { "price": 102 },
            { "price": 105 },
            { "price": 110 },
            { "price": 200 }
          ],
          "nextPageCursor": "more"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var parsed = RobloxResellerDataParser.Parse(123, "collectible", doc.RootElement);
        AssertTrue(parsed.IsAvailable);
        AssertEqual(5, parsed.ObservedListings);
        AssertEqual(100L, parsed.LowestPrice!.Value);
        AssertEqual(4, parsed.ListingsWithin10Pct);
        AssertTrue(parsed.HasMoreListings);
        AssertTrue(Math.Abs(parsed.MedianTop10!.Value - 105d) < 0.001d);
        return Task.CompletedTask;
    }
}
