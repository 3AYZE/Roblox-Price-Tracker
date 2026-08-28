using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcDiscoveryAcceptsZeroPriceSearchRowAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 126389144425805,
              "itemType": "Asset",
              "assetType": 8,
              "creatorTargetId": 12345,
              "price": 0,
              "purchaseCount": null,
              "itemRestrictions": ["Collectible"]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var ids = RobloxUgcCatalogDiscoveryParser.ParseDiscoveryIds(doc.RootElement);
        AssertEqual(1, ids.Count);
        AssertEqual(126389144425805L, ids[0]);
        return Task.CompletedTask;
    }

    static Task TestUgcHydratedPaidShopLimitedBecomesCandidateAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 126389144425805,
              "itemType": "Asset",
              "name": "Hydrated Limited",
              "creatorName": "UGC Creator",
              "creatorTargetId": 12345,
              "assetType": 8,
              "price": 95,
              "priceStatus": "For Sale",
              "unitsAvailableForConsumption": 2840,
              "totalQuantity": 3000,
              "purchaseCount": null,
              "favoriteCount": 100,
              "saleLocationType": "ShopOnly",
              "itemRestrictions": ["Collectible"],
              "itemStatus": ["Sale"]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var rows = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(doc.RootElement);
        AssertEqual(1, rows.Count);
        AssertEqual(95, rows[0].Price);
        AssertEqual(160L, rows[0].PurchaseCount);
        AssertEqual(2840L, rows[0].UnitsAvailable!.Value);
        return Task.CompletedTask;
    }

    static Task TestUgcHydratedZeroPriceLimitedIsExcludedAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 126389144425806,
              "itemType": "Asset",
              "name": "Zero Price Limited",
              "creatorName": "UGC Creator",
              "creatorTargetId": 12345,
              "assetType": 8,
              "price": 0,
              "unitsAvailableForConsumption": 50,
              "totalQuantity": 100,
              "saleLocationType": "ShopOnly",
              "itemRestrictions": ["Collectible"],
              "itemStatus": ["Sale"]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var rows = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(doc.RootElement);
        AssertEqual(0, rows.Count);
        return Task.CompletedTask;
    }

    static Task TestUgcHydratedUnavailableLimitedIsExcludedAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 126389144425807,
              "itemType": "Asset",
              "name": "Unavailable Limited",
              "creatorName": "UGC Creator",
              "creatorTargetId": 12345,
              "assetType": 8,
              "price": 95,
              "priceStatus": "OffSale",
              "isOffSale": true,
              "unitsAvailableForConsumption": 0,
              "totalQuantity": 100,
              "saleLocationType": "ShopOnly",
              "itemRestrictions": ["Collectible"],
              "itemStatus": []
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var rows = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(doc.RootElement);
        AssertEqual(0, rows.Count);
        return Task.CompletedTask;
    }

    static Task TestUgcHydratedExperienceOnlyLimitedIsExcludedAsync()
    {
        const string json = """
        {
          "data": [
            {
              "id": 987654321,
              "itemType": "Asset",
              "name": "Experience Only",
              "creatorTargetId": 12345,
              "assetType": 43,
              "price": 95,
              "unitsAvailableForConsumption": 100,
              "totalQuantity": 200,
              "saleLocationType": "ExperiencesDevApiOnly",
              "itemRestrictions": ["Collectible"],
              "itemStatus": ["Sale"]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var rows = RobloxUgcCatalogDiscoveryParser.ParseHydratedCandidates(doc.RootElement);
        AssertEqual(0, rows.Count);
        return Task.CompletedTask;
    }

    static async Task TestUgcDiscoveryRetriesAnonymousCsrfAndHydratesAsync()
    {
        var call = 0;
        using var handler = new DelegateHandler((request, _) =>
        {
            call++;
            if (call == 1)
            {
                AssertEqual(HttpMethod.Get, request.Method);
                const string searchJson = """
                {"data":[{"id":126389144425805,"itemType":"Asset","assetType":8,"creatorTargetId":12345,"price":0,"itemRestrictions":["Collectible"]}]}
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchJson, Encoding.UTF8, "application/json")
                });
            }

            AssertEqual(HttpMethod.Post, request.Method);
            if (call == 2)
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
                forbidden.Headers.TryAddWithoutValidation("x-csrf-token", "anonymous-test-token");
                return Task.FromResult(forbidden);
            }

            AssertTrue(request.Headers.TryGetValues("x-csrf-token", out var values));
            AssertEqual("anonymous-test-token", values!.Single());
            const string detailJson = """
            {"data":[{"id":126389144425805,"itemType":"Asset","name":"Hydrated Limited","creatorName":"UGC Creator","creatorTargetId":12345,"assetType":8,"price":95,"unitsAvailableForConsumption":20,"totalQuantity":100,"saleLocationType":"ShopOnly","itemRestrictions":["Collectible"],"itemStatus":["Sale"]}]}
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            });
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var logPath = Path.Combine(Path.GetTempPath(), "rpt-selftest", Guid.NewGuid().ToString("N"), "app.log");
        var logger = new AppLogger(logPath);
        var service = new RobloxUgcDiscoveryService(http, logger, expandDiscovery: false);
        var result = await service.DiscoverAsync();

        AssertEqual(3, call);
        AssertEqual(1, result.DiscoveredCount);
        AssertEqual(1, result.HydratedCount);
        AssertEqual(1, result.Items.Count);
        AssertEqual(95, result.Items[0].Price);
    }
}
