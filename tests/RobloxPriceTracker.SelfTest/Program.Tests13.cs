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

    private sealed class ResponseGroup : IDisposable
    {
        private readonly HttpResponseMessage[] _responses;
        public ResponseGroup(HttpResponseMessage[] responses) => _responses = responses;
        public void Dispose() { foreach (var response in _responses) response.Dispose(); }
    }
}
