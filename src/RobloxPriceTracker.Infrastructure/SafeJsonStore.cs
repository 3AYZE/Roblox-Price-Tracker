using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Small, dependency-free safety layer for user-owned JSON files that do not use JsonFileRepository.
/// It preserves a last-known-good backup, quarantines unreadable primaries, and can snapshot the
/// application data directory before upgrades or manual maintenance.
/// </summary>
public static class SafeJsonStore
{
    public static async Task<T?> LoadWithRecoveryAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var backupPath = path + ".bak";
        var primaryFailed = false;

        if (File.Exists(path))
        {
            try
            {
                return await ReadAsync<T>(path, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                primaryFailed = true;
                logger?.Error($"User data '{Path.GetFileName(path)}' could not be read: {ex.Message}");
                Quarantine(path, logger);
            }
        }

        if (File.Exists(backupPath))
        {
            try
            {
                var recovered = await ReadAsync<T>(backupPath, options, cancellationToken).ConfigureAwait(false);
                if (recovered is not null && (primaryFailed || !File.Exists(path)))
                {
                    try { File.Copy(backupPath, path, overwrite: false); }
                    catch { }
                }
                logger?.Info($"Recovered '{Path.GetFileName(path)}' from its backup.");
                return recovered;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.Error($"Backup for '{Path.GetFileName(path)}' could not be read: {ex.Message}");
            }
        }

        return default;
    }

    public static async Task SaveWithBackupAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new JsonSerializerOptions { WriteIndented = true };
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";

        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         32 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);

        File.Move(tempPath, path, overwrite: true);
    }

    public static IReadOnlyList<string> MigrateLegacyJsonFiles(string legacyDirectory, string destinationDirectory, AppLogger? logger = null)
    {
        var copied = new List<string>();
        if (!Directory.Exists(legacyDirectory)) return copied;
        Directory.CreateDirectory(destinationDirectory);

        foreach (var source in Directory.EnumerateFiles(legacyDirectory, "*.json*", SearchOption.TopDirectoryOnly))
        {
            if (source.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (File.Exists(destination)) continue;
            try
            {
                File.Copy(source, destination, overwrite: false);
                copied.Add(destination);
            }
            catch (Exception ex)
            {
                logger?.Info($"Legacy data migration skipped '{Path.GetFileName(source)}': {ex.Message}");
            }
        }

        return copied;
    }

    public static Task<string?> CreateAutomaticSnapshotAsync(
        string dataDirectory,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var backupRoot = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(backupRoot);
        var newest = Directory.EnumerateDirectories(backupRoot, "auto-*")
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.CreationTimeUtc)
            .FirstOrDefault();

        if (newest is not null && DateTime.UtcNow - newest.CreationTimeUtc < TimeSpan.FromHours(12))
            return Task.FromResult<string?>(null);

        return CreateSnapshotCoreAsync(dataDirectory, "auto", keepAutomatic: 8, logger, cancellationToken);
    }

    public static async Task<string> CreateManualSnapshotAsync(
        string dataDirectory,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return (await CreateSnapshotCoreAsync(dataDirectory, "manual", keepAutomatic: null, logger, cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<string?> CreateSnapshotCoreAsync(
        string dataDirectory,
        string prefix,
        int? keepAutomatic,
        AppLogger? logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dataDirectory);
        var sourceFiles = Directory.EnumerateFiles(dataDirectory, "*.json*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceFiles.Length == 0) return null;

        var backupRoot = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(backupRoot);
        var snapshot = Path.Combine(backupRoot, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var suffix = 1;
        while (Directory.Exists(snapshot))
            snapshot = Path.Combine(backupRoot, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix++}");
        Directory.CreateDirectory(snapshot);

        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(snapshot, Path.GetFileName(source));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (keepAutomatic is { } keep)
        {
            var old = Directory.EnumerateDirectories(backupRoot, "auto-*")
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.CreationTimeUtc)
                .Skip(Math.Max(1, keep))
                .ToArray();
            foreach (var info in old)
            {
                try { info.Delete(recursive: true); }
                catch { }
            }
        }

        logger?.Info($"Created user-data snapshot '{snapshot}'.");
        return snapshot;
    }

    private static async Task<T> ReadAsync<T>(string path, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"'{path}' was empty or did not contain valid data.");
    }

    private static void Quarantine(string path, AppLogger? logger)
    {
        if (!File.Exists(path)) return;
        try
        {
            var quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(path, quarantine, overwrite: false);
            logger?.Info($"Preserved unreadable user data as '{Path.GetFileName(quarantine)}'.");
        }
        catch (Exception ex)
        {
            logger?.Info($"Could not quarantine unreadable '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
