using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Local verified creator-price timeline. Never imports catalog-only price anchors
/// as verified historical evidence; no network calls, user IDs, or credentials.
/// A backup is preserved and writes occur only when evidence materially changes.
/// </summary>
public sealed class UgcPriceIntegrityLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataPath;
    private readonly string _configPath;
    private readonly AppLogger? _logger;
    private readonly Dictionary<long, List<UgcPriceIntegrityPolicy.PrimaryPriceObservation>> _timeline = new();
    private bool _initialized;
    private bool _dirty;

    public UgcPriceIntegrityLedger(string dataDirectory, AppLogger? logger = null)
    {
        _dataPath = Path.Combine(dataDirectory, "ugc-primary-price-evidence.json");
        _configPath = Path.Combine(dataDirectory, "ugc-price-integrity-rules.json");
        _logger = logger;
    }

    public UgcPriceIntegrityPolicy.Configuration Configuration { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        try
        {
            if (File.Exists(_configPath))
            {
                var settings = await SafeJsonStore.LoadWithRecoveryAsync<UgcPriceIntegrityPolicy.Configuration>(
                    _configPath, JsonOptions, _logger, cancellationToken).ConfigureAwait(false);
                if (settings is { IsValid: true })
                    Configuration = settings;
                else _logger?.Info("UGC price-integrity settings were invalid; safe default thresholds apply.");
            }

            var observations = await SafeJsonStore.LoadWithRecoveryAsync<
                List<UgcPriceIntegrityPolicy.PrimaryPriceObservation>>(
                _dataPath, JsonOptions, _logger, cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var point in observations.Where(UgcPriceIntegrityPolicy.IsVerifiedOriginal)
                .OrderBy(x => x.ObservedAtUtc))
                Observe(point, markDirty: false);
            _initialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.Error($"Could not load UGC price-integrity evidence: {ex.Message}");
            _initialized = true;
        }
        _dirty = false;
    }

    public IReadOnlyList<UgcPriceIntegrityPolicy.PrimaryPriceObservation> History(long assetId) =>
        _timeline.TryGetValue(assetId, out var data) ? data.ToArray() : [];

    public void Observe(UgcPriceIntegrityPolicy.PrimaryPriceObservation observation) =>
        Observe(observation, markDirty: true);

    private void Observe(UgcPriceIntegrityPolicy.PrimaryPriceObservation point, bool markDirty)
    {
        if (!UgcPriceIntegrityPolicy.IsVerifiedOriginal(point)) return;
        if (!_timeline.TryGetValue(point.AssetId, out var records))
            _timeline[point.AssetId] = records = [];

        if (records.Count > 0)
        {
            var latest = records[^1];
            // Avoid manufacturing new historical price points on every 45s refresh.
            // A repricing always gets a new point regardless of elapsed time.
            if (latest.PrimaryPrice == point.PrimaryPrice &&
                point.ObservedAtUtc - latest.ObservedAtUtc < TimeSpan.FromHours(24))
                return;
            if (point.ObservedAtUtc < latest.ObservedAtUtc) return;
        }

        records.Add(point);
        var cutoff = point.ObservedAtUtc.AddDays(-Configuration.MaxHistoryAgeDays);
        records.RemoveAll(x => x.ObservedAtUtc < cutoff);
        if (records.Count > 36) records.RemoveRange(0, records.Count - 36);
        if (markDirty) _dirty = true;
    }

    public async Task SaveIfChangedAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Configuration.MaxHistoryAgeDays);
            var all = _timeline.Values.SelectMany(x => x)
                .Where(x => x.ObservedAtUtc >= cutoff)
                .OrderByDescending(x => x.ObservedAtUtc)
                .Take(5000)
                .OrderBy(x => x.ObservedAtUtc)
                .ToArray();

            await SafeJsonStore.SaveWithBackupAsync(_dataPath, all, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _dirty = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger?.Error($"Could not save UGC verified price history: {ex.Message}"); }
    }
}
