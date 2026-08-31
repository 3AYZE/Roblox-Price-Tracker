using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

public sealed record GitHubReleaseAsset(string Name, Uri ApiUrl, long Size);

public sealed record GitHubReleaseInfo(
    Version Version,
    string TagName,
    string ReleaseName,
    Uri HtmlUrl,
    GitHubReleaseAsset Executable,
    GitHubReleaseAsset Checksum);

public sealed record UpdateCheckResult(bool UpdateAvailable, string Message, GitHubReleaseInfo? Release = null);

public sealed class GitHubUpdateService : IDisposable
{
    public const string DefaultRepository = "3AYZE/Roblox-Price-Tracker";

    private const string LiteExecutableName = "RobloxPriceTracker-Lite.exe";
    private const string LiteChecksumName = "RobloxPriceTracker-Lite.exe.sha256";
    private const string LegacyExecutableName = "RobloxPriceTracker.exe";
    private const string LegacyChecksumName = "RobloxPriceTracker.exe.sha256";

    private static readonly Regex Sha256Regex = new(@"\b[a-fA-F0-9]{64}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _dataDirectory;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _token;

    public GitHubUpdateService(string dataDirectory, AppLogger logger)
    {
        _dataDirectory = dataDirectory;
        _logger = logger;
        _token = NormalizeToken(Environment.GetEnvironmentVariable("RPT_GITHUB_TOKEN"));
        Repository = NormalizeRepository(Environment.GetEnvironmentVariable("RPT_UPDATE_REPOSITORY")) ?? DefaultRepository;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public string Repository { get; }

    public Version CurrentVersion => NormalizeVersion(typeof(GitHubUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
            using var request = CreateGitHubRequest(HttpMethod.Get, endpoint, "application/vnd.github+json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var message = string.IsNullOrWhiteSpace(_token)
                    ? $"No public GitHub Release feed is available for {Repository}. The repository may be private."
                    : $"No GitHub Release is published for {Repository} yet.";
                return new UpdateCheckResult(false, message);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                return new UpdateCheckResult(false, "GitHub temporarily rate-limited the update check. Roblox Market Helper will try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, $"GitHub update check returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (!TryParseVersionTag(tag, out var latestVersion))
            {
                return new UpdateCheckResult(false, "The latest GitHub Release does not contain a valid version tag.");
            }

            var releaseName = root.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString())
                ? nameElement.GetString()!.Trim()
                : tag!;
            var htmlUrlText = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            var htmlUrl = Uri.TryCreate(htmlUrlText, UriKind.Absolute, out var parsedHtml)
                ? parsedHtml
                : new Uri($"https://github.com/{Repository}/releases");

            GitHubReleaseAsset? liteExecutable = null;
            GitHubReleaseAsset? liteChecksum = null;
            GitHubReleaseAsset? legacyExecutable = null;
            GitHubReleaseAsset? legacyChecksum = null;
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    var assetName = assetElement.TryGetProperty("name", out var assetNameElement) ? assetNameElement.GetString() : null;
                    var apiUrlText = assetElement.TryGetProperty("url", out var apiUrlElement) ? apiUrlElement.GetString() : null;
                    var size = assetElement.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                    if (string.IsNullOrWhiteSpace(assetName) || !Uri.TryCreate(apiUrlText, UriKind.Absolute, out var apiUrl)) continue;

                    var asset = new GitHubReleaseAsset(assetName, apiUrl, size);
                    if (string.Equals(assetName, LiteExecutableName, StringComparison.OrdinalIgnoreCase)) liteExecutable = asset;
                    else if (string.Equals(assetName, LiteChecksumName, StringComparison.OrdinalIgnoreCase)) liteChecksum = asset;
                    else if (string.Equals(assetName, LegacyExecutableName, StringComparison.OrdinalIgnoreCase)) legacyExecutable = asset;
                    else if (string.Equals(assetName, LegacyChecksumName, StringComparison.OrdinalIgnoreCase)) legacyChecksum = asset;
                }
            }

            // Prefer the current framework-dependent Lite release pair. Keep the legacy pair as a
            // compatibility fallback so older/custom release feeds continue to update safely.
            var executable = liteExecutable is not null && liteChecksum is not null ? liteExecutable : legacyExecutable;
            var checksum = liteExecutable is not null && liteChecksum is not null ? liteChecksum : legacyChecksum;

            var current = CurrentVersion;
            var latest = NormalizeVersion(latestVersion);
            if (latest.CompareTo(current) <= 0)
            {
                return new UpdateCheckResult(false, $"Up to date · v{FormatVersion(current)}");
            }

            if (executable is null || checksum is null)
            {
                return new UpdateCheckResult(false, $"Release v{FormatVersion(latest)} is newer, but its verified Windows update files are incomplete.");
            }

            return new UpdateCheckResult(
                true,
                $"Update v{FormatVersion(latest)} is available.",
                new GitHubReleaseInfo(latest, tag!, releaseName, htmlUrl, executable, checksum));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"GitHub update check failed: {ex}");
            return new UpdateCheckResult(false, "Update check failed. See the application log for details.");
        }
    }

