using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestResaleDataParserAsync()
    {
        const string json = """
        {
          "sales": 12345,
          "recentAveragePrice": 5000,
          "priceDataPoints": [
            { "value": 4900, "date": "2026-08-22T00:00:00Z" },
            { "value": 5000, "date": "2026-08-23T00:00:00Z" }
          ],
          "volumeDataPoints": [
            { "value": 10, "date": "2026-08-22T00:00:00Z" },
            { "value": 20, "date": "2026-08-23T00:00:00Z" }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var data = RobloxResaleDataParser.Parse(123, doc.RootElement, "test");
        AssertTrue(data.IsAvailable);
        AssertEqual(12345L, data.TotalSales!.Value);
        AssertTrue(Math.Abs(data.RecentAveragePrice!.Value - 5000d) < 0.001);
        AssertEqual(2, data.PriceDataPoints.Count);
        AssertEqual(2, data.VolumeDataPoints.Count);
        AssertTrue(data.SalesPerDay7d > 4d);
        return Task.CompletedTask;
    }

    static Task TestForecastRequiresMinimumHistoryAsync()
    {
        var key = new ItemKey(CatalogItemType.Asset, 901);
        var start = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 7)
            .Select(i => new JsonFileRepository.PriceHistoryEntry(key, start.AddHours(i), 10_000 - i * 50, MarketStatus.Available, i + 1))
            .ToArray();
        var result = new PriceForecastEngine().Calculate(history, null, 8_000, start.AddHours(7));
        AssertTrue(!result.IsAvailable);
        AssertEqual(7, result.ObservationCount);
        return Task.CompletedTask;
    }

    static Task TestForecastFollowsCleanDowntrendAsync()
    {
        var key = new ItemKey(CatalogItemType.Asset, 902);
        var start = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 16)
            .Select(i => new JsonFileRepository.PriceHistoryEntry(key, start.AddHours(i), 10_000 - i * 100, MarketStatus.Available, i + 1))
            .ToArray();
        var current = history[^1].Price!.Value;
        var result = new PriceForecastEngine().Calculate(history, null, 8_000, start.AddHours(15));
        AssertTrue(result.IsAvailable);
        AssertTrue(result.NextPrice is > 0 && result.NextPrice.Value < current);
        AssertTrue(result.Direction is ForecastDirection.Bearish or ForecastDirection.StrongBearish);
        AssertTrue(result.RangeLow < result.NextPrice && result.RangeHigh > result.NextPrice);
        return Task.CompletedTask;
    }

    static Task TestLiquidityRaisesForecastConfidenceAsync()
    {
        var key = new ItemKey(CatalogItemType.Asset, 903);
        var start = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 24)
            .Select(i => new JsonFileRepository.PriceHistoryEntry(key, start.AddHours(i), 5_000 + (i % 3 - 1) * 10, MarketStatus.Available, i + 1))
            .ToArray();

        var lowVolume = new RobloxResaleMarketData(
            key.Id, true, "test", 100, 5_000,
            Array.Empty<ResaleDataPoint>(),
            Enumerable.Range(0, 7).Select(i => new ResaleDataPoint(start.AddDays(-i), 1)).ToArray(),
            null, null);
        var highVolume = lowVolume with
        {
            TotalSales = 100_000,
            VolumeDataPoints = Enumerable.Range(0, 7).Select(i => new ResaleDataPoint(start.AddDays(-i), 100)).ToArray()
        };

        var engine = new PriceForecastEngine();
        var low = engine.Calculate(history, lowVolume, 4_500, start.AddHours(23));
        var high = engine.Calculate(history, highVolume, 4_500, start.AddHours(23));
        AssertTrue(low.IsAvailable && high.IsAvailable);
        AssertTrue(high.LiquidityScore > low.LiquidityScore);
        AssertTrue(high.ConfidencePercent > low.ConfidencePercent);
        return Task.CompletedTask;
    }

    static async Task TestForecastBacktestStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rpt-forecast-{Guid.NewGuid():N}.json");
        try
        {
            var key = new ItemKey(CatalogItemType.Asset, 904);
            var store = new ForecastHistoryStore(path);
            await store.InitializeAsync();
            var forecast = new PriceForecastResult(
                true, "test", 950, 940, 900, 1_000, 70, ForecastDirection.Bearish,
                2, 50, 10, 10, 25, 50, 5, 20, 1, -50, true, PriceForecastEngine.ModelVersion);

            var now = DateTimeOffset.UtcNow;
            await store.RecordAsync(key, 1, forecast, 1_000, now);
            await store.RecordAsync(key, 2, forecast with { NextPrice = 900 }, 900, now.AddMinutes(5));
            var stats = await store.GetStatsAsync(key);
            AssertEqual(1, stats.EvaluatedForecasts);
            AssertTrue(stats.MeanAbsolutePercentError > 5d && stats.MeanAbsolutePercentError < 6d);
            AssertEqual(100d, stats.RangeCoveragePercent);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, ".bak", ".tmp" })
            {
                try { File.Delete(path + suffix); } catch { }
            }
        }
    }
}
