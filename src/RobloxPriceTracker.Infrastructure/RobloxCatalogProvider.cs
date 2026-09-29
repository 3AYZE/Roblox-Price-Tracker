using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Infrastructure;

public sealed record RobloxProviderOptions(int BatchSize = 40)
{
    public int GetValidatedBatchSize() => BatchSize is >= 1 and <= 100 ? BatchSize : 40;
}

public sealed class RobloxCatalogProvider : IMarketProvider
{
    private static readonly Uri Endpoint = new("https://catalog.roblox.com/v1/catalog/items/details");
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly RobloxProviderOptions _options;
    private string? _anonymousCsrfToken;

    public RobloxCatalogProvider(HttpClient httpClient, RobloxProviderOptions? options = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _options = options ?? new RobloxProviderOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "RobloxCatalog";

    public async Task<ProviderFetchResult> FetchAsync(IReadOnlyCollection<ItemKey> items, CancellationToken cancellationToken)
    {
        var distinctItems = items.Distinct().ToArray();
        var observations = new Dictionary<ItemKey, MarketObservation>();
        if (distinctItems.Length == 0)
        {
            return new ProviderFetchResult(observations, null);
        }

        if (distinctItems.Any(x => x.Type != CatalogItemType.Asset))
        {
            return new ProviderFetchResult(observations, new ProviderFailure(
                ProviderFailureKind.Protocol,
                "V1 only supports Roblox catalog assets."));
        }

        var batchSize = _options.GetValidatedBatchSize();
        foreach (var chunk in distinctItems.Chunk(batchSize))
        {
            var result = await FetchChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            foreach (var pair in result.Observations)
            {
                observations[pair.Key] = pair.Value;
            }

            if (result.Failure is not null)
            {
                return new ProviderFetchResult(observations, result.Failure);
            }
        }

        return new ProviderFetchResult(observations, null);
    }

    private async Task<ProviderFetchResult> FetchChunkAsync(ItemKey[] items, CancellationToken cancellationToken)
    {
        var payload = new
        {
            items = items.Select(x => new { itemType = "Asset", id = x.Id }).ToArray()
        };

        HttpResponseMessage response;
        try
        {
            response = await SendCatalogRequestAsync(payload, _anonymousCsrfToken, cancellationToken).ConfigureAwait(false);

            // Roblox documents this endpoint as Cookie None, but anonymous POSTs can still
            // be challenged with HTTP 403 + x-csrf-token. Treat that as a one-time
            // anonymous CSRF handshake; never request or store .ROBLOSECURITY.
            if (response.StatusCode == HttpStatusCode.Forbidden && TryGetCsrfToken(response, out var challengedToken))
            {
                response.Dispose();
                _anonymousCsrfToken = challengedToken;
                response = await SendCatalogRequestAsync(payload, challengedToken, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderFailureKind.Transient, "Roblox request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Failure(ProviderFailureKind.Transient, $"Roblox network request failed: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Failure(
                    ProviderFailureKind.RateLimited,
                    "Roblox returned HTTP 429 Too Many Requests.",
                    GetRetryAfter(response),
                    (int)response.StatusCode);
            }

            if ((int)response.StatusCode >= 500)
            {
                return Failure(
                    ProviderFailureKind.Transient,
                    $"Roblox returned HTTP {(int)response.StatusCode}.",
                    null,
                    (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
                var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
                return Failure(
                    ProviderFailureKind.Protocol,
                    $"Roblox rejected the anonymous catalog request with HTTP 403 after CSRF retry{suffix}.",
                    null,
                    (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    ProviderFailureKind.Protocol,
                    $"Roblox returned unexpected HTTP {(int)response.StatusCode}.",
                    null,
                    (int)response.StatusCode);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ProviderFetchResult(
                    RobloxCatalogParser.Parse(items, document.RootElement, _timeProvider.GetUtcNow()),
                    null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(ProviderFailureKind.Transient, "Roblox response body timed out.");
            }
            catch (IOException ex)
            {
                return Failure(ProviderFailureKind.Transient, $"Roblox response stream failed: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                return Failure(ProviderFailureKind.Transient, $"Roblox response stream failed: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return Failure(ProviderFailureKind.Protocol, $"Roblox returned malformed JSON: {ex.Message}");
            }
        }
    }

    private async Task<HttpResponseMessage> SendCatalogRequestAsync<TPayload>(
        TPayload payload,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);
        }

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetCsrfToken(HttpResponseMessage response, out string token)
    {
        token = string.Empty;
        if (!response.Headers.TryGetValues("x-csrf-token", out var values))
        {
            return false;
        }

        token = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        return token.Length > 0;
    }

    private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 180 ? text : text[..180] + "…";
        }
        catch
        {
            return null;
        }
    }

    private ProviderFetchResult Failure(ProviderFailureKind kind, string message, TimeSpan? retryAfter = null, int? status = null) =>
        new(new Dictionary<ItemKey, MarketObservation>(), new ProviderFailure(kind, message, retryAfter, status));

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retry?.Date is { } date)
        {
            var remaining = date - _timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 2
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxPriceTracker/0.3.0 (+local desktop monitor)");
        return client;
    }
}

public static class RobloxCatalogParser
{
    private static readonly HashSet<string> ResaleMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Limited",
        "LimitedUnique",
        "Collectible"
    };

    public static IReadOnlyDictionary<ItemKey, MarketObservation> Parse(
        IReadOnlyCollection<ItemKey> requested,
        JsonElement root,
        DateTimeOffset observedAtUtc)
    {
        var results = new Dictionary<ItemKey, MarketObservation>();
        var requestedSet = requested.ToHashSet();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Catalog response is missing a data array.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetInt64(item, "id", out var id) || id <= 0)
            {
                continue;
            }

            var itemType = GetString(item, "itemType");
            if (!string.Equals(itemType, "Asset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = new ItemKey(CatalogItemType.Asset, id);
            if (!requestedSet.Contains(key))
            {
                continue;
            }

            results[key] = ParseOne(key, item, observedAtUtc);
        }

        foreach (var key in requestedSet)
        {
            if (!results.ContainsKey(key))
            {
                results[key] = new MarketObservation(
                    key,
                    null,
                    MarketStatus.MissingFromResponse,
                    null,
                    false,
                    observedAtUtc,
                    "Requested item was omitted from the successful batch response.");
            }
        }

        return results;
    }

    private static MarketObservation ParseOne(ItemKey key, JsonElement item, DateTimeOffset observedAtUtc)
    {
        var name = GetString(item, "name");
        var priceStatus = Normalize(GetString(item, "priceStatus"));
        var restrictions = GetStringArray(item, "itemRestrictions");
        var hasResaleMarker = restrictions.Any(x => ResaleMarkers.Contains(x));
        var hasLowestPrice = TryGetInt64(item, "lowestPrice", out var lowestPrice);

        if (hasLowestPrice && PriceValidation.IsValidResalePrice(lowestPrice))
        {
            return new MarketObservation(key, name, MarketStatus.Available, lowestPrice, true, observedAtUtc);
        }

        if (priceStatus == "noresellers")
        {
            return new MarketObservation(key, name, MarketStatus.NoResellers, null, true, observedAtUtc);
        }

        if (priceStatus == "offsale" && hasResaleMarker)
        {
            return new MarketObservation(key, name, MarketStatus.OffSale, null, true, observedAtUtc);
        }

        if (hasLowestPrice && lowestPrice <= 0 && hasResaleMarker)
        {
            return new MarketObservation(
                key,
                name,
                MarketStatus.InvalidPrice,
                null,
                true,
                observedAtUtc,
                "Resellable item returned a non-positive lowestPrice without an explicit NoResellers state.");
        }

        if (hasResaleMarker)
        {
            return new MarketObservation(
                key,
                name,
                MarketStatus.Unknown,
                null,
                true,
                observedAtUtc,
                "Item appears resellable, but no actionable lowestPrice was returned.");
        }

        return new MarketObservation(
            key,
            name,
            MarketStatus.Unsupported,
            null,
            false,
            observedAtUtc,
            "V1 only tracks resellable Limited/collectible assets.");
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string[] GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
