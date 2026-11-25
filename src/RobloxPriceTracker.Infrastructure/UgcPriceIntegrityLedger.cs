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
    // Keep compact verified anchors per identity, independent of rolling retention.
    private readonly Dictionary<(long Asset, long Creator, int Type),
        (UgcPriceIntegrityPolicy.PrimaryPriceObservation First, UgcPriceIntegrityPolicy.PrimaryPriceObservation Low)> _anchors = new();
    private readonly object _timelineGate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
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
        lock (_timelineGate) _dirty = false;
    }

    public IReadOnlyList<UgcPriceIntegrityPolicy.PrimaryPriceObservation> History(long assetId)
    {
        lock (_timelineGate)
        {
            var rolling = _timeline.TryGetValue(assetId, out var data)
                ? data : Enumerable.Empty<UgcPriceIntegrityPolicy.PrimaryPriceObservation>();
            var anchors = _anchors.Where(x => x.Key.Asset == assetId)
                .SelectMany(x => new[] { x.Value.First, x.Value.Low });
            return rolling.Concat(anchors).Distinct().OrderBy(x => x.ObservedAtUtc).ToArray();
        }
    }

    public void Observe(UgcPriceIntegrityPolicy.PrimaryPriceObservation observation) =>
        Observe(observation, markDirty: true);

    private void Observe(UgcPriceIntegrityPolicy.PrimaryPriceObservation point, bool markDirty)
    {
        if (!UgcPriceIntegrityPolicy.IsVerifiedOriginal(point) || point.ObservedAtUtc > DateTimeOffset.UtcNow) return;
        lock (_timelineGate)
        {
            var key = (point.AssetId, point.CreatorId, point.AssetType);
            if (!_anchors.TryGetValue(key, out var anchor))
            {
                _anchors[key] = (point, point);
                if (markDirty) _dirty = true;
            }
            else
            {
                var first = point.ObservedAtUtc < anchor.First.ObservedAtUtc ? point : anchor.First;
                var low = point.PrimaryPrice < anchor.Low.PrimaryPrice ||
                    (point.PrimaryPrice == anchor.Low.PrimaryPrice && point.ObservedAtUtc < anchor.Low.ObservedAtUtc)
                    ? point : anchor.Low;
                if (first != anchor.First || low != anchor.Low)
                {
                    _anchors[key] = (first, low);
                    if (markDirty) _dirty = true;
                }
            }
            if (!_timeline.TryGetValue(point.AssetId, out var records))
                _timeline[point.AssetId] = records = [];

            if (records.Count > 0)
            {
                var latest = records[^1];
                // A stable creator price is recorded at most once per day.
                // A genuine repricing always creates a new timestamped point.
                if (latest.PrimaryPrice == point.PrimaryPrice &&
                    point.ObservedAtUtc - latest.ObservedAtUtc <= TimeSpan.FromHours(24))
                    return;
                if (point.ObservedAtUtc < latest.ObservedAtUtc) return;
            }

            records.Add(point);
            var cutoff = point.ObservedAtUtc.AddDays(-Configuration.MaxHistoryAgeDays);
            records.RemoveAll(x => x.ObservedAtUtc < cutoff);
            if (records.Count > 36) records.RemoveRange(0, records.Count - 36);
            if (markDirty) _dirty = true;
        }
    }

    public async Task SaveIfChangedAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UgcPriceIntegrityPolicy.PrimaryPriceObservation[] snapshot;
            lock (_timelineGate)
            {
                if (!_dirty) return;
                var cutoff = DateTimeOffset.UtcNow.AddDays(-Configuration.MaxHistoryAgeDays);
                snapshot = _timeline.Values.SelectMany(x => x)
                    .Where(x => x.ObservedAtUtc >= cutoff)
                    .OrderByDescending(x => x.ObservedAtUtc)
                    .Take(5000)
                    .Concat(_anchors.Values.SelectMany(x => new[] { x.First, x.Low }))
                    .Distinct()
                    .OrderBy(x => x.ObservedAtUtc)
                    .ToArray();
                _dirty = false; // A concurrent new observation sets this back to true.
            }

            try
            {
                await SafeJsonStore.SaveWithBackupAsync(_dataPath, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_timelineGate) _dirty = true;
                throw;
            }
            catch (Exception ex)
            {
                lock (_timelineGate) _dirty = true;
                _logger?.Error($"Could not save UGC verified price history: {ex.Message}");
            }
        }
        finally { _saveGate.Release(); }
    }
}
