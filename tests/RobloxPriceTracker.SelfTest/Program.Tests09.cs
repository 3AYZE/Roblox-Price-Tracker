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

        // First-seen repricings must also be rejected from Roblox market evidence even when
        // Hunter never personally observed the old primary price.
        AssertTrue(UgcHunterPrimaryPricePolicy.IsMarketReferenceIncrease(300, 95d, null));
        AssertTrue(UgcHunterPrimaryPricePolicy.IsMarketReferenceIncrease(68_000_000, 95d, null));
        AssertTrue(UgcHunterPrimaryPricePolicy.IsMarketReferenceIncrease(300, null, 95));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsMarketReferenceIncrease(150, 95d, null));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsMarketReferenceIncrease(95, 95d, null));

        // UGC Hunter should never surface Roblox-published inventory as a UGC opportunity.
        AssertTrue(UgcHunterPrimaryPricePolicy.IsClearlyNonUgcPublisher(1, "Roblox"));
        AssertTrue(UgcHunterPrimaryPricePolicy.IsClearlyNonUgcPublisher(12345, "Roblox"));
        AssertTrue(!UgcHunterPrimaryPricePolicy.IsClearlyNonUgcPublisher(12345, "UGC Creator"));
        return Task.CompletedTask;
    }

    static async Task TestUgcDiscoveryCombinesMultipleLimitedFeedsAsync()
    {
        const long caesarCrownAssetId = 96_423_734_124_931L;
        var searchCalls = 0;
        var detailCalls = 0;
        var totalCalls = 0;
        var maxDetailBatch = 0;
        var searchQueries = new List<string>();

        using var handler = new DelegateHandler(async (request, _) =>
        {
            totalCalls++;
            if (request.Method == HttpMethod.Get)
            {
                searchCalls++;
                searchQueries.Add(request.RequestUri?.Query ?? string.Empty);

                IEnumerable<long> ids = searchCalls switch
                {
                    1 => Enumerable.Range(1000, 30).Select(x => (long)x),
                    // Reference regression: an older item can be absent from the daily feed and
                    // still be discovered because it becomes a high seller in the weekly window.
                    2 => new[] { caesarCrownAssetId }.Concat(Enumerable.Range(1030, 29).Select(x => (long)x)),
                    3 => Enumerable.Range(1060, 30).Select(x => (long)x),
                    4 => Enumerable.Range(1090, 30).Select(x => (long)x),
                    _ => Enumerable.Range(1120, 30).Select(x => (long)x)
                };

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
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            AssertEqual(HttpMethod.Post, request.Method);
            AssertTrue(request.RequestUri?.Host.Equals("catalog.roblox.com", StringComparison.OrdinalIgnoreCase) == true);
            detailCalls++;

            var payload = await request.Content!.ReadAsStringAsync();
            using var payloadDocument = JsonDocument.Parse(payload);
            var requestedIds = payloadDocument.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt64())
                .ToArray();
            maxDetailBatch = Math.Max(maxDetailBatch, requestedIds.Length);

            var detailJson = JsonSerializer.Serialize(new
            {
                data = requestedIds.Select(id => new
                {
                    id,
                    itemType = "Asset",
                    name = id == caesarCrownAssetId ? "Caesar Crown" : $"Limited {id}",
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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var logPath = Path.Combine(Path.GetTempPath(), "rpt-selftest", Guid.NewGuid().ToString("N"), "app.log");
        var logger = new AppLogger(logPath);
        var service = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: true);
        var result = await service.DiscoverAsync();

        AssertEqual(5, searchCalls);
        AssertEqual(2, detailCalls);
        AssertEqual(7, totalCalls);
        AssertTrue(maxDetailBatch <= 40);
        AssertEqual(80, result.DiscoveredCount);
        AssertEqual(80, result.HydratedCount);
        AssertEqual(80, result.Items.Count);
        AssertTrue(result.Items.Any(x => x.AssetId == caesarCrownAssetId));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=1", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=3", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=4", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortType=3", StringComparison.OrdinalIgnoreCase)));
    }
}
