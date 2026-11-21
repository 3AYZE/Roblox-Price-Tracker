using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestOfficialResaleCoverageRotatesAsync()
    {
        var ids = Enumerable.Range(1, 180).Select(x => (long)x).ToArray();
        var attempted = new Dictionary<long, DateTimeOffset>();
        var now = new DateTimeOffset(2026, 11, 21, 16, 0, 0, TimeSpan.Zero);
        for (var scan = 0; scan < 5; scan++)
        {
            var selected = OfficialResaleCoveragePolicy.SelectNext(ids, attempted, 36);
            AssertEqual(36, selected.Count);
            AssertEqual(36, selected.Distinct().Count());
            AssertEqual((long)(scan * 36 + 1), selected.First());
            foreach (var id in selected) attempted.Add(id, now.AddMinutes(scan));
        }
        AssertEqual(180, attempted.Count);
        AssertEqual(1L, OfficialResaleCoveragePolicy.SelectNext(ids, attempted, 36).First());
        AssertEqual(0, OfficialResaleCoveragePolicy.SelectNext(ids, attempted, 0).Count);
        AssertEqual(1, OfficialResaleCoveragePolicy.SelectNext([1, 1, 0], attempted, 36).Count);
        return Task.CompletedTask;
    }

    static Task TestOfficialResaleRejectsStaleWeeksAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-90);
        var olderMarket = new RobloxResaleMarketData(
            555, true, "Roblox Economy", 200, 1000,
            [new ResaleDataPoint(old, 900)],
            [new ResaleDataPoint(old, 12)], null, null);
        var item = new RobloxOfficialLimitedCatalogItem(555, "Stale analytics", 600, null, 20, 10, 8, 50);
        AssertTrue(OfficialResaleCoveragePolicy.IsDemonstrablyStaleLegacy(olderMarket, now));
        AssertTrue(!OfficialResaleCoveragePolicy.HasRecentVolume(olderMarket.VolumeDataPoints, now));
        var scored = OfficialLimitedHuntScoring.Evaluate(item, olderMarket);
        AssertTrue(!scored.IsEnriched);
        AssertTrue(scored.Rap is null);
        AssertTrue(!scored.HasRecentSalesCoverage);
        AssertTrue(!scored.IsHuntCandidate);
        AssertTrue(scored.EvidenceText.Contains("unavailable", StringComparison.Ordinal));

        var freshZero = olderMarket with
        {
            Source = "Roblox Marketplace Sales",
            RecentAveragePrice = 800,
            PriceDataPoints = [new ResaleDataPoint(now.AddHours(-2), 800)],
            VolumeDataPoints = [new ResaleDataPoint(now.AddHours(-2), 0)]
        };
        AssertTrue(OfficialResaleCoveragePolicy.HasRecentVolume(freshZero.VolumeDataPoints, now));
        var fresh = OfficialLimitedHuntScoring.Evaluate(item, freshZero);
        AssertTrue(fresh.IsEnriched);
        AssertTrue(fresh.HasRecentSalesCoverage);
        AssertEqual(0d, fresh.Sales7d);
        AssertTrue(!fresh.IsHuntCandidate);
        return Task.CompletedTask;
    }

    static async Task TestOfficialResaleFallsBackForMigratedAssetAsync()
    {
        var oldDate = DateTimeOffset.UtcNow.AddDays(-90);
        var recent = DateTimeOffset.UtcNow.AddHours(-4);
        var legacyJson = JsonSerializer.Serialize(new
        {
            recentAveragePrice = 200,
            priceDataPoints = new[] { new { date = oldDate, value = 200 } },
            volumeDataPoints = new[] { new { date = oldDate, value = 17 } }
        });
        var modernJson = JsonSerializer.Serialize(new
        {
            recentAveragePrice = 900,
            priceDataPoints = new[] { new { date = recent, value = 900 } },
            volumeDataPoints = new[] { new { date = recent, value = 4 } }
        });
        var visited = new List<string>();
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            visited.Add(path);
            var result = path switch
            {
                "/v1/assets/777/resale-data" => legacyJson,
                "/v1/catalog/items/details" => "{\"data\":[{\"id\":777}]}",
                "/v2/assets/777/details" => "{\"collectibleItemId\":\"test-collectible-777\"}",
                "/marketplace-sales/v1/item/test-collectible-777/resale-data" => modernJson,
                _ => throw new InvalidOperationException("Unexpected endpoint: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(result)
            });
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-official-resale", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxResaleDataService(client,
                new AppLogger(Path.Combine(root, "test.log")));
            var result = await service.GetAsync(777);
            AssertEqual("Roblox Marketplace Sales", result.Source);
            AssertEqual(900d, result.RecentAveragePrice!.Value);
            AssertTrue(result.VolumeDataPoints.Count > 0);
            AssertEqual(4, visited.Count);
            AssertTrue(visited.Contains("/v2/assets/777/details"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