    public async Task<string> DownloadAndVerifyAsync(GitHubReleaseInfo release, CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(_dataDirectory, "updates");
        Directory.CreateDirectory(updateDirectory);

        var checksumText = await DownloadTextAssetAsync(release.Checksum, cancellationToken);
        var expectedHash = ExtractSha256(checksumText)
            ?? throw new InvalidDataException("The release checksum file does not contain a valid SHA-256 hash.");

        var versionText = FormatVersion(release.Version);
        var finalPath = Path.Combine(updateDirectory, $"RobloxMarketHelper-v{versionText}.exe");
        if (File.Exists(finalPath))
        {
            var existingHash = await ComputeSha256Async(finalPath, cancellationToken);
            if (string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase) && IsPortableExecutable(finalPath))
            {
                return finalPath;
            }
            try { File.Delete(finalPath); } catch { }
        }

        var temporaryPath = finalPath + ".download";
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            await DownloadBinaryAssetAsync(release.Executable, temporaryPath, cancellationToken);

            var length = new FileInfo(temporaryPath).Length;
            if (length < 100_000)
            {
                throw new InvalidDataException("The downloaded update is unexpectedly small.");
            }
            if (!IsPortableExecutable(temporaryPath))
            {
                throw new InvalidDataException("The downloaded update is not a valid Windows executable.");
            }

            var actualHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            _logger.Info($"Verified GitHub update v{versionText} staged at {finalPath}.");
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public void LaunchInstaller(string stagedExecutable)
    {
        if (string.IsNullOrWhiteSpace(stagedExecutable) || !File.Exists(stagedExecutable))
            throw new FileNotFoundException("The staged update file no longer exists.", stagedExecutable);

        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
            throw new InvalidOperationException("The current executable path could not be determined.");
        if (string.Equals(Path.GetFileName(currentExecutable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self-update is only available from the published Roblox Market Helper Lite build.");

        var currentDirectory = Path.GetDirectoryName(currentExecutable) ?? throw new InvalidOperationException("The executable directory could not be determined.");
        VerifyDirectoryWritable(currentDirectory);

        var updateDirectory = Path.Combine(_dataDirectory, "updates");
        Directory.CreateDirectory(updateDirectory);
        var scriptPath = Path.Combine(updateDirectory, $"install-update-{Guid.NewGuid():N}.ps1");
        var source = EscapePowerShellSingleQuoted(Path.GetFullPath(stagedExecutable));
        var destination = EscapePowerShellSingleQuoted(Path.GetFullPath(currentExecutable));
        var scriptBuilder = new StringBuilder();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Stop'");
        scriptBuilder.AppendLine($"$source = '{source}'");
        scriptBuilder.AppendLine($"$destination = '{destination}'");
        scriptBuilder.AppendLine($"$processId = {Environment.ProcessId}");
        scriptBuilder.AppendLine("try {");
        scriptBuilder.AppendLine("    while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }");
        scriptBuilder.AppendLine("    $copied = $false");
        scriptBuilder.AppendLine("    for ($i = 0; $i -lt 30 -and -not $copied; $i++) {");
        scriptBuilder.AppendLine("        try {");
        scriptBuilder.AppendLine("            Copy-Item -LiteralPath $source -Destination $destination -Force");
        scriptBuilder.AppendLine("            $copied = $true");
        scriptBuilder.AppendLine("        } catch {");
        scriptBuilder.AppendLine("            Start-Sleep -Seconds 1");
        scriptBuilder.AppendLine("        }");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("    if (-not $copied) { exit 21 }");
        scriptBuilder.AppendLine("    Start-Process -FilePath $destination -ArgumentList '--updated'");
        scriptBuilder.AppendLine("    Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue");
        scriptBuilder.AppendLine("} finally {");
        scriptBuilder.AppendLine("    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        scriptBuilder.AppendLine("}");
        File.WriteAllText(scriptPath, scriptBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = currentDirectory
        });
        if (process is null) throw new InvalidOperationException("The update installer could not be started.");
        _logger.Info($"Update installer launched for {currentExecutable}.");
    }

    internal static bool TryParseVersionTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var normalized = tag.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var suffix = normalized.IndexOf('-');
        if (suffix >= 0) normalized = normalized[..suffix];
        if (!Version.TryParse(normalized, out var parsed) || parsed.Major < 0 || parsed.Minor < 0) return false;
        version = NormalizeVersion(parsed);
        return true;
    }

    internal static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Sha256Regex.Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private async Task DownloadBinaryAssetAsync(GitHubReleaseAsset asset, string destination, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await source.CopyToAsync(target, 131072, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private async Task<string> DownloadTextAssetAsync(GitHubReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 64 * 1024) throw new InvalidDataException("The checksum file is unexpectedly large.");
        return Encoding.UTF8.GetString(bytes);
    }

    private HttpRequestMessage CreateGitHubRequest(HttpMethod method, Uri uri, string accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd($"RobloxMarketHelper/{FormatVersion(CurrentVersion)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(_token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static string? NormalizeRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Trim('/');
        var parts = candidate.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) return null;
        if (parts.Any(part => part.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))) return null;
        return candidate;
    }

    private static string? NormalizeToken(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".rpt-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "ok");
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException("The current EXE folder is not writable, so the app cannot replace itself automatically. Move the EXE to a normal user-writable folder and try again.", ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static string EscapePowerShellSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose() => _httpClient.Dispose();
}
