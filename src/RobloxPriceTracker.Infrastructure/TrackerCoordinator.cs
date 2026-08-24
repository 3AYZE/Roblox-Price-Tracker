using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Infrastructure;

public sealed record CheckSummary(
    int RequestedItems,
    int ProcessedItems,
    int MissingItems,
    int AlertsCreated,
    bool SkippedForBackoff,
    ProviderFailure? Failure,
    long PollSequence);

public sealed class TrackerCoordinator
{
    private readonly JsonFileRepository _repository;
    private readonly IMarketProvider _provider;
    private readonly AlertEngine _alertEngine;
    private readonly RateLimitGovernor _governor;
    private readonly TimeProvider _timeProvider;
    private readonly AppLogger _logger;
    private readonly object _inFlightGate = new();
    private Task<CheckSummary>? _inFlight;
    private long _pollSequence;

    public TrackerCoordinator(
        JsonFileRepository repository,
        IMarketProvider provider,
        AlertEngine alertEngine,
        RateLimitGovernor governor,
        long initialPollSequence,
        AppLogger logger,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _provider = provider;
        _alertEngine = alertEngine;
        _governor = governor;
        _pollSequence = initialPollSequence;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CheckSummary> CheckOnceAsync(CancellationToken cancellationToken)
    {
        lock (_inFlightGate)
        {
            if (_inFlight is { IsCompleted: false })
            {
                return _inFlight;
            }

            _inFlight = CheckCoreAsync(cancellationToken);
            return _inFlight;
        }
    }

    private async Task<long> GetNextPollSequenceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var persistedMax = await _repository.GetMaxPollSequenceAsync(cancellationToken).ConfigureAwait(false);
            var observed = Volatile.Read(ref _pollSequence);
            var baseline = Math.Max(persistedMax, observed);
            var next = checked(baseline + 1);
            if (Interlocked.CompareExchange(ref _pollSequence, next, observed) == observed)
            {
                return next;
            }
        }
    }

    private async Task<CheckSummary> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (!_governor.CanRequest)
        {
            return new CheckSummary(0, 0, 0, 0, true, null, _pollSequence);
        }

        var snapshots = await _repository.LoadEnabledSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshots.Count == 0)
        {
            return new CheckSummary(0, 0, 0, 0, false, null, _pollSequence);
        }

        var sequence = await GetNextPollSequenceAsync(cancellationToken).ConfigureAwait(false);
        var keys = snapshots.Select(x => x.Item.ItemKey).ToArray();
        _logger.Info($"Poll {sequence}: checking {keys.Length} item(s) through {_provider.Name}.");

        var fetch = await _provider.FetchAsync(keys, cancellationToken).ConfigureAwait(false);
        var processed = 0;
        var missing = 0;
        var alerts = 0;
        var snapshotByKey = snapshots.ToDictionary(x => x.Item.ItemKey);

        foreach (var key in keys)
        {
            if (!fetch.Observations.TryGetValue(key, out var observation))
            {
                missing++;
                continue;
            }

            if (observation.Status == MarketStatus.MissingFromResponse)
            {
                missing++;
                continue;
            }

            var snapshot = snapshotByKey[key];
            var now = _timeProvider.GetUtcNow();
            var decision = _alertEngine.Evaluate(snapshot, observation, sequence, now);
            var committed = await _repository.CommitDecisionAsync(decision, sequence, cancellationToken).ConfigureAwait(false);
            if (!committed)
            {
                _logger.Warn($"Poll {sequence}: skipped stale concurrent result for {snapshot.Item.Name}.");
                continue;
            }

            processed++;
            alerts += decision.Alerts.Count;

            if (decision.Alerts.Count > 0)
            {
                _logger.Info($"Poll {sequence}: created {decision.Alerts.Count} alert event(s) for {snapshot.Item.Name}.");
            }
        }

        if (fetch.Failure is null)
        {
            _governor.OnSuccess();
            await _repository.UpdateProviderHealthAsync(_provider.Name, "Healthy", _governor.ConsecutiveFailures, null, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _governor.OnFailure(fetch.Failure);
            var state = fetch.Failure.Kind == ProviderFailureKind.RateLimited ? "RateLimited" : "Degraded";
            var backoffUntilUtc = _timeProvider.GetUtcNow() + _governor.RemainingDelay;
            await _repository.UpdateProviderHealthAsync(_provider.Name, state, _governor.ConsecutiveFailures, fetch.Failure.Message, backoffUntilUtc, cancellationToken).ConfigureAwait(false);
            _logger.Warn($"Poll {sequence}: {fetch.Failure.Message} Backoff: {_governor.RemainingDelay.TotalSeconds:N0}s.");
        }

        return new CheckSummary(keys.Length, processed, missing, alerts, false, fetch.Failure, sequence);
    }
}
