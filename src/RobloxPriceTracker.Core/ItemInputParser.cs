using System.Text.RegularExpressions;

namespace RobloxPriceTracker.Core;

public static partial class ItemInputParser
{
    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericIdRegex();

    public static bool TryParseAsset(string input, out ItemKey key, out string? error)
    {
        key = default;
        error = null;
        input = input.Trim();

        if (NumericIdRegex().IsMatch(input) && long.TryParse(input, out var id) && id > 0)
        {
            key = new ItemKey(CatalogItemType.Asset, id);
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "Enter a positive Roblox asset ID or a Roblox catalog URL.";
            return false;
        }

        if (!uri.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only roblox.com catalog URLs are accepted.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("catalog", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(segments[i + 1], out id) && id > 0)
            {
                key = new ItemKey(CatalogItemType.Asset, id);
                return true;
            }
        }

        error = "The Roblox URL does not contain a valid /catalog/{assetId} path.";
        return false;
    }
}
