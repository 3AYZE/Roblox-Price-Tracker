using System.Text.Json;
using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Infrastructure;

public sealed record ForecastBacktestStats(
    int EvaluatedForecasts,
    double MeanAbsolutePercentError,
    double AccuracyPercent,
    double RangeCoveragePercent);

public sealed record ForecastHistoryEntry(
    ItemKey ItemKey,
    long SourcePollSequence,
    DateTimeOffset GeneratedAtUtc,
    long PredictedPrice,
    long RangeLow,
    long RangeHigh,
    double ConfidencePercent,
    string ModelVersion,
    long? ActualPrice,
    DateTimeOffset? EvaluatedAtUtc,
    double? AbsolutePercentError,
    bool? ActualInsideRange);

/// <summary>
/// Stores forecast/backtest data separately from tracker-state.json so forecast model
/// evolution never risks the core watchlist/alert state format.
/// </summary>
public sealed class ForecastHistoryStore
{
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private State _state = new();
    private bool _initialized;

    public ForecastHistoryStore(string path)
    {
        _path = path;
        _backupPath = path + ".bak";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _state = await LoadBestAsync(cancellationToken).ConfigureAwait(false);
            _state.Entries ??= new List<ForecastHistoryEntry>();
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(
        ItemKey itemKey,
        long sourcePollSequence,
        PriceForecastResult forecast,
        long? actualCurrentPrice,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!forecast.IsAvailable || forecast.NextPrice is not > 0 || forecast.RangeLow is not > 0 || forecast.RangeHigh is not > 0 || sourcePollSequence <= 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();

            if (actualCurrentPrice is > 0)
            {
                var unresolvedIndex = _state.Entries.FindLastIndex(x =>
                    x.ItemKey == itemKey &&
                    x.SourcePollSequence < sourcePollSequence &&
                    x.ActualPrice is null);
                if (unresolvedIndex >= 0)
                {
                    var prior = _state.Entries[unresolvedIndex];
                    var error = Math.Abs(actualCurrentPrice.Value - prior.PredictedPrice) / (double)Math.Max(1, actualCurrentPrice.Value) * 100d;
                    var inRange = actualCurrentPrice.Value >= prior.RangeLow && actualCurrentPrice.Value <= prior.RangeHigh;
                    _state.Entries[unresolvedIndex] = prior with
                    {
                        ActualPrice = actualCurrentPrice.Value,
                        EvaluatedAtUtc = observedAtUtc,
                        AbsolutePercentError = Math.Round(error, 4),
                        ActualInsideRange = inRange
                    };
                }
            }

            var exists = _state.Entries.Any(x => x.ItemKey == itemKey && x.SourcePollSequence == sourcePollSequence && x.ModelVersion == forecast.ModelVersion);
            if (!exists)
            {
                _state.Entries.Add(new ForecastHistoryEntry(
                    itemKey,
                    sourcePollSequence,
                    DateTimeOffset.UtcNow,
                    forecast.NextPrice.Value,
                    forecast.RangeLow!.Value,
                    forecast.RangeHigh!.Value,
                    forecast.ConfidencePercent,
                    forecast.ModelVersion,
                    null,
                    null,
                    null,
                    null));
            }

            Trim(itemKey);
            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ForecastBacktestStats> GetStatsAsync(ItemKey itemKey, int limit = 50, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var evaluated = _state.Entries
                .Where(x => x.ItemKey == itemKey && x.AbsolutePercentError is not null)
                .OrderByDescending(x => x.EvaluatedAtUtc)
                .Take(Math.Max(1, limit))
                .ToArray();

            if (evaluated.Length == 0)
            {
                return new ForecastBacktestStats(0, 0, 0, 0);
            }

            var mape = evaluated.Average(x => x.AbsolutePercentError!.Value);
            var accuracy = Math.Clamp(100d - mape, 0d, 100d);
            var coverage = evaluated.Count(x => x.ActualInsideRange == true) / (double)evaluated.Length * 100d;
            return new ForecastBacktestStats(
                evaluated.Length,
                Math.Round(mape, 2),
                Math.Round(accuracy, 0),
                Math.Round(coverage, 0));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ForecastHistoryEntry>> GetRecentAsync(ItemKey itemKey, int limit = 100, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            return _state.Entries
                .Where(x => x.ItemKey == itemKey)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .Take(Math.Max(0, limit))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Forecast history store is not initialized.");
    }

    private void Trim(ItemKey itemKey)
    {
        var keep = _state.Entries
            .Where(x => x.ItemKey == itemKey)
            .OrderByDescending(x => x.GeneratedAtUtc)
            .Take(300)
            .ToHashSet();
        _state.Entries.RemoveAll(x => x.ItemKey == itemKey && !keep.Contains(x));

        if (_state.Entries.Count > 10_000)
        {
            _state.Entries = _state.Entries.OrderByDescending(x => x.GeneratedAtUtc).Take(10_000).OrderBy(x => x.GeneratedAtUtc).ToList();
        }
    }

    private async Task<State> LoadBestAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { _path, _backupPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer.DeserializeAsync<State>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false) ?? new State();
            }
            catch
            {
            }
        }
        return new State();
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var temp = _path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, _state, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(_path)) File.Copy(_path, _backupPath, overwrite: true);
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class State
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ForecastHistoryEntry> Entries { get; set; } = new();
    }
}
