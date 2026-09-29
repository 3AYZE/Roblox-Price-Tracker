using System.Net;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static async Task TestAdaptiveScannerBoundsConcurrencyAsync()
    {
        var active = 0;
        var maximum = 0;
        var requests = 0;
        using var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref requests);
            var previously = Volatile.Read(ref maximum);
            while (previously < current &&
                Interlocked.CompareExchange(ref maximum, current, previously) != previously)
                previously = Volatile.Read(ref maximum);
            try
            {
                await Task.Delay(75, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var scheduler = new AdaptiveRobloxHttpHandler(handler);
        using var client = new HttpClient(scheduler);
        var work = Enumerable.Range(0, 12).Select(i =>
            client.GetAsync($"https://catalog.roblox.com/test?id={i}")).ToArray();
        using var replies = new ResponseGroup(await Task.WhenAll(work));
        AssertEqual(12, requests);
        AssertTrue(maximum <= 2);
        AssertTrue(scheduler.ReadMetrics().Single().Requests == 12);
    }

    static async Task TestAdaptiveScannerBackoffAsync()
    {
        using var handler = new DelegateHandler((request, _) =>
        {
            var response = new HttpResponseMessage(
                request.RequestUri!.Host.Contains("catalog") ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
            return Task.FromResult(response);
        });
        using var scheduler = new AdaptiveRobloxHttpHandler(handler);
        using var client = new HttpClient(scheduler);
        using var fail = await client.GetAsync("https://catalog.roblox.com/test");
        using var pass = await client.GetAsync("https://economy.roblox.com/test");
        using var foreign = await client.GetAsync("https://example.com/test");

        AssertEqual(HttpStatusCode.TooManyRequests, fail.StatusCode);
        AssertEqual(HttpStatusCode.OK, pass.StatusCode);
        AssertEqual(HttpStatusCode.OK, foreign.StatusCode);
        var lanes = scheduler.ReadMetrics();
        AssertEqual(2, lanes.Count);
        var catalog = lanes.Single(lane => lane.Host == "catalog.roblox.com");
        AssertEqual(1L, catalog.RateLimits);
        AssertEqual(1, catalog.Parallelism);
        AssertTrue(catalog.SpacingMs >= 300);
        AssertEqual(0L, lanes.Single(lane => lane.Host == "economy.roblox.com").RateLimits);
        AssertTrue(!AdaptiveRobloxHttpHandler.IsRobloxHost("roblox.com.evil.example"));
    }

    static async Task TestParallelDiscoveryKeepsDeduplicationAsync()
    {
        var inFlight = 0;
        var maximum = 0;
        using var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                int current = Interlocked.Increment(ref inFlight);
                int seen;
                do
                {
                    seen = Volatile.Read(ref maximum);
                    if (seen >= current) break;
                } while (Interlocked.CompareExchange(ref maximum, current, seen) != seen);
                try
                {
                    await Task.Delay(100, cancellationToken);
                    const string ids = """{"data":[{"id":99001,"itemType":"Asset","assetType":8,"creatorTargetId":987,"itemRestrictions":["Collectible"]}],"nextPageCursor":null}""";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ids)
                    };
                }
                finally { Interlocked.Decrement(ref inFlight); }
            }

            // A single ID returned by all independent feeds should only be hydrated once.
            const string detail = """
            {"data":[{"id":99001,"itemType":"Asset","name":"Limited",
            "creatorName":"Test","creatorTargetId":987,"assetType":8,
            "price":95,"unitsAvailableForConsumption":100,"totalQuantity":1000,
            "saleLocationType":"ShopAndAllExperiences","itemRestrictions":["Collectible"]}]}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detail) };
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "rpt-scan-perf", Guid.NewGuid().ToString("N"), "app.log"));
        var scanner = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: true);
        var result = await scanner.DiscoverAsync();
        AssertTrue(maximum >= 2);
        AssertEqual(1, result.DiscoveredCount);
        AssertEqual(1, result.HydratedCount);
        AssertEqual(1, result.Items.Count);
        AssertEqual(99001L, result.Items[0].AssetId);
    }

    static async Task TestResaleLookupSingleFlightAsync()
    {
        int calls = 0;
        const string json = """
        {"sales":300,"recentAveragePrice":950,
         "priceDataPoints":[{"value":950,"date":"2026-09-28T00:00:00Z"}],
         "volumeDataPoints":[{"value":12,"date":"2026-09-28T00:00:00Z"}]}
        """;
        using var handler = new DelegateHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "rpt-resale-flight", Guid.NewGuid().ToString("N"), "app.log"));
        var service = new RobloxResaleDataService(http, logger);

        var legacy = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => service.GetAsync(8123456L)));
        AssertEqual(1, calls);
        AssertTrue(legacy.All(row => row.IsAvailable && row.AssetId == 8123456L));
        await service.GetAsync(8123456L);
        AssertEqual(1, calls);

        var modern = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => service.GetModernAsync(8123457L, "test-collectible-id")));
        AssertEqual(2, calls);
        AssertTrue(modern.All(row => row.IsAvailable && row.AssetId == 8123457L));
        await service.GetModernAsync(8123457L, "test-collectible-id");
        AssertEqual(2, calls);
    }

    static async Task TestResaleLookupIsolatedCancellationAsync()
    {
        int calls = 0;
        const string json = """
        {"sales":300,"recentAveragePrice":750,
         "priceDataPoints":[{"value":750,"date":"2026-09-28T00:00:00Z"}],
         "volumeDataPoints":[{"value":5,"date":"2026-09-28T00:00:00Z"}]}
        """;
        using var handler = new DelegateHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(120, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "rpt-cancellation", Guid.NewGuid().ToString("N"), "app.log"));
        var service = new RobloxResaleDataService(http, logger);

        using var cancel = new CancellationTokenSource();
        var cancelled = service.GetAsync(987654L, cancel.Token);
        var other = service.GetAsync(987654L);
        cancel.Cancel();
        bool observedCancellation = false;
        try { await cancelled; }
        catch (OperationCanceledException) { observedCancellation = true; }
        AssertTrue(observedCancellation);
        var completed = await other;
        AssertTrue(completed.IsAvailable);
        AssertEqual(1, calls);
    }

    private sealed class ResponseGroup : IDisposable
    {
        private readonly HttpResponseMessage[] _responses;
        public ResponseGroup(HttpResponseMessage[] responses) => _responses = responses;
        public void Dispose() { foreach (var response in _responses) response.Dispose(); }
    }
}
