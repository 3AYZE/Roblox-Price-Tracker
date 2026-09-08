using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

public sealed class DevelopmentUpdateService : IDisposable
{
    public const string DevelopmentTag = "main-latest";

    private const string LiteExecutableName = "RobloxPriceTracker-Lite.exe";
    private const string LiteChecksumName = "RobloxPriceTracker-Lite.exe.sha256";
    private static readonly Regex Sha256Regex = new(@"\b[a-fA-F0-9]{64}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionRegex = new(@"\bv(?<version>\d+\.\d+\.\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _repository;
    private readonly AppLogger _logger;
    private readonly string? _token;
    private readonly HttpClient _httpClient;

    public DevelopmentUpdateService(string repository, AppLogger logger)
    {
        _repository = repository;
        _logger = logger;
        _token = NormalizeToken(Environment.GetEnvironmentVariable("RPT_GITHUB_TOKEN"));
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new Uri($"https://api.github.com/repos/{_repository}/releases/tags/{DevelopmentTag}");
            using var request = CreateGitHubRequest(HttpMethod.Get, endpoint, "application/vnd.github+json", currentVersion);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var message = string.IsNullOrWhiteSpace(_token)
                    ? $"No accessible Latest / Development feed is available for {_repository}."
                    : "No successful Latest / Development build has been published yet.";
                return new UpdateCheckResult(false, message);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                return new UpdateCheckResult(false, "GitHub temporarily rate-limited the development update check. The app will try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, $"Latest / Development update check returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var prerelease = root.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.ValueKind == JsonValueKind.True;
            if (!prerelease)
            {
                return new UpdateCheckResult(false, "The development feed is not marked as a prerelease, so it was ignored for safety.");
            }

            var releaseName = root.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString())
                ? nameElement.GetString()!.Trim()
                : "Latest / Development";
            var releaseVersion = ParseReleaseVersion(releaseName) ?? NormalizeVersion(currentVersion);
            var htmlUrlText = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            var htmlUrl = Uri.TryCreate(htmlUrlText, UriKind.Absolute, out var parsedHtml)
                ? parsedHtml
                : new Uri($"https://github.com/{_repository}/releases/tag/{DevelopmentTag}");

            GitHubReleaseAsset? executable = null;
            GitHubReleaseAsset? checksum = null;
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    var assetName = assetElement.TryGetProperty("name", out var assetNameElement) ? assetNameElement.GetString() : null;
                    var apiUrlText = assetElement.TryGetProperty("url", out var apiUrlElement) ? apiUrlElement.GetString() : null;
                    var size = assetElement.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                    if (string.IsNullOrWhiteSpace(assetName) || !Uri.TryCreate(apiUrlText, UriKind.Absolute, out var apiUrl)) continue;

                    var asset = new GitHubReleaseAsset(assetName, apiUrl, size);
                    if (string.Equals(assetName, LiteExecutableName, StringComparison.OrdinalIgnoreCase)) executable = asset;
                    else if (string.Equals(assetName, LiteChecksumName, StringComparison.OrdinalIgnoreCase)) checksum = asset;
                }
            }

            if (executable is null || checksum is null || executable.Size < 100_000)
            {
                return new UpdateCheckResult(false, "The Latest / Development build exists, but its verified Windows update files are incomplete.");
            }

            var checksumText = await DownloadTextAssetAsync(checksum, currentVersion, cancellationToken);
            var expectedHash = ExtractSha256(checksumText);
            if (expectedHash is null)
            {
                return new UpdateCheckResult(false, "The Latest / Development checksum is invalid, so the build was ignored.");
            }

            var currentExecutable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExecutable)
                && File.Exists(currentExecutable)
                && !string.Equals(Path.GetFileName(currentExecutable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var currentHash = await ComputeSha256Async(currentExecutable, cancellationToken);
                if (string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateCheckResult(false, $"Up to date · {releaseName}");
                }
            }

            return new UpdateCheckResult(
                true,
                $"{releaseName} is available.",
                new GitHubReleaseInfo(releaseVersion, DevelopmentTag, releaseName, htmlUrl, executable, checksum));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Latest / Development update check failed: {ex}");
            return new UpdateCheckResult(false, "Latest / Development update check failed. See the application log for details.");
        }
    }

    private async Task<string> DownloadTextAssetAsync(GitHubReleaseAsset asset, Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream", currentVersion);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("The development checksum file is unexpectedly large.");
        return Encoding.UTF8.GetString(bytes);
    }

    private HttpRequestMessage CreateGitHubRequest(HttpMethod method, Uri uri, string accept, Version currentVersion)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd($"RobloxMarketHelper/{FormatVersion(currentVersion)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(_token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static Version? ParseReleaseVersion(string releaseName)
    {
        var match = VersionRegex.Match(releaseName);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var parsed)
            ? NormalizeVersion(parsed)
            : null;
    }

    private static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Sha256Regex.Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static string? NormalizeToken(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose() => _httpClient.Dispose();
}
