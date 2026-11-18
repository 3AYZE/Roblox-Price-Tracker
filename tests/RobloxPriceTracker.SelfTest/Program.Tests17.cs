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
}
