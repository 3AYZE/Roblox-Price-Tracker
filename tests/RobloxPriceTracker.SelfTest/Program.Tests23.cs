using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestResaleVolumeWindowsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var data = new RobloxResaleMarketData(1, true, "test", null, null, [],
            [new(now.AddHours(-3), 0), new(now.AddDays(-6), 10),
             new(now.AddDays(-25), 3), new(now.AddDays(-90), 100),
             new(now.AddDays(1), 800)], null, null);
        AssertTrue(data.HasSalesCoverage(now));
        AssertEqual(0d, data.SalesWithinDays(25d / 24d, now));
        AssertEqual(10d, data.SalesWithinDays(7.25d, now));
        AssertEqual(13d, data.SalesWithinDays(30.25d, now));
        AssertTrue(!data.HasSalesCoverage(now.AddDays(4)));
        AssertEqual(0d, data.SalesWithinDays(30.25d, now.AddDays(4)));

        using var malformed = JsonDocument.Parse("""
            {"volumeDataPoints":[
                {"date":"2026-01-01T00:00:00Z","value":-4},
                {"date":"bad-date","value":1},
                {"date":"2026-01-01T00:00:00Z","value":"oops"}]}
            """);
        var parsed = RobloxResaleDataParser.Parse(1, malformed.RootElement, "test");
        AssertEqual(0, parsed.VolumeDataPoints.Count);
        return Task.CompletedTask;
    }

    static async Task TestResaleRateLimitBackoffAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
            return Task.FromResult(response);
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-resale-429", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxResaleDataService(client, new AppLogger(Path.Combine(root, "test.log")));
            var first = await service.GetAsync(998);
            AssertTrue(!first.IsAvailable && first.RateLimited);
            AssertTrue(first.RetryAfterUtc > DateTimeOffset.UtcNow.AddMinutes(10));
            var second = await service.GetAsync(998);
            AssertTrue(second.RateLimited);
            var anotherAsset = await service.GetAsync(999);
            var modernAsset = await service.GetModernAsync(1000, "another-collectible");
            AssertTrue(anotherAsset.RateLimited && modernAsset.RateLimited);
            AssertEqual(1, requests);

            // A multi-item refresh may have up to three requests in flight when
            // the first 429 arrives; the remaining assets must honor that backoff.
            var batchRequests = 0;
            using var batchClient = new HttpClient(new DelegateHandler((_, _) =>
            {
                Interlocked.Increment(ref batchRequests);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
                return Task.FromResult(response);
            }));
            var batchService = new RobloxResaleDataService(batchClient,
                new AppLogger(Path.Combine(root, "batch.log")));
            var batch = await batchService.GetManyAsync(Enumerable.Range(5000, 150).Select(x => (long)x));
            AssertEqual(150, batch.Count);
            AssertTrue(batch.Values.All(x => x.RateLimited && !x.IsAvailable));
            AssertTrue(batchRequests is >= 1 and <= 3);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static async Task TestModernResaleRateLimitFallbackAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests++;
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/resale-data", StringComparison.Ordinal) &&
                request.RequestUri?.Host == "economy.roblox.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"recentAveragePrice\":null,\"volumeDataPoints\":[],\"priceDataPoints\":[]}")
                });
            if (path.EndsWith("/catalog/items/details", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":777,\"collectibleItemId\":\"collectible-777\"}]}")
                });
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(limited);
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-modern-429", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxResaleDataService(client, new AppLogger(Path.Combine(root, "test.log")));
            var first = await service.GetAsync(777);
            AssertTrue(first.RateLimited && !first.IsAvailable);
            AssertEqual("Roblox Marketplace Sales", first.Source);
            var next = await service.GetAsync(778);
            AssertTrue(next.RateLimited && !next.IsAvailable);
            AssertEqual(3, requests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static async Task TestResaleFailureModesAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            if (requests == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{invalid-json")
                });
            throw new TaskCanceledException("Simulated request timeout");
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-resale-failure", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxResaleDataService(client, new AppLogger(Path.Combine(root, "test.log")));
            var malformed = await service.GetAsync(997);
            var timeout = await service.GetAsync(996);
            AssertTrue(!malformed.IsAvailable && !timeout.IsAvailable);
            AssertTrue(malformed.Error is not null && timeout.Error is not null);
            AssertEqual(2, requests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
