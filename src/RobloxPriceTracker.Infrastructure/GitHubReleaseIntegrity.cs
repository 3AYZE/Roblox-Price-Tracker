using System.Text.RegularExpressions;

namespace RobloxPriceTracker.Infrastructure;

public static class GitHubReleaseIntegrity
{
    private static readonly Regex TitleVersion = new(
        @"\bv(?<version>\d+\.\d+\.\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Digest = new(
        @"\Asha256:(?<hash>[a-fA-F0-9]{64})\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryReadVersion(string? title, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = TitleVersion.Match(title ?? string.Empty);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    public static bool TryReadDigest(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        var match = Digest.Match(digest ?? string.Empty);
        if (!match.Success) return false;
        sha256 = match.Groups["hash"].Value.ToLowerInvariant();
        return true;
    }

    public static bool NeedsDownload(Version installed, Version published, string? installedHash, string publishedHash)
    {
        int comparison = published.CompareTo(installed);
        return comparison > 0 || (comparison == 0 && installedHash is not null &&
            !string.Equals(installedHash, publishedHash, StringComparison.OrdinalIgnoreCase));
    }
}
