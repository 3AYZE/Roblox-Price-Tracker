using System.Text.Json;
using System.Text.Json.Serialization;
using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Infrastructure;

public sealed record PendingNotification(long Id, string Title, string Body, int Attempts);

/// <summary>
/// Zero-dependency persistence used by the backend prototype.
/// Writes are serialized, flushed to a temporary file, then moved over the live file.
/// A backup is retained so an interrupted/corrupt write can be recovered on next start.
/// </summary>
public sealed class JsonFileRepository
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private Store _store = Store.Create();

    public JsonFileRepository(string path)
    {
        _path = path;
        _backupPath = path + ".bak";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store = await LoadBestAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (_store.SchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"State schema {_store.SchemaVersion} is newer than this application supports ({CurrentSchemaVersion}).");
            }

            if (_store.SchemaVersion <= 0)
            {
                _store.SchemaVersion = CurrentSchemaVersion;
            }

            NormalizeCounters(_store);
            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddOrUpdateTrackedAssetAsync(
        MarketObservation observation,
        long targetPrice,
        int rearmBasisPoints = 100,
        CancellationToken cancellationToken = default)
    {
        if (!observation.IsResaleCapable || observation.Status is MarketStatus.Unsupported or MarketStatus.MissingFromResponse)
        {
            throw new InvalidOperationException("The item is not confirmed as a supported resellable asset.");
        }
        PriceValidation.ThrowIfInvalidTarget(targetPrice);

        await MutateAsync(store =>
        {
            var key = observation.ItemKey.ToString();
            var name = string.IsNullOrWhiteSpace(observation.Name) ? key : observation.Name!;
            if (store.Items.TryGetValue(key, out var existing))
            {
                store.Items[key] = existing with { Name = name, Enabled = true };
            }
            else
            {
                store.Items[key] = new TrackedItem(observation.ItemKey, name, true, observation.ObservedAtUtc);
            }

            if (!store.MarketStates.ContainsKey(key))
            {
                store.MarketStates[key] = MarketState.Empty(observation.ItemKey);
            }

            UpsertRule(store, observation.ItemKey, AlertRuleType.TargetPrice, targetPrice, AlertState.Armed, rearmBasisPoints, true, null, resetTrigger: true);
            UpsertRule(store, observation.ItemKey, AlertRuleType.NewTrackedLow, null, AlertState.Armed, 0, true, null, resetTrigger: false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task DisableItemAsync(ItemKey key, CancellationToken cancellationToken = default) =>
        MutateAsync(store =>
        {
            var itemKey = key.ToString();
            if (store.Items.TryGetValue(itemKey, out var item))
            {
                store.Items[itemKey] = item with { Enabled = false };
            }

            for (var i = 0; i < store.Rules.Count; i++)
            {
                if (store.Rules[i].ItemKey == key)
                {
                    store.Rules[i] = store.Rules[i] with { Enabled = false, State = AlertState.Disabled };
                }
            }
        }, cancellationToken);

    public async Task<bool> CommitDecisionAsync(ObservationDecision decision, long pollSequence, CancellationToken cancellationToken = default)
    {
        if (decision.IgnoredAsOldObservation)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = decision.UpdatedSnapshot;
            var key = snapshot.Item.ItemKey.ToString();
            if (!_store.MarketStates.TryGetValue(key, out var current) || current.LastPollSequence != decision.ExpectedPreviousPollSequence)
            {
                return false;
            }

            _store.MarketStates[key] = snapshot.Market with { LastPollSequence = pollSequence };

            if (decision.ShouldWriteHistory && !_store.PriceHistory.Any(x => x.ItemKey == snapshot.Item.ItemKey && x.PollSequence == pollSequence))
            {
                _store.PriceHistory.Add(new PriceHistoryEntry(
                    snapshot.Item.ItemKey,
                    snapshot.Market.LastSuccessAtUtc ?? DateTimeOffset.UtcNow,
                    snapshot.Market.CurrentLowestPrice,
                    snapshot.Market.ObservedStatus,
                    pollSequence));
            }

            foreach (var mutation in decision.RuleMutations)
            {
                var index = _store.Rules.FindIndex(x => x.Id == mutation.Rule.Id);
                if (index >= 0)
                {
                    _store.Rules[index] = mutation.Rule;
                }
            }

            foreach (var alert in decision.Alerts)
            {
                var existing = _store.AlertEvents.FirstOrDefault(x =>
                    x.RuleId == alert.RuleId && x.EventType == alert.EventType && x.PollSequence == pollSequence);

                AlertEventEntry eventEntry;
                if (existing is not null)
                {
                    eventEntry = existing;
                }
                else
                {
                    eventEntry = new AlertEventEntry(
                        _store.NextAlertEventId++, alert.RuleId, snapshot.Item.ItemKey, alert.EventType,
                        alert.OldPrice, alert.NewPrice, alert.CreatedAtUtc, pollSequence);
                    _store.AlertEvents.Add(eventEntry);
                }

                if (alert.QueueNotification && !_store.Notifications.Any(x => x.AlertEventId == eventEntry.Id))
                {
                    _store.Notifications.Add(new NotificationEntry(
                        _store.NextNotificationId++, eventEntry.Id, alert.Title, alert.Body,
                        NotificationState.Pending, alert.CreatedAtUtc, null, 0));
                }
            }

            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TrackerItemSnapshot>> LoadEnabledSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.Items.Values
                .Where(x => x.Enabled)
                .OrderBy(x => x.AddedAtUtc)
                .Select(item =>
                {
                    var key = item.ItemKey.ToString();
                    var market = _store.MarketStates.TryGetValue(key, out var state) ? state : MarketState.Empty(item.ItemKey);
                    var rules = _store.Rules.Where(x => x.ItemKey == item.ItemKey && x.Enabled).OrderBy(x => x.Id).ToArray();
                    return new TrackerItemSnapshot(item, market, rules);
                })
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrackerItemSnapshot?> LoadSnapshotAsync(ItemKey key, CancellationToken cancellationToken = default)
    {
        var all = await LoadEnabledSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(x => x.Item.ItemKey == key);
    }

    public async Task<long> GetMaxPollSequenceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.MarketStates.Count == 0 ? 0 : _store.MarketStates.Values.Max(x => x.LastPollSequence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingNotification>> GetPendingNotificationsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.Notifications
                .Where(x => x.State == NotificationState.Pending)
                .OrderBy(x => x.Id)
                .Take(Math.Max(0, limit))
                .Select(x => new PendingNotification(x.Id, x.Title, x.Body, x.Attempts))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkNotificationDeliveredAsync(long id, DateTimeOffset deliveredAtUtc, CancellationToken cancellationToken = default) =>
        MutateAsync(store =>
        {
            var index = store.Notifications.FindIndex(x => x.Id == id);
            if (index >= 0)
            {
                store.Notifications[index] = store.Notifications[index] with
                {
                    State = NotificationState.Delivered,
                    DeliveredAtUtc = deliveredAtUtc
                };
            }
        }, cancellationToken);

    public Task MarkNotificationAttemptFailedAsync(long id, CancellationToken cancellationToken = default) =>
        MutateAsync(store =>
        {
            var index = store.Notifications.FindIndex(x => x.Id == id);
            if (index >= 0)
            {
                var nextAttempts = store.Notifications[index].Attempts + 1;
                store.Notifications[index] = store.Notifications[index] with
                {
                    Attempts = nextAttempts,
                    State = nextAttempts >= 5 ? NotificationState.Failed : NotificationState.Pending
                };
            }
        }, cancellationToken);

    public Task UpdateProviderHealthAsync(
        string provider,
        string state,
        int failureCount,
        string? message,
        DateTimeOffset? backoffUntilUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(store =>
        {
            store.ProviderHealth[provider] = new ProviderHealthEntry(
                provider, state, failureCount, message, backoffUntilUtc, DateTimeOffset.UtcNow);
        }, cancellationToken);

    public async Task<DateTimeOffset?> GetProviderBackoffUntilAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.ProviderHealth.TryGetValue(provider, out var health) ? health.BackoffUntilUtc : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AlertEventEntry>> GetAlertEventsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.AlertEvents
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(Math.Max(0, limit))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PriceHistoryEntry>> GetPriceHistoryAsync(ItemKey? itemKey = null, int limit = 1000, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<PriceHistoryEntry> query = _store.PriceHistory;
            if (itemKey is { } key)
            {
                query = query.Where(x => x.ItemKey == key);
            }

            return query
                .OrderByDescending(x => x.ObservedAtUtc)
                .Take(Math.Max(0, limit))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProviderHealthEntry?> GetProviderHealthAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _store.ProviderHealth.TryGetValue(provider, out var health) ? health : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Action<Store> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshUnsafeAsync(cancellationToken).ConfigureAwait(false);
            mutation(_store);
            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshUnsafeAsync(CancellationToken cancellationToken)
    {
        _store = await LoadBestAvailableAsync(cancellationToken).ConfigureAwait(false);
        NormalizeCounters(_store);
    }

    private async Task<Store> LoadBestAvailableAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path))
        {
            try
            {
                return await ReadStoreAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (File.Exists(_backupPath))
            {
                // Preserve the bad primary for diagnostics and prevent SaveUnsafeAsync from
                // copying it over the known-good backup during recovery.
                try
                {
                    var corruptPath = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
                    File.Move(_path, corruptPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(_path); } catch { }
                }
                return await ReadStoreAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            }
        }

        if (File.Exists(_backupPath))
        {
            return await ReadStoreAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        }

        return Store.Create();
    }

    private async Task<Store> ReadStoreAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<Store>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"State file '{path}' is empty or invalid.");
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, _store, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {
            File.Copy(_path, _backupPath, overwrite: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static void UpsertRule(
        Store store,
        ItemKey key,
        AlertRuleType type,
        long? threshold,
        AlertState state,
        int rearmBasisPoints,
        bool enabled,
        DateTimeOffset? lastTriggered,
        bool resetTrigger)
    {
        var index = store.Rules.FindIndex(x => x.ItemKey == key && x.RuleType == type);
        if (index >= 0)
        {
            var existing = store.Rules[index];
            store.Rules[index] = existing with
            {
                Threshold = threshold,
                State = state,
                RearmBasisPoints = rearmBasisPoints,
                Enabled = enabled,
                LastTriggeredAtUtc = resetTrigger ? null : existing.LastTriggeredAtUtc
            };
            return;
        }

        store.Rules.Add(new AlertRule(store.NextRuleId++, key, type, threshold, state, rearmBasisPoints, enabled, lastTriggered));
    }

    private static void NormalizeCounters(Store store)
    {
        store.Items ??= new Dictionary<string, TrackedItem>(StringComparer.OrdinalIgnoreCase);
        store.MarketStates ??= new Dictionary<string, MarketState>(StringComparer.OrdinalIgnoreCase);
        store.Rules ??= new List<AlertRule>();
        store.PriceHistory ??= new List<PriceHistoryEntry>();
        store.AlertEvents ??= new List<AlertEventEntry>();
        store.Notifications ??= new List<NotificationEntry>();
        store.ProviderHealth ??= new Dictionary<string, ProviderHealthEntry>(StringComparer.OrdinalIgnoreCase);
        store.NextRuleId = Math.Max(store.NextRuleId, store.Rules.Count == 0 ? 1 : store.Rules.Max(x => x.Id) + 1);
        store.NextAlertEventId = Math.Max(store.NextAlertEventId, store.AlertEvents.Count == 0 ? 1 : store.AlertEvents.Max(x => x.Id) + 1);
        store.NextNotificationId = Math.Max(store.NextNotificationId, store.Notifications.Count == 0 ? 1 : store.Notifications.Max(x => x.Id) + 1);
    }

    public sealed class Store
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long NextRuleId { get; set; } = 1;
        public long NextAlertEventId { get; set; } = 1;
        public long NextNotificationId { get; set; } = 1;
        public Dictionary<string, TrackedItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MarketState> MarketStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AlertRule> Rules { get; set; } = new();
        public List<PriceHistoryEntry> PriceHistory { get; set; } = new();
        public List<AlertEventEntry> AlertEvents { get; set; } = new();
        public List<NotificationEntry> Notifications { get; set; } = new();
        public Dictionary<string, ProviderHealthEntry> ProviderHealth { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static Store Create() => new();
    }

    public sealed record PriceHistoryEntry(ItemKey ItemKey, DateTimeOffset ObservedAtUtc, long? Price, MarketStatus Status, long PollSequence);
    public sealed record AlertEventEntry(long Id, long RuleId, ItemKey ItemKey, AlertEventType EventType, long? OldPrice, long NewPrice, DateTimeOffset CreatedAtUtc, long PollSequence);
    public enum NotificationState { Pending, Delivered, Failed }
    public sealed record NotificationEntry(long Id, long AlertEventId, string Title, string Body, NotificationState State, DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc, int Attempts);
    public sealed record ProviderHealthEntry(string Provider, string State, int FailureCount, string? Message, DateTimeOffset? BackoffUntilUtc, DateTimeOffset UpdatedAtUtc);
}
