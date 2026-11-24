namespace RobloxPriceTracker.Gui;

/// <summary>
/// Coalesces UGC Hunter refreshes into one asynchronous scan. Callers await the
/// completed result instead of receiving a stale snapshot while a detached scan runs.
/// The last successful snapshot is persisted for recovery, not treated as live stock.
/// </summary>
public sealed class UgcHunterServiceFacade : IDisposable
{
    private static readonly TimeSpan MinimumLiveRefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumDiskSnapshotAge = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        IgnoreReadOnlyProperties = true
    };

    private readonly UgcHunterService _inner;
    private readonly AppLogger _logger;
    private readonly string _snapshotPath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly object _stateGate = new();

    private UgcHunterMarketSnapshot? _cachedSnapshot;
    private Task<UgcHunterMarketSnapshot>? _refreshTask;
    private DateTimeOffset _lastLiveRefreshStartedUtc = DateTimeOffset.MinValue;
    private bool _initialized;
    private bool _disposed;

    public UgcHunterServiceFacade(UgcHunterService inner, AppLogger logger, string dataDirectory)
    {
        _inner = inner;
        _logger = logger;
        _snapshotPath = Path.Combine(dataDirectory, "ugc-hunter-snapshot.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _inner.InitializeAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var persisted = await SafeJsonStore.LoadWithRecoveryAsync<UgcHunterMarketSnapshot>(
                    _snapshotPath,
                    SnapshotJsonOptions,
                    _logger,
                    cancellationToken).ConfigureAwait(false);

                if (persisted is not null)
                {
                    var age = DateTimeOffset.UtcNow - persisted.ObservedAtUtc;
                    if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                    if (age <= MaximumDiskSnapshotAge && SnapshotMatchesEntryPricePolicy(persisted))
                    {
                        _cachedSnapshot = persisted;
                        _logger.Info($"UGC Hunter restored cached snapshot with {persisted.Items.Count} item(s), age {age.TotalMinutes:0.#}m.");
                    }
                    else if (age > MaximumDiskSnapshotAge)
                    {
                        _logger.Info($"UGC Hunter ignored cached snapshot older than {MaximumDiskSnapshotAge.TotalHours:0}h.");
                    }
                    else
                    {
                        _logger.Info(
                            "UGC Hunter ignored an old or invalid snapshot; " +
                            "a fresh price-integrity evaluation is required.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"UGC Hunter snapshot cache could not be loaded: {ex.Message}");
            }

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<UgcHunterMarketSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        Task<UgcHunterMarketSnapshot> refresh;
        lock (_stateGate)
        {
            if (_refreshTask is null ||
                (_refreshTask.IsCompleted &&
                 DateTimeOffset.UtcNow - _lastLiveRefreshStartedUtc >= MinimumLiveRefreshInterval))
            {
                _lastLiveRefreshStartedUtc = DateTimeOffset.UtcNow;
                // One caller cancelling its wait must not cancel the scan shared
                // by another view. Errors flow to every awaiting caller.
                _refreshTask = RefreshLiveAndCacheAsync(CancellationToken.None);
            }
            refresh = _refreshTask;
        }
        return await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UgcHunterMarketSnapshot> RefreshLiveAndCacheAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _inner.RefreshAsync(cancellationToken).ConfigureAwait(false);

        lock (_stateGate)
        {
            // A partial empty response is not a successful empty market. Keep
            // the recovery file, but return the actual result to the active UI.
            if (snapshot.IsPartial && snapshot.Items.Count == 0)
            {
                _logger.Info($"UGC stock verification incomplete; retained recovery snapshot ({_cachedSnapshot?.Items.Count ?? 0} previous items).");
                return snapshot;
            }
            _cachedSnapshot = snapshot;
        }

        try
        {
            await SafeJsonStore.SaveWithBackupAsync(
                _snapshotPath,
                snapshot,
                SnapshotJsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"UGC Hunter snapshot cache could not be saved: {ex.Message}");
        }

        return snapshot;
    }

    private static bool SnapshotMatchesEntryPricePolicy(UgcHunterMarketSnapshot snapshot) =>
        snapshot.PriceIntegrityPolicyVersion >= 4 &&
        snapshot.Items.All(item =>
            UgcHunterEntryPricePolicy.IsEligiblePrimaryPrice(item.Price) &&
            item.BuyingPriceClassification is
                (UgcCategoryBuyingPricePolicy.Good or UgcCategoryBuyingPricePolicy.Above or UgcCategoryBuyingPricePolicy.Insufficient) &&
            item.PriceIntegrityClassification is
                (UgcPriceIntegrityPolicy.Passed or UgcPriceIntegrityPolicy.Insufficient or UgcPriceIntegrityPolicy.Excluded));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UgcHunterServiceFacade));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initializeGate.Dispose();
    }
}
