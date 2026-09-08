using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxPriceTracker.Gui;

public static class GitHubUpdateServiceMainLatestExtensions
{
    private const string MainLatestTag = "main-latest";
    private const string LiteExecutableName = "RobloxPriceTracker-Lite.exe";
    private const string LiteChecksumName = "RobloxPriceTracker-Lite.exe.sha256";
    private static readonly Regex Sha256Regex = new(@"\b[a-fA-F0-9]{64}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<UpdateCheckResult> CheckMainLatestAsync(
        this GitHubUpdateService service,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true
            })
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            var endpoint = new Uri($"https://api.github.com/repos/{service.Repository}/releases/tags/{MainLatestTag}");
            using var request = CreateRequest(HttpMethod.Get, endpoint, "application/vnd.github+json", service.CurrentVersion);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false, "Latest channel has no successful main build yet.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                return new UpdateCheckResult(false, "GitHub temporarily rate-limited the Latest update check.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, $"Latest update check returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var releaseName = root.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString())
                ? nameElement.GetString()!.Trim()
                : "Latest successful main build";
            var htmlUrlText = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            var htmlUrl = Uri.TryCreate(htmlUrlText, UriKind.Absolute, out var parsedHtml)
                ? parsedHtml
                : new Uri($"https://github.com/{service.Repository}/releases/tag/{MainLatestTag}");

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

            if (executable is null || checksum is null)
            {
                return new UpdateCheckResult(false, "Latest successful main build is missing its verified Windows update files.");
            }

            var expectedHash = await DownloadChecksumAsync(client, checksum, service.CurrentVersion, cancellationToken).ConfigureAwait(false);
            if (expectedHash is null)
            {
                return new UpdateCheckResult(false, "Latest build checksum is invalid; the update was not offered.");
            }

            var currentHash = await TryHashCurrentExecutableAsync(cancellationToken).ConfigureAwait(false);
            if (currentHash is not null && string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult(false, "Latest channel up to date · newest successful main build installed");
            }

            return new UpdateCheckResult(
                true,
                "A newer successful main build is available.",
                new GitHubReleaseInfo(service.CurrentVersion, MainLatestTag, releaseName, htmlUrl, executable, checksum));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new UpdateCheckResult(false, "Latest update check failed. The Stable channel is unaffected.");
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string accept, Version currentVersion)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd($"RobloxMarketHelper/{currentVersion.Major}.{currentVersion.Minor}.{Math.Max(0, currentVersion.Build)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        var token = Environment.GetEnvironmentVariable("RPT_GITHUB_TOKEN")?.Trim();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string?> DownloadChecksumAsync(
        HttpClient client,
        GitHubReleaseAsset checksum,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, checksum.ApiUrl, "application/octet-stream", currentVersion);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 64 * 1024) return null;
        var match = Sha256Regex.Match(Encoding.UTF8.GetString(bytes));
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static async Task<string?> TryHashCurrentExecutableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
