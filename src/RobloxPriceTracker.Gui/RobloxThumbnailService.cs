using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace RobloxPriceTracker.Gui;

public sealed class RobloxThumbnailService
{
    private static readonly Uri Endpoint = new("https://thumbnails.roblox.com/v1/assets");
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<long, string> _cache = new();

    public RobloxThumbnailService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyDictionary<long, string>> GetAssetThumbnailUrlsAsync(
        IEnumerable<long> assetIds,
        CancellationToken cancellationToken = default)
    {
        var ids = assetIds.Where(x => x > 0).Distinct().ToArray();
        var result = new Dictionary<long, string>();
        var missing = new List<long>();

        foreach (var id in ids)
        {
            if (_cache.TryGetValue(id, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                result[id] = cached;
            }
            else
            {
                missing.Add(id);
            }
        }

        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                var idList = string.Join(',', chunk);
                var uri = new Uri($"{Endpoint}?assetIds={Uri.EscapeDataString(idList)}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("targetId", out var targetIdNode) || !targetIdNode.TryGetInt64(out var targetId))
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("imageUrl", out var imageNode) || imageNode.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var imageUrl = imageNode.GetString();
                    if (string.IsNullOrWhiteSpace(imageUrl))
                    {
                        continue;
                    }

                    _cache[targetId] = imageUrl;
                    result[targetId] = imageUrl;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return result;
    }

    public async Task<string?> GetAssetThumbnailUrlAsync(long assetId, CancellationToken cancellationToken = default)
    {
        var result = await GetAssetThumbnailUrlsAsync(new[] { assetId }, cancellationToken).ConfigureAwait(false);
        return result.TryGetValue(assetId, out var url) ? url : null;
    }
}
