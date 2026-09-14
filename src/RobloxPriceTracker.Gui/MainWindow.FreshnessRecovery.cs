using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace RobloxPriceTracker.Gui;

/// <summary>
/// Keeps tracker freshness aligned with the real polling/backoff state and provides a
/// lightweight watchdog for the specific failure mode where every tracked quote ages out
/// together even though monitoring is supposed to be active.
/// </summary>
internal static class FreshnessRecoveryBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(window.InitializeFreshnessRecovery));
    }
}

public partial class MainWindow
{
    private DispatcherTimer? _freshnessRecoveryTimer;
    private bool _freshnessRecoveryInitialized;
    private bool _freshnessRecoveryRunning;
    private DateTimeOffset _lastFreshnessRecoveryAttemptUtc = DateTimeOffset.MinValue;

    internal void InitializeFreshnessRecovery()
    {
        if (_freshnessRecoveryInitialized) return;
        _freshnessRecoveryInitialized = true;

        _freshnessRecoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _freshnessRecoveryTimer.Tick += async (_, _) => await RefreshFreshnessAndRecoverAsync();
        _freshnessRecoveryTimer.Start();

        Closed += (_, _) => _freshnessRecoveryTimer?.Stop();
        _ = RefreshFreshnessAndRecoverAsync();
    }

    private TimeSpan EffectiveStaleAfter()
    {
        // Three normal polls was too aggressive whenever Roblox asked the app to back off.
        // Keep a truthful stale state, but allow enough room for ordinary throttling and a
        // temporary provider hiccup before painting the whole board amber.
        var normalPoll = TimeSpan.FromSeconds(Math.Max(10, _services.Settings.NormalPollSeconds));
        var baseGrace = TimeSpan.FromSeconds(Math.Max(
            TimeSpan.FromMinutes(10).TotalSeconds,
            normalPoll.TotalSeconds * 5d));

        var backoff = _services.RateGovernor.RemainingDelay;
        if (backoff <= TimeSpan.Zero) return baseGrace;

        var backoffGrace = backoff + TimeSpan.FromSeconds(normalPoll.TotalSeconds * 2d);
        return backoffGrace > baseGrace ? backoffGrace : baseGrace;
    }

    private async Task RefreshFreshnessAndRecoverAsync()
    {
        if (_freshnessRecoveryRunning || !_initialized || _watchlistRows.Count == 0) return;

        _freshnessRecoveryRunning = true;
        try
        {
            var staleAfter = EffectiveStaleAfter();

            // Refresh the presentation using a threshold that accounts for provider backoff.
            // This changes no persisted market values and does not touch forecast/history data.
            foreach (var row in _watchlistRows)
            {
                if (row.Snapshot is not null)
                    row.Update(row.Snapshot, staleAfter);
            }

            _watchlistView?.Refresh();
            UpdateDashboardWatchlist();

            // A normal pause clears both fields in StopMonitoringAsync. If the guarded monitor
            // loop exits unexpectedly, however, its finally block clears only _monitoringCts and
            // leaves the completed task reference behind. That distinction lets the watchdog
            // restart a crashed loop without overriding an intentional user pause.
            if (_startupDelayCts is not null) return;
            var monitorLoopEndedUnexpectedly = _monitoringCts is null && _monitoringTask is { IsCompleted: true };
            var allStale = _watchlistRows.Count > 0 && _watchlistRows.All(x => x.IsStale);
            if (!allStale && !monitorLoopEndedUnexpectedly) return;

            // Respect Roblox's Retry-After / governor state. Once requests are legal again,
            // recover immediately rather than repeatedly restarting or forcing a blocked poll.
            if (!_services.RateGovernor.CanRequest)
            {
                await RefreshProviderHealthAsync();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var minimumRetrySpacing = TimeSpan.FromSeconds(Math.Max(30, _services.Settings.NormalPollSeconds));
            if (now - _lastFreshnessRecoveryAttemptUtc < minimumRetrySpacing) return;

            _lastFreshnessRecoveryAttemptUtc = now;
            if (monitorLoopEndedUnexpectedly)
            {
                _services.Logger.Info("Freshness watchdog: monitoring loop ended unexpectedly; restarting background monitoring.");
                StartMonitoring();
                await RefreshProviderHealthAsync();
                return;
            }

            // If monitoring is intentionally paused, all-stale data should remain visible as stale
            // rather than silently turning monitoring back on.
            if (_monitoringCts is null) return;

            _services.Logger.Info("Freshness watchdog: all tracked quotes are stale; requesting an immediate recovery poll.");
            await RunCheckAsync(userInitiated: false, CancellationToken.None);
            await RefreshProviderHealthAsync();
        }
        catch (Exception ex)
        {
            // Freshness recovery is defensive only; it must never take down the normal monitor.
            _services.Logger.Error($"Freshness recovery failed: {ex}");
        }
        finally
        {
            _freshnessRecoveryRunning = false;
        }
    }
}
