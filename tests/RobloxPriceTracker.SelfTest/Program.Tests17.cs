using System.Net;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestAnimationBundleSparseSearchNeedsDetailVerificationAsync()
    {
        const string search = """
        {"data":[
          {"id":401,"itemType":"Bundle","bundleType":2,"name":"Sparse Pack",
           "creatorTargetId":222,"price":300},
          {"id":402,"itemType":"Bundle","bundleType":2,"name":"Not Limited",
           "creatorTargetId":222,"price":300,"itemRestrictions":[]}
        ]}
        """;
        const string details = """
        {"id":401,"itemType":"Bundle","bundleType":2,"name":"Sparse Pack",
         "creatorTargetId":222,"itemRestrictions":["Collectible"],"price":300,
         "unitsAvailableForConsumption":8}
        """;
        using var searchDocument = JsonDocument.Parse(search);
        using var detailDocument = JsonDocument.Parse(details);
        var found = RobloxAnimationBundleResearchParser.ParseSearch(searchDocument.RootElement);
        AssertEqual(1, found.Count);
        AssertTrue(!found[0].LimitedVerified);
        var hydrated = RobloxAnimationBundleResearchParser.ApplyBundleDetails(found[0], detailDocument.RootElement);
        AssertTrue(hydrated is not null && hydrated.LimitedVerified);
        AssertEqual(401L, hydrated!.BundleId);
        AssertTrue(!hydrated.PrimaryMarketVerified);
        AssertEqual("—", hydrated.OriginalPriceText);
        return Task.CompletedTask;
    }

    static Task TestAnimationBundleDetailRejectionDoesNotFallbackAsync()
    {
        const string search = """
        {"data":[{"id":501,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Rejected Pack","itemRestrictions":["Limited"]}]}
        """;
        const string details = """
        {"id":501,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Rejected Pack","itemRestrictions":[]}
        """;
        using var a = JsonDocument.Parse(search);
        using var b = JsonDocument.Parse(details);
        var candidate = RobloxAnimationBundleResearchParser.ParseSearch(a.RootElement).Single();
        AssertTrue(candidate.LimitedVerified);
        AssertTrue(RobloxAnimationBundleResearchParser.ApplyBundleDetails(candidate, b.RootElement) is null);
        return Task.CompletedTask;
    }

    static async Task TestAnimationBundlePagedDiscoveryDeduplicatesAsync()
    {
        const string pageOne = """
        {"data":[{"id":601,"itemType":"Bundle","bundleType":2,"name":"One",
          "creatorTargetId":123,"itemRestrictions":["Collectible"]}],
          "nextPageCursor":"cursor-a"}
        """;
        const string pageTwo = """
        {"data":[
          {"id":601,"itemType":"Bundle","bundleType":2,"name":"One",
           "creatorTargetId":123,"itemRestrictions":["Collectible"]},
          {"id":602,"itemType":"Bundle","bundleType":2,"name":"Two",
           "creatorTargetId":234,"itemRestrictions":["Limited"]}
        ],"nextPageCursor":"cursor-b"}
        """;
        const string detailOne = """
        {"id":601,"itemType":"Bundle","bundleType":2,"name":"One",
         "creatorTargetId":123,"itemRestrictions":["Collectible"],"price":300,
         "unitsAvailableForConsumption":5}
        """;
        const string detailTwo = """
        {"id":602,"itemType":"Bundle","bundleType":2,"name":"Two",
         "creatorTargetId":234,"itemRestrictions":["Limited"],"price":350,
         "unitsAvailableForConsumption":9}
        """;
        int pages = 0, details = 0;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            string payload;
            if (path.Contains("/search/items/details", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref pages);
                payload = request.RequestUri?.Query.Contains("Cursor=", StringComparison.Ordinal) == true
                    ? pageTwo : pageOne;
            }
            else if (path.Contains("/bundles/601/details", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref details);
                payload = detailOne;
            }
            else if (path.Contains("/bundles/602/details", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref details);
                payload = detailTwo;
            }
            else throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-live-drops", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxAnimationBundleResearchService(http,
                new AppLogger(Path.Combine(root, "test.log")));
            var result = await service.DiscoverAsync();
            AssertEqual(2, result.Count);
            AssertEqual(2, pages); // Bounded pagination, even with another next cursor.
            AssertEqual(2, details); // Duplicates are hydrated only once.
            AssertTrue(result.All(x => x.LimitedVerified));
            AssertTrue(result.All(x => !x.PrimaryMarketVerified));
            AssertTrue(result.All(x => x.OriginalPriceText == "—"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static async Task TestAnimationBundleUnconfirmedNeverDisplayedAsync()
    {
        const string search = """
        {"data":[{"id":701,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Unconfirmed Pack","price":300}]}
        """;
        const string details = """
        {"id":701,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Unconfirmed Pack","price":300}
        """;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri?.AbsolutePath.Contains("/search/items/details") == true
                    ? search : details)
            })));
        var root = Path.Combine(Path.GetTempPath(), "rpt-live-unconfirmed", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxAnimationBundleResearchService(http,
                new AppLogger(Path.Combine(root, "test.log")));
            var result = await service.DiscoverAsync();
            AssertEqual(0, result.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static async Task TestAnimationBundleMarketplaceRecoversSparseCollectibleAsync()
    {
        const string search = """
        {"data":[{"id":801,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Sparse Collectible","price":0,
        "collectibleItemId":"sparse-collectible"}]}
        """;
        const string details = """
        {"id":801,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Sparse Collectible","price":300,
        "collectibleItemId":"sparse-collectible"}
        """;
        const string validMarket = """
        [{"collectibleItemId":"sparse-collectible","itemTargetId":801,
        "price":300,"unitsAvailableForConsumption":10,
        "saleLocationType":"ShopAndAllExperiences","lowestResalePrice":500}]
        """;
        const string invalidMarket = """
        [{"collectibleItemId":"sparse-collectible","itemTargetId":999,
        "price":300,"unitsAvailableForConsumption":10,
        "saleLocationType":"ShopAndAllExperiences"}]
        """;

        foreach (var correct in new[] { true, false })
        {
            var calls = 0;
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                Interlocked.Increment(ref calls);
                var uri = request.RequestUri?.AbsolutePath ?? "";
                var data = uri.Contains("/search/items/details", StringComparison.Ordinal) ? search :
                    uri.Contains("/bundles/801/details", StringComparison.Ordinal) ? details :
                    uri.Contains("/marketplace-items/v1/items/details", StringComparison.Ordinal)
                        ? (correct ? validMarket : invalidMarket) :
                    throw new InvalidOperationException("Unexpected endpoint: " + uri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(data)
                });
            }));
            var root = Path.Combine(Path.GetTempPath(), "rpt-sparse-collectible", Guid.NewGuid().ToString("N"));
            try
            {
                var service = new RobloxAnimationBundleResearchService(http,
                    new AppLogger(Path.Combine(root, "test.log")));
                var data = await service.DiscoverAsync();
                AssertEqual(correct ? 1 : 0, data.Count);
                if (correct)
                {
                    AssertTrue(data[0].LimitedVerified);
                    AssertTrue(data[0].PrimaryMarketVerified);
                    AssertEqual(300, data[0].VerifiedOriginalPrice);
                    AssertEqual(500L, data[0].LowestResaleAsk);
                }
                AssertEqual(3, calls);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    static async Task TestAnimationBundleRateLimitPreservesCacheContractAsync()
    {
        const string search = """
        {"data":[{"id":901,"itemType":"Bundle","bundleType":2,
        "creatorTargetId":123,"name":"Rate Limited Pack",
        "itemRestrictions":["Limited"],"collectibleItemId":"rl-bundle"}]}
        """;
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            var isSearch = request.RequestUri?.AbsolutePath.Contains("/search/items/details") == true;
            return Task.FromResult(isSearch
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(search) }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var root = Path.Combine(Path.GetTempPath(), "rpt-bundle-ratelimit", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RobloxAnimationBundleResearchService(http,
                new AppLogger(Path.Combine(root, "test.log")));
            bool rateLimited = false;
            try { await service.DiscoverAsync(); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rateLimited = true;
            }
            AssertTrue(rateLimited);
            AssertEqual(2, calls); // No Marketplace call after 429.
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
