namespace RobloxPriceTracker.Gui;

/// <summary>
/// Keeps UGC Hunter responsive by serving the last successful snapshot immediately while a
/// single live refresh runs in the background. The snapshot is persisted with SafeJsonStore so
/// reopening the app does not require the user to wait on a full Roblox discovery/enrichment pass
/// before seeing the Hunter board.
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
    private Task? _backgroundRefreshTask;
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
                    if (age <= MaximumDiskSnapshotAge)
                    {
                        _cachedSnapshot = persisted;
                        _logger.Info($"UGC Hunter restored cached snapshot with {persisted.Items.Count} item(s), age {age.TotalMinutes:0.#}m.");
                    }
                    else
                    {
                        _logger.Info($"UGC Hunter ignored cached snapshot older than {MaximumDiskSnapshotAge.TotalHours:0}h.");
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

        UgcHunterMarketSnapshot? cached;
        lock (_stateGate)
        {
            cached = _cachedSnapshot;
            if (cached is not null)
                StartBackgroundRefreshLocked();
        }

        if (cached is not null)
            return cached;

        // First-ever run has no safe snapshot to show. Do one foreground refresh, persist it, and
        // every later visit becomes stale-while-revalidate instead of blocking the Hunter UI.
        return await RefreshLiveAndCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartBackgroundRefreshLocked()
    {
        if (_disposed) return;
        if (_backgroundRefreshTask is { IsCompleted: false }) return;
        if (DateTimeOffset.UtcNow - _lastLiveRefreshStartedUtc < MinimumLiveRefreshInterval) return;

        _lastLiveRefreshStartedUtc = DateTimeOffset.UtcNow;
        _backgroundRefreshTask = RefreshInBackgroundAsync();
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshLiveAndCacheAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"UGC Hunter background refresh failed; cached snapshot remains active: {ex}");
        }
        finally
        {
            lock (_stateGate)
            {
                _backgroundRefreshTask = null;
            }
        }
    }

    private async Task<UgcHunterMarketSnapshot> RefreshLiveAndCacheAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _inner.RefreshAsync(cancellationToken).ConfigureAwait(false);

        lock (_stateGate)
        {
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
