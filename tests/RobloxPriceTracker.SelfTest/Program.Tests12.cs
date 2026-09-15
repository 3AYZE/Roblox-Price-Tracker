using System.Net;
using System.Text;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static async Task TestUgcDiscoveryFallsBackFromProtocolErrorAsync()
    {
        var searchCalls = 0;
        var detailCalls = 0;
        var sawBroadFallback = false;

        using var handler = new DelegateHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                searchCalls++;
                var query = request.RequestUri?.Query ?? string.Empty;
                if (query.Contains("Category=2", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"errors\":[{\"message\":\"unsupported category route\"}]}", Encoding.UTF8, "application/json")
                    };
                }

                sawBroadFallback |= query.Contains("Category=1", StringComparison.OrdinalIgnoreCase);
                var json = JsonSerializer.Serialize(new
                {
                    previousPageCursor = (string?)null,
                    nextPageCursor = (string?)null,
                    data = Enumerable.Range(0, 30).Select(i => new
                    {
                        id = 50_000L + i,
                        itemType = "Asset",
                        assetType = 8,
                        creatorTargetId = 12345,
                        itemRestrictions = new[] { "Collectible" }
                    }).ToArray()
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            detailCalls++;
            var payload = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            var ids = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt64())
                .ToArray();

            var detailJson = JsonSerializer.Serialize(new
            {
                data = ids.Select(id => new
                {
                    id,
                    itemType = "Asset",
                    name = $"Limited {id}",
                    creatorName = "UGC Creator",
                    creatorTargetId = 12345,
                    assetType = 8,
                    price = 95,
                    unitsAvailableForConsumption = 100,
                    totalQuantity = 1000,
                    saleLocationType = "ShopAndAllExperiences",
                    itemRestrictions = new[] { "Collectible" }
                }).ToArray()
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "rpt-selftest", Guid.NewGuid().ToString("N"), "app.log"));
        var service = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: false);
        var result = await service.DiscoverAsync();

        AssertEqual(2, searchCalls);
        AssertTrue(sawBroadFallback);
        AssertEqual(1, detailCalls);
        AssertEqual(30, result.Items.Count);
    }

    static async Task TestUgcDiscoveryRecoversPartialBadDetailBatchAsync()
    {
        var detailCalls = 0;
        const long firstId = 70_000L;

        using var handler = new DelegateHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var json = JsonSerializer.Serialize(new
                {
                    previousPageCursor = (string?)null,
                    nextPageCursor = (string?)null,
                    data = Enumerable.Range(0, 30).Select(i => new
                    {
                        id = firstId + i,
                        itemType = "Asset",
                        assetType = 8,
                        creatorTargetId = 12345,
                        itemRestrictions = new[] { "Collectible" }
                    }).ToArray()
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            detailCalls++;
            var payload = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            var ids = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt64())
                .ToArray();

            // The first 30-ID batch is rejected. During bounded isolation, only the final 10-ID
            // chunk stays bad. Hunter must keep the 20 valid candidates instead of surfacing API ERROR.
            if (ids.Length > 10 || ids.Any(id => id >= firstId + 20))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"errors\":[{\"message\":\"one or more invalid assets\"}]}", Encoding.UTF8, "application/json")
                };
            }

            var detailJson = JsonSerializer.Serialize(new
            {
                data = ids.Select(id => new
                {
                    id,
                    itemType = "Asset",
                    name = $"Limited {id}",
                    creatorName = "UGC Creator",
                    creatorTargetId = 12345,
                    assetType = 8,
                    price = 95,
                    unitsAvailableForConsumption = 100,
                    totalQuantity = 1000,
                    saleLocationType = "ShopAndAllExperiences",
                    itemRestrictions = new[] { "Collectible" }
                }).ToArray()
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var logger = new AppLogger(Path.Combine(Path.GetTempPath(), "rpt-selftest", Guid.NewGuid().ToString("N"), "app.log"));
        var service = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: false);
        var result = await service.DiscoverAsync();

        AssertEqual(4, detailCalls);
        AssertEqual(20, result.HydratedCount);
        AssertEqual(20, result.Items.Count);
        AssertTrue(result.Items.All(x => x.AssetId < firstId + 20));
    }
}
