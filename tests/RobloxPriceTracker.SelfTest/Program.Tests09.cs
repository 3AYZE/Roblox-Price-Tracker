using System.Net;
using System.Text;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcHunterLargePrimaryPriceIncreasePolicyAsync()
    {
        AssertTrue(UgcHunterPrimaryPricePolicy.IsLargeIncrease(300, 95));
        AssertTrue(UgcHunterPrimaryPricePolicy.IsLargeIncrease(175, 100));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsLargeIncrease(150, 95));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsLargeIncrease(120, 95));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsLargeIncrease(95, 95));
        return Task.CompletedTask;
    }

    static async Task TestUgcDiscoveryCombinesMultipleLimitedFeedsAsync()
    {
        var searchCalls = 0;
        var totalCalls = 0;
        using var handler = new DelegateHandler((request, _) =>
        {
            totalCalls++;
            if (request.Method == HttpMethod.Get)
            {
                searchCalls++;
                var ids = searchCalls == 1
                    ? Enumerable.Range(1000, 30).Select(x => (long)x)
                    : Enumerable.Range(1020, 30).Select(x => (long)x);
                var json = JsonSerializer.Serialize(new
                {
                    data = ids.Select(id => new
                    {
                        id,
                        itemType = "Asset",
                        assetType = 8,
                        creatorTargetId = 12345,
                        price = 0,
                        itemRestrictions = new[] { "Collectible" }
                    }).ToArray()
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            AssertEqual(HttpMethod.Post, request.Method);
            var detailJson = JsonSerializer.Serialize(new
            {
                data = Enumerable.Range(1000, 40).Select(x => new
                {
                    id = (long)x,
                    itemType = "Asset",
                    name = $"Limited {x}",
                    creatorName = "UGC Creator",
                    creatorTargetId = 12345,
                    assetType = 8,
                    price = 95,
                    unitsAvailableForConsumption = 20,
                    totalQuantity = 100,
                    saleLocationType = "ShopOnly",
                    itemRestrictions = new[] { "Collectible" },
                    itemStatus = new[] { "Sale" }
                }).ToArray()
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            });
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var logPath = Path.Combine(Path.GetTempPath(), "rpt-selftest", Guid.NewGuid().ToString("N"), "app.log");
        var logger = new AppLogger(logPath);
        var service = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: true);
        var result = await service.DiscoverAsync();

        AssertEqual(2, searchCalls);
        AssertEqual(3, totalCalls);
        AssertEqual(40, result.DiscoveredCount);
        AssertEqual(40, result.HydratedCount);
        AssertEqual(40, result.Items.Count);
    }
}
