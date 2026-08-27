using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{

    private void UpdateDashboardWatchlist()
    {
        var ordered = _watchlistRows
            .OrderBy(x => x.IsTargetHit ? 0 : x.IsNearTarget ? 1 : x.IsIssue ? 3 : 2)
            .ThenBy(x => x.TargetDistanceSort == double.MaxValue ? double.MaxValue : Math.Abs(x.TargetDistanceSort))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        _dashboardWatchRows.Clear();
        foreach (var row in ordered)
        {
            _dashboardWatchRows.Add(row);
        }

        if (DashboardWatchlistEmpty is not null)
        {
            DashboardWatchlistEmpty.Visibility = _dashboardWatchRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task RefreshAlertsAsync(CancellationToken cancellationToken = default)
    {
        var events = await _services.Repository.GetAlertEventsAsync(250, cancellationToken);
        var names = _watchlistRows.ToDictionary(x => x.ItemKey, x => x.Name);
        _alertRows.Clear();

        foreach (var item in events)
        {
            var itemName = names.TryGetValue(item.ItemKey, out var name) ? name : item.ItemKey.ToString();
            var eventName = item.EventType switch
            {
                AlertEventType.TargetReached => "Target reached",
                AlertEventType.NewTrackedLow => "New tracked low",
                _ => item.EventType.ToString()
            };
            var severity = item.EventType == AlertEventType.TargetReached ? "High" : "Info";
            _alertRows.Add(new AlertRow(
                item.Id,
                item.ItemKey,
                itemName,
                eventName,
                DisplayFormatting.Price(item.NewPrice),
                DisplayFormatting.Price(item.OldPrice),
                severity,
                item.CreatedAtUtc));
        }

        _activityRows.Clear();
        foreach (var alert in _alertRows.Take(6))
        {
            var previous = alert.PreviousPrice == "—" ? "first recorded price" : $"previous {alert.PreviousPrice}";
            _activityRows.Add(new RecentActivityRow(
                alert.CreatedAtUtc.ToLocalTime().ToString("h:mm tt"),
                alert.ItemName,
                alert.Event,
                $"{alert.Price} · {previous}"));
        }

        if (_activityRows.Count == 0)
        {
            _activityRows.Add(new RecentActivityRow("—", "No alert activity yet", "Monitoring", "Target hits and new lows will appear here."));
        }
    }

    private async Task RefreshProviderHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = await _services.Repository.GetProviderHealthAsync(_services.Provider.Name, cancellationToken);
        var remaining = _services.RateGovernor.RemainingDelay;

        if (health is null)
        {
            SetApiStatus("Not checked", "Waiting for first market check", Color.FromRgb(152, 162, 179));
        }
        else
        {
            var color = health.State switch
            {
                "Healthy" => Color.FromRgb(6, 118, 71),
                "RateLimited" => Color.FromRgb(181, 71, 8),
                _ => Color.FromRgb(180, 35, 24)
            };
            var detail = string.IsNullOrWhiteSpace(health.Message)
                ? $"Updated {health.UpdatedAtUtc.ToLocalTime():h:mm tt}"
                : health.Message;
            SetApiStatus(health.State, detail, color);
        }

        HealthBackoffText.Text = remaining > TimeSpan.Zero ? $"{Math.Ceiling(remaining.TotalSeconds):N0}s remaining" : "None";
    }

    private void UpdateDashboard()
    {
        KpiLastRefresh.Text = _lastCheckUtc is { } last ? last.ToLocalTime().ToString("h:mm:ss tt") : "—";
        KpiNextRefresh.Text = _nextCheckUtc is { } next ? $"Next scheduled {next.ToLocalTime():h:mm:ss tt}" : "Next check not scheduled";
        HealthMonitoringText.Text = _monitoringCts is not null ? "Active" : "Paused";
        DataPathText.Text = _services.DataDirectory;
    }

    private void SetApiStatus(string status, string detail, Color color)
    {
        var brush = new SolidColorBrush(color);
        KpiApiStatus.Text = status;
        KpiApiStatus.Foreground = brush;
        KpiApiDetail.Text = detail;
        FooterApiText.Text = $"API: {status}";
        FooterApiDot.Foreground = brush;
        DiagnosticsApiText.Text = string.IsNullOrWhiteSpace(detail) ? status : $"{status} · {detail}";
    }

    private async Task RunCheckAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken))
        {
            if (userInitiated)
            {
                ShowBanner("Check already running", "A marketplace refresh is already in progress.");
            }
            return;
        }

        if (userInitiated)
        {
            CheckNowButton.IsEnabled = false;
            CheckNowButtonText.Text = "Checking...";
        }

        try
        {
            var summary = await _services.Coordinator.CheckOnceAsync(cancellationToken);
            await _services.NotificationDispatcher.DispatchPendingAsync(cancellationToken);
            _lastCheckUtc = DateTimeOffset.UtcNow;

            if (summary.SkippedForBackoff)
            {
                ShowBanner("Check delayed", $"Roblox API backoff is active for about {Math.Ceiling(_services.RateGovernor.RemainingDelay.TotalSeconds):N0} seconds.");
            }
            else if (summary.Failure is not null && userInitiated)
            {
                ShowBanner("Roblox check issue", summary.Failure.Message);
            }

            await RefreshAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when monitoring is paused or the app closes.
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            if (userInitiated)
            {
                ShowBanner("Check failed", ex.Message);
            }
        }
        finally
        {
            if (userInitiated)
            {
                CheckNowButton.IsEnabled = true;
                CheckNowButtonText.Text = "Check Now";
            }
            _checkGate.Release();
        }
    }

    private void ScheduleDelayedStartupMonitoring()
    {
        _startupDelayCts?.Cancel();
        _startupDelayCts?.Dispose();
        var cts = new CancellationTokenSource();
        _startupDelayCts = cts;
        var seconds = Math.Clamp(_services.Settings.StartupDelaySeconds, 0, 120);
        UpdateStartupDelayUi(seconds);
        _ = DelayThenStartMonitoringAsync(cts, seconds);
    }

    private void UpdateStartupDelayUi(int secondsRemaining)
    {
        SidebarMonitorText.Text = secondsRemaining > 0 ? $"Starting in {secondsRemaining}s" : "Starting";
        FooterMonitorText.Text = secondsRemaining > 0 ? $"STARTING IN {secondsRemaining}s" : "STARTING";
        MonitoringButton.Content = "Pause Startup";
        _trayIcon?.UpdateMonitoring(monitoring: false, startupDelay: true, secondsRemaining);
    }

    private void CancelDelayedStartupMonitoring()
    {
        var pending = _startupDelayCts;
        _startupDelayCts = null;
        pending?.Cancel();
        UpdateMonitoringUi(false);
    }

    private async Task DelayThenStartMonitoringAsync(CancellationTokenSource cts, int seconds)
    {
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                UpdateStartupDelayUi(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
            if (cts.IsCancellationRequested || !ReferenceEquals(_startupDelayCts, cts)) return;
            _startupDelayCts = null;
            StartMonitoring();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_startupDelayCts, cts)) _startupDelayCts = null;
            cts.Dispose();
        }
    }
}
