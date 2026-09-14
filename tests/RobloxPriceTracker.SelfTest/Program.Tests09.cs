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
        var sawCursorRequest = false;
        var searchQueries = new List<string>();
        var generatedId = 10_000L;

        using var handler = new DelegateHandler(async (request, _) =>
        {
            totalCalls++;
            if (request.Method == HttpMethod.Get)
            {
                searchCalls++;
                var query = request.RequestUri?.Query ?? string.Empty;
                searchQueries.Add(query);
                var isCursorPage = query.Contains("cursor=", StringComparison.OrdinalIgnoreCase);
                sawCursorRequest |= isCursorPage;

                var isUgcDay = query.Contains("Category=13", StringComparison.OrdinalIgnoreCase) &&
                               query.Contains("SortAggregation=1", StringComparison.OrdinalIgnoreCase);
                var isUgcWeek = query.Contains("Category=13", StringComparison.OrdinalIgnoreCase) &&
                                query.Contains("SortAggregation=3", StringComparison.OrdinalIgnoreCase);

                var ids = new List<long>(30);
                if (isCursorPage && (isUgcDay || isUgcWeek))
                {
                    // Regression target: Caesar Crown is deliberately outside the first 30 rows.
                    // It must still be discovered because the strong sales feeds are paginated.
                    ids.Add(caesarCrownAssetId);
                    for (var i = 1; i < 30; i++) ids.Add(generatedId++);
                }
                else
                {
                    for (var i = 0; i < 30; i++) ids.Add(generatedId++);
                }

                var nextCursor = !isCursorPage && (isUgcDay || isUgcWeek)
                    ? $"page2-{searchCalls}"
                    : null;

                var json = JsonSerializer.Serialize(new
                {
                    previousPageCursor = (string?)null,
                    nextPageCursor = nextCursor,
                    data = ids.Select(id => new
                    {
                        id,
                        itemType = "Asset",
                        assetType = 8,
                        creatorTargetId = 12345,
                        price = 0,
                        // Search metadata can omit itemRestrictions in production. Hydration below
                        // is the strict Limited check, so discovery must not lose the ID here.
                        itemRestrictions = id == caesarCrownAssetId ? null : new[] { "Collectible" }
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
                    unitsAvailableForConsumption = 1464,
                    totalQuantity = 2500,
                    saleLocationType = "ShopAndAllExperiences",
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

        AssertTrue(searchCalls >= 8);
        AssertTrue(sawCursorRequest);
        AssertTrue(maxDetailBatch <= 40);
        AssertTrue(detailCalls >= 1 && detailCalls <= 4);
        AssertTrue(totalCalls >= searchCalls + detailCalls);
        AssertTrue(result.DiscoveredCount > 30 && result.DiscoveredCount <= 140);
        AssertEqual(result.DiscoveredCount, result.HydratedCount);
        AssertEqual(result.DiscoveredCount, result.Items.Count);
        AssertTrue(result.Items.Any(x => x.AssetId == caesarCrownAssetId));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=1", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=3", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortAggregation=4", StringComparison.OrdinalIgnoreCase)));
        AssertTrue(searchQueries.Any(x => x.Contains("SortType=3", StringComparison.OrdinalIgnoreCase)));
    }
}
