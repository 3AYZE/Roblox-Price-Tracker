using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Parses the public Roblox thumbnails response for either asset or bundle IDs.
/// A pending, blocked, missing or non-HTTPS image is not a usable PNG preview.
/// </summary>
public static class RobloxThumbnailResponseParser
{
    public static IReadOnlyDictionary<long, string> ParseCompleted(
        JsonElement root, IReadOnlySet<long> requestedIds)
    {
        var result = new Dictionary<long, string>();
        if (!root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("targetId", out var idNode) ||
                idNode.ValueKind != JsonValueKind.Number ||
                !idNode.TryGetInt64(out var id) || id <= 0 || !requestedIds.Contains(id))
                continue;

            if (!row.TryGetProperty("state", out var state) ||
                state.ValueKind != JsonValueKind.String ||
                !string.Equals(state.GetString(), "Completed", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!row.TryGetProperty("imageUrl", out var urlValue) ||
                urlValue.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                !IsRobloxImageHost(url.Host))
                continue;
            result[id] = url.AbsoluteUri;
        }
        return result;
    }

    private static bool IsRobloxImageHost(string hostname) =>
        hostname.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) ||
        hostname.Equals("rbxcdn.com", StringComparison.OrdinalIgnoreCase) ||
        hostname.Equals("thumbnails.roblox.com", StringComparison.OrdinalIgnoreCase);
}
